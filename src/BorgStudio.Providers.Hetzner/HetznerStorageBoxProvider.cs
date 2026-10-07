using System.Text.RegularExpressions;
using BorgStudio.Plugins;
using BorgStudio.Providers.Hetzner.Resources;

namespace BorgStudio.Providers.Hetzner;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class HetznerProviderPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new HetznerStorageBoxProvider());
}

/// <summary>
/// Repository on a Hetzner Storage Box. borg runs over SSH on port 23; the box has no regular shell
/// (no redirections), so restricted keys are written over SFTP and unrestricted ones go through
/// Hetzner's <c>install-ssh-key</c>.
/// </summary>
public sealed partial class HetznerStorageBoxProvider : IRepositoryProvider
{
    public const string UserKey = "user";
    public const string PathKey = "path";
    public const string RemoteBorgKey = "remoteBorg";

    /// <summary>Port with SSH commands (borg, rsync, ...); port 22 only offers SFTP and SCP.</summary>
    public const int Port = 23;

    public const string Domain = "your-storagebox.de";

    /// <summary>Hetzner's command that adds a public key read from standard input to the box.</summary>
    public const string InstallKeyCommand = "install-ssh-key";

    /// <summary>
    /// Host key fingerprints of all Storage Box servers (ED25519, RSA, ECDSA), as published at
    /// https://docs.hetzner.com/storage/storage-box/general – update them here if Hetzner changes its keys.
    /// </summary>
    public static IReadOnlyList<string> HostKeyFingerprints { get; } =
    [
        "SHA256:XqONwb1S0zuj5A1CDxpOSuD2hnAArV1A3wKY7Z3sdgM",
        "SHA256:EMlfI8GsRIfpVkoW1H2u0zYVpFGKkIMKHFZIRkf2ioI",
        "SHA256:oDHZqKXnoMtgvPBjjC57pcuFez28roaEuFcfwyg8O5c",
    ];

    /// <summary>The borg versions Hetzner offers that BorgStudio supports (borg 1.1 is too old).</summary>
    public static IReadOnlyList<string> RemoteBorgVersions { get; } = ["borg-1.4", "borg-1.2"];

    public string Id => "hetzner-storage-box";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(UserKey, Strings.UserLabel) { Hint = Strings.UserHint },
        new(PathKey, Strings.PathLabel) { Hint = Strings.PathHint },
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
        var user = Value(values, UserKey);
        var path = ServerPath(Value(values, PathKey));

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
        var user = Value(values, UserKey).ToLowerInvariant();
        var path = ServerPath(Value(values, PathKey));
        var host = $"{user}.{Domain}";

        return new RepositoryLocation($"ssh://{user}@{host}:{Port}/./{path}")
        {
            BorgArguments = [$"--remote-path={Value(values, RemoteBorgKey)}"],
            Ssh = new SshEndpoint(host, Port, user, path)
            {
                KeyInstallation = SshKeyInstallation.Sftp,
                InstallKeyCommand = InstallKeyCommand,
                PublishedHostKeyFingerprints = HostKeyFingerprints,
            },
        };
    }

    /// <summary>The path relative to the box's home directory: "./backups/x/" and "~/backups/x" become "backups/x".</summary>
    private static string ServerPath(string path)
    {
        while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimEnd('/');
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";

    /// <summary>Main account u123456 or sub-account u123456-sub1.</summary>
    [GeneratedRegex("^u[0-9]+(-sub[0-9]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UserName();

    // Ends up unquoted in the restricted key's forced command, so only characters every shell takes literally.
    [GeneratedRegex("^[A-Za-z0-9._+/-]+$")]
    private static partial Regex PlainPath();
}
