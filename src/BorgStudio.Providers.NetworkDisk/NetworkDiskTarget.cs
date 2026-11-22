using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>iSCSI or NVMe over TCP.</summary>
public enum NetworkDiskProtocol
{
    Iscsi,
    NvmeTcp,
}

/// <summary>A network disk to log in to.</summary>
/// <param name="Host">The portal's host name or IP address.</param>
/// <param name="Port">3260 for iSCSI, 4420 for NVMe/TCP by default.</param>
/// <param name="Name">The target's IQN (iSCSI) or NQN (NVMe).</param>
/// <param name="ChapUser">CHAP user (iSCSI); <c>null</c>: no CHAP.</param>
public sealed partial record NetworkDiskTarget(NetworkDiskProtocol Protocol, string Host, int Port, string Name, string? ChapUser)
{
    public const int IscsiPort = 3260;
    public const int NvmePort = 4420;

    /// <summary>
    /// What was set up for this target (and CHAP secret, if given), as kept with the repository – a hash, never the secret.
    /// Without the secret only the part before ":" is compared.
    /// </summary>
    public string SetupMarker(string? chapSecret) =>
        Hash($"{Protocol}|{Host}|{Port}|{Name}|{ChapUser}") + ":" + (chapSecret is { Length: > 0 } ? Hash(chapSecret) : "-");

    public bool IsSetUp(string? marker, string? chapSecret) =>
        marker is { Length: > 0 }
        && (chapSecret is { Length: > 0 } ? marker == SetupMarker(chapSecret) : marker.Split(':')[0] == SetupMarker(null).Split(':')[0]);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>"nas", "nas:3260", "192.168.1.5", "[fd00::5]:4420" → host and port; <c>null</c> if it is not one.</summary>
    public static (string Host, int Port)? Portal(string text, NetworkDiskProtocol protocol)
    {
        var match = PortalPattern().Match(text.Trim());
        if (!match.Success)
            return null;
        var host = match.Groups["host"].Value is { Length: > 0 } plain ? plain : match.Groups["ipv6"].Value;
        var port = protocol == NetworkDiskProtocol.Iscsi ? IscsiPort : NvmePort;
        if (match.Groups["port"].Success
            && (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return null;
        return (host.ToLowerInvariant(), port);
    }

    /// <summary>IQN/NQN characters that are safe on every command line and in sudoers (after escaping ":").</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static bool IsValidChapUser(string user) => ChapUserPattern().IsMatch(user);

    [GeneratedRegex(@"^(?:(?<host>[A-Za-z0-9.-]+)|\[(?<ipv6>[0-9A-Fa-f:]+)\])(?::(?<port>\d+))?$")]
    private static partial Regex PortalPattern();

    [GeneratedRegex(@"^[A-Za-z0-9.:-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._@-]+$")]
    private static partial Regex ChapUserPattern();
}
