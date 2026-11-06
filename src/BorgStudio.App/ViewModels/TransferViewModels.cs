using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Security;
using BorgStudio.Core.Transfer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The export dialog: a password of its own, borg keys or not, then the file.</summary>
public sealed partial class ExportViewModel(MainViewModel main, IDialogService dialogs) : ViewModelBase
{
    /// <summary>Where nested dialogs appear; set by the export window.</summary>
    public IDialogService Dialogs { get; set; } = dialogs;

    /// <summary>Key derivation cost; <c>null</c> for the default (tests use a cheaper one).</summary>
    public KdfParameters? Kdf { get; init; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string PasswordConfirmation { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludeBorgKeys { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotDone))]
    public partial bool IsDone { get; private set; }

    public bool IsNotDone => !IsDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public string PasswordHint => BorgTexts.Format(Strings.ExportPasswordHint, ExportFile.MinimumPasswordLength);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ExportAsync()
    {
        Errors.Clear();
        if (!ExportFile.IsValidPassword(Password))
            Errors.Add(BorgTexts.Format(Strings.ExportPasswordTooShort, ExportFile.MinimumPasswordLength));
        else if (Password != PasswordConfirmation)
            Errors.Add(Strings.ExportPasswordMismatch);
        if (Errors.Count > 0)
            return;

        var suggested = $"BorgStudio-{DateTime.Now:yyyy-MM-dd}.{ExportFile.Extension}";
        if (await Dialogs.PickSaveFileAsync(Strings.ExportTitle, suggested, ExportFile.Extension) is not { } file)
            return;

        IsBusy = true;
        try
        {
            var collected = await SettingsTransfer.CollectAsync(main, IncludeBorgKeys,
                name => Progress = BorgTexts.Format(Strings.ExportFetchingKey, name));
            Progress = Strings.ExportEncrypting;
            var password = Password;
            await Task.Run(() => ExportFile.Write(file, collected.Contents, password, Kdf));

            foreach (var problem in collected.Problems)
                Problems.Add(problem);
            var contents = collected.Contents;
            Result = BorgTexts.Format(Strings.ExportDone, contents.Repositories.Count, contents.SshKeys.Count,
                contents.Passphrases.Count, contents.BorgKeys.Count, file);
            IsDone = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Errors.Add(exception.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = null;
            Password = PasswordConfirmation = "";
        }
    }

    private bool IsIdle() => !IsBusy;
}

/// <summary>The import dialog: shows what the export contains, merge or replace, then applies it.</summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly ExportContents _contents;

    public ImportViewModel(MainViewModel main, ExportContents contents, IDialogService dialogs)
    {
        _main = main;
        _contents = contents;
        Dialogs = dialogs;

        var repositories = main.Repositories.Select(item => item.Config.Id).ToHashSet();
        var keys = main.Services.Vault.State == VaultState.Unlocked ? main.Services.Vault.Keys.Select(key => key.Id).ToHashSet() : [];
        foreach (var repository in contents.Repositories)
        {
            var missing = main.Services.Plugins.FindRepositoryProvider(repository.ProviderId) is null;
            Items.Add(new ImportItemViewModel(this, repository.Id, Strings.ImportKindRepository, repository.Name,
                repositories.Contains(repository.Id), missing ? BorgTexts.Format(Strings.ImportProviderMissing, repository.ProviderId) : null));
        }
        foreach (var key in contents.SshKeys)
            Items.Add(new ImportItemViewModel(this, key.Id, Strings.ImportKindKey, $"{key.Name} ({key.Type})", keys.Contains(key.Id), null));
        var jobs = main.AllJobs.Select(job => job.Id).ToHashSet();
        foreach (var job in contents.Jobs)
            Items.Add(new ImportItemViewModel(this, job.Id, Strings.ImportKindJob, job.Name, jobs.Contains(job.Id), null));

        Source = BorgTexts.Format(Strings.ImportSource, contents.Machine ?? "?",
            contents.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), contents.AppVersion ?? "?");
    }

    /// <summary>Asks for the file and its password; <c>null</c> if cancelled or unreadable (then the reason was shown).</summary>
    public static async Task<ImportViewModel?> OpenAsync(MainViewModel main, IDialogService dialogs)
    {
        if (await dialogs.PickOpenFileAsync(Strings.ImportTitle) is not { } file)
            return null;

        string? error = null;
        while (true)
        {
            var password = await dialogs.AskSecretAsync(Strings.ImportPasswordTitle,
                BorgTexts.Format(Strings.ImportPasswordPrompt, Path.GetFileName(file)), error);
            if (password is null)
                return null;
            try
            {
                var contents = await Task.Run(() => ExportFile.Read(file, password));
                return new ImportViewModel(main, contents, dialogs);
            }
            catch (SecretBoxException)
            {
                error = Strings.ImportPasswordWrong;
            }
            catch (Exception exception) when (exception is ExportFileException or IOException or UnauthorizedAccessException)
            {
                await dialogs.ShowMessageAsync(Strings.ImportTitle, BorgTexts.Format(Strings.ImportUnreadable, exception.Message));
                return null;
            }
        }
    }

    /// <summary>Where nested dialogs appear; set by the import window.</summary>
    public IDialogService Dialogs { get; set; }

    /// <summary>"Export from NB-1 on 05.11.2026 22:40 (BorgStudio 0.2.0)".</summary>
    public string Source { get; }

    public ObservableCollection<ImportItemViewModel> Items { get; } = [];

    public bool HasConflicts => Items.Any(item => item.Exists);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMerge), nameof(IsReplace))]
    public partial ImportMode Mode { get; set; } = ImportMode.Merge;

    public bool IsMerge
    {
        get => Mode == ImportMode.Merge;
        set { if (value) Mode = ImportMode.Merge; }
    }

    public bool IsReplace
    {
        get => Mode == ImportMode.Replace;
        set { if (value) Mode = ImportMode.Replace; }
    }

    partial void OnModeChanged(ImportMode value)
    {
        foreach (var item in Items)
            item.ModeChanged();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotDone))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsDone { get; private set; }

    public bool IsNotDone => !IsDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    public ObservableCollection<string> Problems { get; } = [];

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (Mode == ImportMode.Replace
            && !await Dialogs.ConfirmAsync(Strings.ImportReplaceTitle, Strings.ImportReplaceWarning, Strings.ImportReplaceConfirm))
            return;

        IsBusy = true;
        try
        {
            // Keys and passphrases go into the vault: set it up on a new computer.
            if (!await VaultAccess.EnsureUnlockedAsync(_main.Services.Vault, Dialogs))
            {
                Problems.Add(Strings.VaultNotUnlocked);
                return;
            }

            var replace = Items.Where(item => item.Exists && item.Replace).Select(item => item.Id).ToHashSet();
            var result = await SettingsTransfer.ApplyAsync(_main, _contents, Mode, replace);
            foreach (var problem in result.Problems)
                Problems.Add(problem);
            Result = BorgTexts.Format(Strings.ImportDone, result.Repositories, result.Keys, result.Passphrases);
            IsDone = true;
        }
        catch (SecretBoxException exception)
        {
            Problems.Add(BorgTexts.Format(Strings.VaultError, exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => !IsBusy && !IsDone;
}

/// <summary>A repository or key in the import preview.</summary>
public sealed partial class ImportItemViewModel(ImportViewModel import, Guid id, string kind, string name, bool exists, string? warning)
    : ObservableObject
{
    public Guid Id { get; } = id;

    public string Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>Already there (same id).</summary>
    public bool Exists { get; } = exists;

    public string Status => Exists ? Strings.ImportStatusExists : Strings.ImportStatusNew;

    public string? Warning { get; } = warning;

    public bool HasWarning => Warning is not null;

    /// <summary>Merging: replace the existing entry with the imported one.</summary>
    [ObservableProperty]
    public partial bool Replace { get; set; }

    public bool ShowReplaceChoice => Exists && import.IsMerge;

    internal void ModeChanged() => OnPropertyChanged(nameof(ShowReplaceChoice));
}
