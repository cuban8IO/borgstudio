using System.Runtime.InteropServices;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Providers.Server;

namespace BorgStudio.App.Services;

/// <summary>The outcome of pairing: the account, or why it did not work (<c>null</c> both: cancelled).</summary>
public sealed record PairingOutcome(ServerAccount? Account, string? Problem)
{
    public static PairingOutcome Cancelled { get; } = new(null, null);
}

/// <summary>
/// Pairing with BorgStudio Servers and ending it – from the repository editor, the settings and a repository whose
/// pairing was revoked. A new pairing of a known server registers this computer's keys of its repositories again (a
/// revoked pairing took them along).
/// </summary>
/// <param name="save">Keeps a repository whose values changed (the main window keeps its list); default: straight into the store.</param>
public sealed class ServerConnections(AppServices services, IDialogService dialogs, Action<RepositoryConfig>? save = null)
{
    public ServerProvider Provider { get; } = services.Server ?? throw new InvalidOperationException("The BorgStudio Server provider is not registered.");

    public ServerAccounts Accounts => Provider.Accounts;

    /// <summary>Asks for the server's address and pairs with it.</summary>
    public async Task<PairingOutcome> PairNewAsync()
    {
        var address = await dialogs.AskTextAsync(Strings.PairingTitle, Strings.PairingAddressPrompt, "https://");
        return string.IsNullOrWhiteSpace(address) || address.Trim() == "https://" ? PairingOutcome.Cancelled : await PairAsync(address);
    }

    /// <summary>
    /// Pairs with the server at <paramref name="address"/>: first contact (the certificate confirmed if the system does
    /// not trust it), the code confirmed in the browser, the token into the vault.
    /// </summary>
    public async Task<PairingOutcome> PairAsync(string address)
    {
        if (ServerAddress.Normalize(address) is not { } url)
            return new PairingOutcome(null, Strings.PairingAddressInvalid);
        var existing = Accounts.Find(url);

        if (ServerAddress.IsUnencrypted(url)
            && !await dialogs.ConfirmAsync(Strings.PairingTitle, BorgTexts.Format(Strings.PairingUnencrypted, ServerAddress.Display(url)), Strings.PairingContinue))
            return PairingOutcome.Cancelled;

        ServerProbe probe;
        try
        {
            probe = await Provider.Http.ProbeAsync(url, CancellationToken.None);
        }
        catch (ServerException exception)
        {
            return new PairingOutcome(null, exception.Message);
        }

        if (probe.Certificate is { } certificate && !string.Equals(certificate.Sha256, existing?.PinnedCertificate, StringComparison.OrdinalIgnoreCase))
        {
            var message = BorgTexts.Format(existing?.PinnedCertificate is null ? Strings.PairingCertificate : Strings.PairingCertificateChanged,
                ServerAddress.Display(url), certificate.Subject, certificate.NotAfter.ToLocalTime().ToString("d"), certificate.Fingerprint);
            if (!await dialogs.ConfirmAsync(Strings.PairingCertificateTitle, message, Strings.PairingCertificateTrust))
                return PairingOutcome.Cancelled;
        }

        // The token goes into the vault: open (or set up) before the server is bothered.
        if (!await VaultAccess.EnsureUnlockedAsync(services.Vault, dialogs))
            return new PairingOutcome(null, Strings.VaultNotUnlocked);

        var pin = probe.Certificate?.Sha256;
        var anonymous = new ServerClient(Provider.Http, url, null, pin);
        ServerPairingViewModel pairing;
        try
        {
            var start = await anonymous.StartPairingAsync(Environment.MachineName, RuntimeInformation.OSDescription, AppVersion.Current, CancellationToken.None);
            pairing = new ServerPairingViewModel(anonymous, start, dialogs);
            await dialogs.ShowServerPairingAsync(pairing);
        }
        catch (ServerException exception)
        {
            return new PairingOutcome(null, exception.Message);
        }
        if (pairing.Result?.Token is not { } token)
            // Denied or expired on the server: told again where the pairing started; closed by the user: nothing to tell.
            return pairing.HasProblem && !pairing.IsWaiting ? new PairingOutcome(null, pairing.Status) : PairingOutcome.Cancelled;

        ServerInfo info;
        try
        {
            info = await new ServerClient(Provider.Http, url, token, pin).GetServerAsync(CancellationToken.None);
        }
        catch (ServerException exception)
        {
            return new PairingOutcome(null, exception.Message);
        }

        var account = new ServerAccount
        {
            Url = url,
            Name = info.Name,
            UserName = info.UserName,
            DeviceId = info.DeviceId,
            PinnedCertificate = pin,
            PairedAt = DateTimeOffset.Now,
        };
        try
        {
            Accounts.Save(account, token);
        }
        catch (VaultLockedException)
        {
            return new PairingOutcome(null, Strings.VaultNotUnlocked);
        }

        // Paired again: the keys of this computer's repositories there went with the old pairing.
        var problems = await RegisterKeysAsync(account);
        return new PairingOutcome(account, problems.Count > 0 ? string.Join("\n", problems) : null);
    }

    /// <summary>The repositories of this computer on the server.</summary>
    public IReadOnlyList<RepositoryConfig> RepositoriesOn(string url) =>
        [.. services.RepositoryStore.Load().Repositories.Where(repository => repository.ProviderId == ServerProvider.ProviderId
            && ServerAddress.Normalize(repository.ProviderValues.GetValueOrDefault(ServerProvider.ServerKey)) == url)];

    /// <summary>
    /// Registers the login key of each of this computer's repositories on the server again (after a new pairing) and keeps
    /// the address the server gives; the problems, one line per repository.
    /// </summary>
    public async Task<IReadOnlyList<string>> RegisterKeysAsync(ServerAccount account)
    {
        var problems = new List<string>();
        var repositories = RepositoriesOn(account.Url);
        if (repositories.Count == 0)
            return problems;
        ServerClient client;
        try
        {
            client = Provider.ClientFor(account.Url);
        }
        catch (ServerException exception)
        {
            return [exception.Message];
        }

        foreach (var repository in repositories)
        {
            try
            {
                if (!Guid.TryParse(repository.ProviderValues.GetValueOrDefault(ServerProvider.RepositoryIdKey), out var id)
                    || repository.SshKeyId is not { } keyId
                    || services.Vault.Keys.FirstOrDefault(key => key.Id == keyId) is not { } key)
                    continue;
                var registration = await client.SetSshKeyAsync(id, key.PublicKey, CancellationToken.None);
                var values = new Dictionary<string, string>(repository.ProviderValues)
                {
                    [ServerProvider.AddressKey] = registration.Url,
                    [ServerProvider.HostKeysKey] = string.Join(' ', registration.Ssh.HostKeys.Select(hostKey => hostKey.Fingerprint)),
                };
                Save(repository with { ProviderValues = values });
            }
            catch (Exception exception) when (exception is ServerException or VaultLockedException)
            {
                problems.Add($"{repository.Name}: {exception.Message}");
            }
        }
        return problems;
    }

    private void Save(RepositoryConfig repository)
    {
        if (save is not null)
        {
            save(repository);
            return;
        }
        var store = services.RepositoryStore;
        store.Save(store.Load().Repositories.Select(existing => existing.Id == repository.Id ? repository : existing));
    }

    /// <summary>
    /// Ends the pairing after asking: on the server (this computer's token and keys there, if it can be reached) and here.
    /// <c>false</c> if the user kept it.
    /// </summary>
    public async Task<bool> UnpairAsync(ServerAccount account)
    {
        var used = RepositoriesOn(account.Url).Count;
        var message = BorgTexts.Format(used > 0 ? Strings.UnpairUsed : Strings.UnpairMessage, account.ToString(), used);
        if (!await dialogs.ConfirmAsync(Strings.UnpairTitle, message, Strings.UnpairConfirm))
            return false;
        // The token is in the vault: needed to end the access on the server, and the vault changes.
        if (!await VaultAccess.EnsureUnlockedAsync(services.Vault, dialogs))
            return false;
        try
        {
            await Provider.ClientFor(account.Url).UnpairAsync(CancellationToken.None);
        }
        catch (ServerException)
        {
            // Not reachable: it stays listed under "Devices" on the server, where it can be revoked.
        }
        Accounts.Remove(account.Url);
        return true;
    }
}
