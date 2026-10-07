using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;

namespace BorgStudio.App.ViewModels;

/// <summary>Host key checks with the user's confirmation, and texts for SSH problems.</summary>
internal static class SshTrustDialog
{
    /// <summary>The trusted host key, or the reason (for the user) why the server is not trusted.</summary>
    public static async Task<(SshHostKey? Key, string? Problem)> EnsureTrustedAsync(
        SshHostTrust trust, SshEndpoint endpoint, IDialogService dialogs)
    {
        try
        {
            var server = endpoint.Port == 22 ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
            var (result, key) = await trust.EnsureTrustedAsync(endpoint.Host, endpoint.Port, endpoint.PublishedHostKeyFingerprints,
                (presented, notPublished) => dialogs.ConfirmAsync(Strings.HostKeyTitle,
                    BorgTexts.Format(notPublished ? Strings.HostKeyNotPublished : Strings.HostKeyMessage,
                        server, presented.Type, presented.Fingerprint),
                    Strings.HostKeyTrust));

            return result switch
            {
                HostTrust.Trusted => (key, null),
                HostTrust.Changed => (null, BorgTexts.Format(Strings.HostKeyChanged, server, trust.KnownHosts.Path)),
                _ => (null, Strings.HostKeyRejected),
            };
        }
        catch (SshOperationException exception)
        {
            return (null, Describe(exception));
        }
    }

    public static string Describe(SshOperationException exception) => exception.Kind switch
    {
        SshFailureKind.AuthenticationFailed => Strings.SshLoginFailed,
        SshFailureKind.HostKeyChanged => Strings.SshHostKeyChangedDuringLogin,
        SshFailureKind.CommandFailed => BorgTexts.Format(Strings.SshInstallFailed, exception.Message),
        _ => BorgTexts.Format(Strings.SshConnectionFailed, exception.Message),
    };

    /// <summary>The borg command on the server: the provider's --remote-path, otherwise "borg".</summary>
    public static string RemoteBorg(RepositoryLocation location) =>
        location.BorgArguments.FirstOrDefault(argument => argument.StartsWith("--remote-path=", StringComparison.Ordinal))
            ?["--remote-path=".Length..] ?? "borg";
}
