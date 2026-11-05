using BorgStudio.App.Resources;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using BorgStudio.Core.Transfer;

namespace BorgStudio.App.ViewModels;

/// <summary>Collects everything for an export, and applies an import.</summary>
public static class SettingsTransfer
{
    /// <param name="Problems">What could not be included, e.g. a borg key of an unreachable repository.</param>
    public sealed record ExportResult(ExportContents Contents, IReadOnlyList<string> Problems);

    /// <param name="Problems">What did not work out, e.g. a missing plugin.</param>
    public sealed record ImportResult(int Repositories, int Keys, int Passphrases, IReadOnlyList<string> Problems);

    /// <summary>Everything to export: repositories, stored passphrases, SSH keys, known hosts, settings and – if asked – borg keys.</summary>
    /// <param name="progress">Called with the repository whose borg key is being fetched.</param>
    public static async Task<ExportResult> CollectAsync(MainViewModel main, bool includeBorgKeys, Action<string>? progress = null)
    {
        var services = main.Services;
        var problems = new List<string>();
        var vaultOpen = services.Vault.State == VaultState.Unlocked;
        var passphrases = new Dictionary<string, string>();

        foreach (var item in main.Repositories)
        {
            var repository = item.Config;
            try
            {
                var passphrase = repository.PassphraseMode switch
                {
                    PassphraseMode.Vault when vaultOpen => services.Vault.GetPassphrase(repository.Id),
                    PassphraseMode.Stored => services.SecretStore.Get(repository.SecretKey),
                    _ => null,
                };
                if (passphrase is not null)
                    passphrases[repository.Id.ToString("N")] = passphrase;
            }
            catch (SecretStoreException exception)
            {
                problems.Add($"{repository.Name}: {BorgTexts.Format(Strings.KeychainError, exception.Message)}");
            }
        }

        var borgKeys = new Dictionary<string, ExportedBorgKey>();
        if (includeBorgKeys)
        {
            foreach (var item in main.Repositories.Where(item => item.IsEncrypted && item.Config.EncryptionMode is not null))
            {
                progress?.Invoke(item.Name);
                var (key, error) = await main.ExportBorgKeyAsync(item);
                if (key is not null)
                    borgKeys[item.Config.Id.ToString("N")] = new ExportedBorgKey(key, item.IsKeyfile);
                else
                    problems.Add(BorgTexts.Format(Strings.TransferBorgKeyFailed, item.Name, error ?? ""));
            }
        }

        var contents = new ExportContents
        {
            Created = DateTimeOffset.Now,
            AppVersion = AppVersion.Current,
            Machine = Environment.MachineName,
            Repositories = [.. main.Repositories.Select(item => item.Config)],
            Passphrases = passphrases,
            SshKeys = vaultOpen ? [.. services.Vault.Keys] : [],
            KnownHosts = new KnownHostsFile(services.SshKeys.KnownHostsFile).Read(),
            Settings = main.Settings,
            BorgKeys = borgKeys,
        };
        return new ExportResult(contents, problems);
    }

    /// <summary>
    /// Applies an export. The vault must be open (keys and passphrases go there). With <see cref="ImportMode.Merge"/>,
    /// existing repositories and keys are replaced only if their id is in <paramref name="replace"/>.
    /// </summary>
    /// <exception cref="VaultLockedException" />
    public static async Task<ImportResult> ApplyAsync(MainViewModel main, ExportContents contents, ImportMode mode,
        IReadOnlySet<Guid> replace)
    {
        var services = main.Services;
        var vault = services.Vault;
        if (vault.State != VaultState.Unlocked)
            throw new VaultLockedException();

        var problems = new List<string>();
        var store = new PassphraseStore(services);
        var current = main.Repositories.Select(item => item.Config).ToList();
        var currentById = current.ToDictionary(repository => repository.Id);

        bool Applies(Guid id, bool exists) => mode == ImportMode.Replace || !exists || replace.Contains(id);

        // Replacing everything: what is not in the export goes, including its stored passphrases and all keys.
        if (mode == ImportMode.Replace)
        {
            foreach (var repository in current)
                if (store.Delete(repository, repository.PassphraseMode) is { } error)
                    problems.Add($"{repository.Name}: {error}");
            foreach (var key in vault.Keys)
                vault.RemoveKey(key.Id);
        }

        // SSH keys.
        var currentKeys = vault.Keys.Select(key => key.Id).ToHashSet();
        var keys = 0;
        foreach (var key in contents.SshKeys.Where(key => Applies(key.Id, currentKeys.Contains(key.Id))))
        {
            vault.SetKey(key);
            keys++;
        }

        // Repositories and their passphrases.
        var applied = contents.Repositories.Where(repository => Applies(repository.Id, currentById.ContainsKey(repository.Id))).ToList();
        var adjusted = new Dictionary<Guid, RepositoryConfig>();
        var passphrases = 0;
        foreach (var repository in applied)
        {
            var target = repository;
            // Keep it where the export kept it; without a keychain here, the vault takes it.
            if (repository.PassphraseMode == PassphraseMode.Stored && !services.SecretStore.IsAvailable)
                target = adjusted[repository.Id] = repository with { PassphraseMode = PassphraseMode.Vault };

            if (mode == ImportMode.Merge && currentById.TryGetValue(repository.Id, out var old) && old.PassphraseMode != target.PassphraseMode)
                store.Delete(old, old.PassphraseMode);

            if (contents.PassphraseFor(repository.Id) is { } passphrase && target.PassphraseMode is PassphraseMode.Vault or PassphraseMode.Stored)
            {
                if (await store.SetAsync(target, target.PassphraseMode, passphrase, services.Dialogs) is { } error)
                    problems.Add($"{repository.Name}: {error}");
                else
                    passphrases++;
            }

            if (services.Plugins.FindRepositoryProvider(repository.ProviderId) is null)
                problems.Add(BorgTexts.Format(Strings.TransferProviderMissing, repository.Name, repository.ProviderId));
        }

        var imported = contents.Repositories.Select(repository => adjusted.GetValueOrDefault(repository.Id) ?? repository).ToList();
        main.ReplaceRepositories(ImportMerge.Merge(current, imported, repository => repository.Id, mode, replace));

        // Known hosts and settings.
        var knownHosts = new KnownHostsFile(services.SshKeys.KnownHostsFile);
        if (mode == ImportMode.Replace)
        {
            knownHosts.Replace(contents.KnownHosts);
            main.UpdateSettings(contents.Settings);
        }
        else
        {
            knownHosts.Merge(contents.KnownHosts);
        }

        // Keyfile keys exist only on a computer: put them where borg looks.
        foreach (var repository in applied)
        {
            if (contents.BorgKeyFor(repository.Id) is { Keyfile: true } borgKey
                && await main.InstallBorgKeyfileAsync(repository, borgKey.Key) is { } error)
                problems.Add(BorgTexts.Format(Strings.TransferKeyfileFailed, repository.Name, error));
        }

        return new ImportResult(applied.Count, keys, passphrases, problems);
    }
}
