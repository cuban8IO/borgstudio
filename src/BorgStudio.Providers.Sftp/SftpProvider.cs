using System.Globalization;
using System.Text.RegularExpressions;
using BorgStudio.Plugins;
using BorgStudio.Providers.Sftp.Resources;

namespace BorgStudio.Providers.Sftp;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class SftpProviderPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new SftpProvider());
}

/// <summary>
/// Repository on a server that only offers SFTP (no borg there). BorgStudio mounts the folder holding it with sshfs and
/// borg works on it like on a local folder (<see cref="RepositoryLocation.MountOverSftp"/>). The login key is added to
/// <c>~/.ssh/authorized_keys</c> over SFTP with the password; it cannot be restricted to borg serve.
/// </summary>
public sealed partial class SftpProvider : IRepositoryProvider
{
    public const string HostKey = "host";
    public const string PortKey = "port";
    public const string UserKey = "user";
    public const string PathKey = "path";

    public string Id => "sftp";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(HostKey, Strings.HostLabel) { Hint = Strings.HostHint },
        new(PortKey, Strings.PortLabel, ProviderFieldKind.Number) { DefaultValue = "22" },
        new(UserKey, Strings.UserLabel),
        new(PathKey, Strings.PathLabel) { Hint = Strings.PathHint },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        var host = Value(values, HostKey);
        if (host.Length == 0)
            errors.Add(Strings.HostRequired);
        else if (!HostName().IsMatch(host))
            errors.Add(Strings.HostInvalid);

        if (Port(values) is null)
            errors.Add(Strings.PortInvalid);

        var user = Value(values, UserKey);
        if (user.Length == 0)
            errors.Add(Strings.UserRequired);
        else if (!UserName().IsMatch(user))
            errors.Add(Strings.UserInvalid);

        var path = ServerPath(Value(values, PathKey));
        if (path.Length == 0)
            errors.Add(Strings.PathRequired);
        else if (path.Split('/').Any(segment => segment == "..") || path.Trim('/').Length == 0)
            errors.Add(Strings.PathInvalid);
        return errors;
    }

    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values)
    {
        var host = Value(values, HostKey).ToLowerInvariant();
        var port = Port(values) ?? 22;
        var user = Value(values, UserKey);
        var path = ServerPath(Value(values, PathKey));
        return new RepositoryLocation($"sftp://{user}@{host}:{port}/{path.TrimStart('/')}")
        {
            MountOverSftp = true,
            Ssh = new SshEndpoint(host, port, user, path) { KeyInstallation = SshKeyInstallation.Sftp },
        };
    }

    private static int? Port(IReadOnlyDictionary<string, string> values) =>
        int.TryParse(Value(values, PortKey) is { Length: > 0 } text ? text : "22", NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is > 0 and < 65536 ? port : null;

    /// <summary>"./backups/x/" and "~/backups/x" become "backups/x"; absolute paths stay absolute.</summary>
    private static string ServerPath(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
            path = path[2..];
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";

    [GeneratedRegex(@"^[A-Za-z0-9.-]+$|^\[[0-9A-Fa-f:]+\]$")]
    private static partial Regex HostName();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex UserName();
}
