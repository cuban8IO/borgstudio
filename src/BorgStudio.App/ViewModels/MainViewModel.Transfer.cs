using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;

namespace BorgStudio.App.ViewModels;

/// <summary>What exports and imports need from the main window.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// The repository key as <c>borg key export</c> writes it; otherwise the reason. Uses the repository's login and
    /// may ask to confirm an unknown server.
    /// </summary>
    public async Task<(string? Key, string? Error)> ExportBorgKeyAsync(RepositoryItemViewModel item)
    {
        if (Borg is not { Support: BorgSupport.Supported } borg)
            return (null, Strings.BorgNotReady);
        if (item.Location is not { } location || RepositoryFormat.Resolve(item.Config.BorgVersion, location) == 2)
            return (null, Strings.Borg2NotSupported);
        // Without an open vault there is no SSH key – and an export must not start setting up a PIN.
        if (location.Ssh is not null && _services.Vault.State != Core.Security.VaultState.Unlocked)
            return (null, Strings.SshKeyMissing);
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return (null, item.ResultText ?? Strings.SshKeyMissing);

        var file = Path.Combine(Path.GetTempPath(), "borgstudio-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await _services.BorgClient.ExportKeyAsync(borg, location, file, ssh.Access);
            if (!result.Succeeded)
                return (null, BorgTexts.Describe(result.Error!));
            return File.Exists(file) ? (await File.ReadAllTextAsync(file), null) : (null, Strings.TransferBorgKeyMissing);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Puts a keyfile key back where borg looks for it (on this computer, inside WSL if borg runs there).</summary>
    public async Task<string?> InstallBorgKeyfileAsync(RepositoryConfig repository, string key)
    {
        if (Borg is not { Support: BorgSupport.Supported } borg)
            return Strings.BorgNotReady;
        if (repository.BorgRepositoryId is not { } repositoryId)
            return Strings.TransferBorgKeyMissing;
        var result = await _services.BorgClient.InstallKeyfileAsync(borg, repositoryId, key);
        return result.Succeeded ? null : BorgTexts.Describe(result.Error!);
    }

    /// <summary>Shows and saves <paramref name="repositories"/> instead of the current list (imports).</summary>
    public void ReplaceRepositories(IEnumerable<RepositoryConfig> repositories)
    {
        var selected = SelectedRepository?.Config.Id;
        Repositories.Clear();
        foreach (var repository in repositories)
            Repositories.Add(new RepositoryItemViewModel(repository, Plugins));
        SaveRepositories();
        _services.Archives.RemoveAllExcept(Repositories.Select(item => item.Config.Id));
        // The same id may now point somewhere else: the archives tab checks with borg again.
        _archivesOf = null;
        SelectedRepository = Repositories.FirstOrDefault(item => item.Config.Id == selected) ?? Repositories.FirstOrDefault();
        RefreshKeyNames();
    }
}
