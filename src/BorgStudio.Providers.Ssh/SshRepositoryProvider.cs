using System.Globalization;
using BorgStudio.Plugins;
using BorgStudio.Providers.Ssh.Resources;

namespace BorgStudio.Providers.Ssh;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class SshProviderPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new SshRepositoryProvider());
}

/// <summary>Repository on an SSH server that has borg installed.</summary>
public sealed class SshRepositoryProvider : IRepositoryProvider
{
    public const string HostKey = "host";
    public const string PortKey = "port";
    public const string UserKey = "user";
    public const string PathKey = "path";
    public const string RemoteBorgKey = "remoteBorg";

    // These values end up in ssh URLs and authorized_keys lines: no quotes, escapes, variables or line breaks.
    private static readonly char[] ForbiddenInPaths = ['"', '\'', '\\', '$', '`', '\n', '\r'];

    public string Id => "ssh";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(HostKey, Strings.HostLabel) { Hint = Strings.HostHint },
        new(PortKey, Strings.PortLabel, ProviderFieldKind.Number) { DefaultValue = "22" },
        new(UserKey, Strings.UserLabel),
        new(PathKey, Strings.PathLabel) { Hint = Strings.PathHint },
        new(RemoteBorgKey, Strings.RemoteBorgLabel) { Required = false, Hint = Strings.RemoteBorgHint },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        var host = Value(values, HostKey);
        var user = Value(values, UserKey);
        var path = Value(values, PathKey);
        var remoteBorg = Value(values, RemoteBorgKey);

        if (host.Length == 0)
            errors.Add(Strings.HostRequired);
        else if (host.Any(c => char.IsWhiteSpace(c) || c is '@' or '/' or '\'' or '"'))
            errors.Add(Strings.HostInvalid);

        if (!int.TryParse(Value(values, PortKey), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            errors.Add(Strings.PortInvalid);

        if (user.Length == 0)
            errors.Add(Strings.UserRequired);
        else if (user.Any(c => char.IsWhiteSpace(c) || c is '@' or ':' or '/' or '\'' or '"'))
            errors.Add(Strings.UserInvalid);

        if (path.Length == 0)
            errors.Add(Strings.PathRequired);
        else if (path.IndexOfAny(ForbiddenInPaths) >= 0 || path.StartsWith('-'))
            errors.Add(Strings.PathInvalid);

        if (remoteBorg.Length > 0 && (remoteBorg.IndexOfAny(ForbiddenInPaths) >= 0 || remoteBorg.Any(char.IsWhiteSpace)))
            errors.Add(Strings.RemoteBorgInvalid);

        return errors;
    }

    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values)
    {
        var host = Value(values, HostKey);
        var port = int.Parse(Value(values, PortKey), CultureInfo.InvariantCulture);
        var user = Value(values, UserKey);
        var path = Value(values, PathKey);
        var remoteBorg = Value(values, RemoteBorgKey);

        // borg: "/abs/path" is absolute, "/./rel/path" and "/~/rel/path" are relative to the home directory.
        var serverPath = path.StartsWith("~/", StringComparison.Ordinal) ? path[2..] : path;
        var urlPath = serverPath.StartsWith('/') ? serverPath : "/./" + serverPath;
        var urlHost = host.Contains(':') ? $"[{host}]" : host;

        return new RepositoryLocation($"ssh://{user}@{urlHost}:{port}{urlPath}")
        {
            BorgArguments = remoteBorg.Length > 0 ? [$"--remote-path={remoteBorg}"] : [],
            Ssh = new SshEndpoint(host, port, user, serverPath),
        };
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";
}
