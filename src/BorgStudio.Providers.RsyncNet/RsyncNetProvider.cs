using System.Globalization;
using System.Text.RegularExpressions;
using BorgStudio.Plugins;
using BorgStudio.Providers.RsyncNet.Resources;

namespace BorgStudio.Providers.RsyncNet;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class RsyncNetProviderPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new RsyncNetProvider());
}

/// <summary>
/// Repository at rsync.net. borg runs on their server (<c>borg14</c> or <c>borg12</c> via <c>--remote-path</c>); there is
/// no interactive shell, so login keys are appended with <c>dd … oflag=append</c> as rsync.net documents. The account's
/// quota comes from their <c>quota</c> command (only with a key that is not restricted to borg).
/// </summary>
/// <remarks>
/// Built from rsync.net's documentation (https://www.rsync.net/resources/howto/remote_commands.html, ssh_keys.html,
/// fingerprints.txt) without a test account: the output of <c>quota</c> is read tolerantly, and whether rsync.net accepts
/// keys restricted with <c>command="borg14 serve …"</c> is not documented.
/// </remarks>
public sealed partial class RsyncNetProvider : IRepositoryProvider, IStorageQuotaSource
{
    public const string HostKey = "host";
    public const string UserKey = "user";
    public const string PathKey = "path";
    public const string RemoteBorgKey = "remoteBorg";

    public const int Port = 22;

    public const string Domain = "rsync.net";

    /// <summary>Appends standard input to authorized_keys, as rsync.net documents for adding keys.</summary>
    public const string AppendKeyCommand = "dd of=.ssh/authorized_keys oflag=append conv=notrunc";

    /// <summary>The borg commands rsync.net offers that BorgStudio supports.</summary>
    public static IReadOnlyList<string> RemoteBorgVersions { get; } = ["borg14", "borg12"];

    /// <summary>
    /// Host key fingerprints of rsync.net's servers (ED25519, ECDSA, RSA, DSA), as published (PGP-signed) at
    /// https://www.rsync.net/resources/fingerprints.txt – update them here if rsync.net changes its keys.
    /// </summary>
    public static IReadOnlyList<string> HostKeyFingerprints { get; } =
    [
        "SHA256:LjBv6uB0wLTgCpHlJ7N30bjoGXGGSf+qHpSLHsBzYfM",
        "SHA256:7RsNtjKlO7rzmmHarZzDRlpCeXdubDdhOeo6RhYWUCA",
        "SHA256:TiW/iOCgJ4l6LCIm5QpBmkvfQnPrtvwWxNl0KATAxpA",
        "SHA256:DBW4gxagH9Q3Avnus+dxaoOS5L/Q/tZlT42bcoMp+4Y",
        "SHA256:oZoWD+zlFmNiMo5vO/Ck4sGdY2Z+4lRefuhj8BthfAM",
        "SHA256:jJCISHUXooV9/rh7kSeqiSuPvOVWBFCOe2CNkwTO46o",
        "SHA256:ILHbyihxk7ztdVyyMhc7hblWwADryBPPA6oDkXRjtgg",
        "SHA256:4oyCIuk+EWE+IQ+KHAakU+EFNl9nAPncFkj5bYz3g28",
        "SHA256:TUGzDVWHRv79NIbeOcrypVgLl4pSMkpHTLlGj0SYXlY",
        "SHA256:1aBuKamW8gLQPz0iqUZI3ut1dqiSlWtLJucjwoP0UDg",
        "SHA256:tYhFqk1ZUG4fZGreIaJ+n3YXJ49PNEC/IJspl3nZxeQ",
        "SHA256:G4hq1a+D2he0uy43fYYFp3F3FXiSFmVFdJiwQYb/UzQ",
        "SHA256:f3O8IiOmKb4bqpS+/b19mWGHhdlweirz3j3bkqbujNo",
        "SHA256:CxrQxQE9l1IICgEkP27s5h596gKIZWrQDccVapeusd8",
        "SHA256:fsczTmRuri4D+5TZUN/2j5AeRhvAyekWRBl6dlLjnkI",
        "SHA256:1Z1TGkiiS5Aucq7lVVvjZNvhRBjoChjvmNpmTkQjjYE",
        "SHA256:U0YxmBNWaeueFEcW7OAbI7LcoMWMlpdH6oQqRtVVnI4",
        "SHA256:Ai4DscKx6Pi3BOcqASTRD2dZRepa/6YqxpcAOw/DY1U",
        "SHA256:FkT4HOSrzHY67gaA9sHD8JHVqtHey12A98C5SZo78zw",
        "SHA256:2iP8PhmyJY4WrELs//xTB2DbfBbwuPiiD4EZYVoEaJM",
        "SHA256:QmTdaDvDruUYqPNvPPyh4Yk0cJxnCHUXDqN2q0Oo+oc",
        "SHA256:LDk8HBZi7vL4Ueb0Dait4ljfYnitAV2MO3L9RFK7PJA",
        "SHA256:V1KWWy9NTeWU3VfsQPuNGsdW2sjEXOkwwyUp+9NaNyc",
        "SHA256:j+MKa6HbcYnTvuVosZtT013sOYDMxXC5Z9dy/6lp+9Q",
        "SHA256:w/Qo2y2QnaRRKUCGVQzd7Xosb4GS2jMXFdu0gxHl0j0",
        "SHA256:RODvG/ixr/m7nd77K6dYrNKl+JaDJfsOIDz6YWszHrs",
        "SHA256:UGxq2Dqi52Zu/pNfkLqkrQdXHIqJnOF5F3D0zD+Oywc",
        "SHA256:SdHDNH7JXIJCQloRz6v9l6IMsDCshQ7gpp6PByHKVII",
        "SHA256:VFQ/u4pzBiTzR9xGkbvi+McfDvTs+GLTVq8rncjLd9k",
        "SHA256:XmOMV1ZaZ/qKRIehihU5hHVFqfbEuuTeIDX2h8iM0aA",
        "SHA256:aDNHfL1BivW6XWQ5ni1JgHCT3nMGNzzXM2zpGbNxr9M",
        "SHA256:ZgHI92oPwu+HpSnOC0aEPLLUNhOYwAZGHUNGsYAiJUM",
        "SHA256:hjRZ3ruPfwyZePA+lUZkRSCV+3kJtyt9f4ICZh+af9M",
        "SHA256:/84+cljL3nxk+35Cu0QGReAEdU0haiP/IdukWvTov74",
        "SHA256:G9x6REwC0jOiUlDc9X8+zWLtcb7gsrtkv7DVSrr6n3E",
        "SHA256:H0QWvTjDO4g1LCpEcKqZbgdEidlDjbdBz2N6gWhHNYc",
    ];

    public string Id => "rsync-net";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(HostKey, Strings.HostLabel) { Hint = Strings.HostHint },
        new(UserKey, Strings.UserLabel) { Hint = Strings.UserHint },
        new(PathKey, Strings.PathLabel) { Hint = Strings.PathHint, DefaultValue = "borg" },
        new(RemoteBorgKey, Strings.RemoteBorgLabel, ProviderFieldKind.Choice)
        {
            DefaultValue = RemoteBorgVersions[0],
            Hint = Strings.RemoteBorgHint,
            Options = [new(RemoteBorgVersions[0], Strings.Borg14), new(RemoteBorgVersions[1], Strings.Borg12)],
        },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        var host = Value(values, HostKey);
        var user = Value(values, UserKey);
        var path = ServerPath(Value(values, PathKey));

        if (host.Length == 0)
            errors.Add(Strings.HostRequired);
        else if (!HostName().IsMatch(host))
            errors.Add(Strings.HostInvalid);

        if (user.Length == 0)
            errors.Add(Strings.UserRequired);
        else if (!UserName().IsMatch(user))
            errors.Add(Strings.UserInvalid);

        if (path.Length == 0)
            errors.Add(Strings.PathRequired);
        else if (path.StartsWith('/'))
            errors.Add(Strings.PathNotRelative);
        else if (!PlainPath().IsMatch(path) || path.Split('/').Any(segment => segment is "" or ".." || segment.StartsWith('-')))
            errors.Add(Strings.PathInvalid);

        if (!RemoteBorgVersions.Contains(Value(values, RemoteBorgKey)))
            errors.Add(Strings.RemoteBorgInvalid);
        return errors;
    }

    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values)
    {
        var host = FullHost(Value(values, HostKey));
        var user = Value(values, UserKey);
        var path = ServerPath(Value(values, PathKey));

        return new RepositoryLocation($"ssh://{user}@{host}:{Port}/./{path}")
        {
            BorgArguments = [$"--remote-path={Value(values, RemoteBorgKey)}"],
            Ssh = new SshEndpoint(host, Port, user, path)
            {
                KeyInstallation = SshKeyInstallation.Command,
                InstallKeyCommand = AppendKeyCommand,
                PublishedHostKeyFingerprints = HostKeyFingerprints,
            },
        };
    }

    public async Task<StorageQuota?> GetQuotaAsync(StorageQuotaRequest request, CancellationToken cancellationToken) =>
        request.RunCommandAsync is { } run && await run("quota", cancellationToken) is { } output ? ParseQuota(output) : null;

    /// <summary>
    /// Reads the output of rsync.net's <c>quota</c> (not documented): a header naming the columns – usage first, then the
    /// quota – and a line with the numbers. Units come from the numbers ("12.3G") or the header ("(GB)", "KB", "MB");
    /// plain numbers without a unit are 1K blocks, as FreeBSD's quota counts.
    /// </summary>
    public static StorageQuota? ParseQuota(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = Array.FindIndex(lines, line => line.Contains("quota", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("usage", StringComparison.OrdinalIgnoreCase) || line.Contains("used", StringComparison.OrdinalIgnoreCase)
                || line.Contains("blocks", StringComparison.OrdinalIgnoreCase)));
        if (header < 0)
            return null;
        var headerUnit = HeaderUnit().Match(lines[header]) is { Success: true } unit ? Multiplier(unit.Groups["unit"].Value) : 1024;

        foreach (var line in lines.Skip(header + 1))
        {
            var numbers = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => Amount().Match(token.TrimEnd('*')))
                .Where(match => match.Success)
                .Select(match => (long)(double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)
                    * (match.Groups["unit"].Success ? Multiplier(match.Groups["unit"].Value) : headerUnit)))
                .ToList();
            if (numbers.Count >= 2 && numbers[1] > 0)
                return new StorageQuota(numbers[0], numbers[1]);
        }
        return null;
    }

    private static long Multiplier(string unit) => char.ToUpperInvariant(unit[0]) switch
    {
        'K' => 1L << 10,
        'M' => 1L << 20,
        'G' => 1L << 30,
        'T' => 1L << 40,
        _ => 1,
    };

    /// <summary>"zh1234" or "zh1234.rsync.net" → "zh1234.rsync.net".</summary>
    private static string FullHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.EndsWith("." + Domain, StringComparison.Ordinal) ? host : $"{host}.{Domain}";
    }

    /// <summary>The path relative to the home folder: "./borg/x/" and "~/borg/x" become "borg/x".</summary>
    private static string ServerPath(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimEnd('/');
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";

    [GeneratedRegex(@"^[a-z0-9-]+(\.rsync\.net)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex UserName();

    // Ends up unquoted in the restricted key's forced command, so only characters every shell takes literally.
    [GeneratedRegex("^[A-Za-z0-9._+/-]+$")]
    private static partial Regex PlainPath();

    [GeneratedRegex(@"^(?<number>\d+(\.\d+)?)(?<unit>[KMGT]i?B?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();

    [GeneratedRegex(@"\b(?<unit>[KMGT])i?B\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderUnit();
}
