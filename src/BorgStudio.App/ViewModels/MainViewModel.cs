using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Everything the main window works with; replaced by fakes in tests.</summary>
public sealed record AppServices(
    BorgDetector BorgDetector,
    PluginCatalog Plugins,
    RepositoryStore RepositoryStore,
    ISecretStore SecretStore,
    BorgClient BorgClient,
    ISshService Ssh,
    SshKeyStore SshKeys,
    SshKeygen SshKeygen,
    Vault Vault,
    SettingsStore Settings,
    BackupJobStore Jobs,
    ArchiveCache Archives,
    DurationHistory Durations,
    IDialogService Dialogs)
{
    public SshHostTrust SshTrust => new(Ssh, new KnownHostsFile(SshKeys.KnownHostsFile));
}

public partial class MainViewModel : ViewModelBase
{
    private readonly AppServices _services;

    // Designer only.
    public MainViewModel() : this(new AppServices(BorgDetector.CreateDefault(), PluginCatalog.Empty,
        RepositoryStore.CreateDefault(), SecretStores.CreateDefault(), BorgClient.CreateDefault(),
        new SshNetService(), SshKeyStore.CreateDefault(), SshKeygen.CreateDefault(), Vault.CreateDefault(), SettingsStore.CreateDefault(), BackupJobStore.CreateDefault(),
        ArchiveCache.CreateDefault(Vault.CreateDefault()), DurationHistory.CreateDefault(), new NoDialogs()))
    {
    }

    public MainViewModel(AppServices services)
    {
        _services = services;
        InitializeVault();
        LoadRepositories();
        LoadJobs();
        RefreshKeyNames();
    }

    public PluginCatalog Plugins => _services.Plugins;

    /// <summary>For the windows opened from the main window (settings, keys).</summary>
    public AppServices Services => _services;

    /// <summary>Window title with the app version, e.g. "BorgStudio 0.2.0".</summary>
    public string Title { get; } = $"BorgStudio {AppVersion.Current}";

    /// <summary>The borg used for all repository operations; <c>null</c> until found.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRepositoryCommand), nameof(ExportKeyCommand), nameof(RunJobCommand), nameof(CleanUpJobCommand),
        nameof(FreeSpaceCommand))]
    public partial BorgInstallation? Borg { get; private set; }

    /// <summary>Status bar text, e.g. "borg 1.4.5 · /usr/bin/borg".</summary>
    [ObservableProperty]
    public partial string BorgStatus { get; set; } = Strings.BorgChecking;

    [ObservableProperty]
    public partial bool HasBorgProblem { get; set; }

    [ObservableProperty]
    public partial string? BorgProblemTitle { get; set; }

    [ObservableProperty]
    public partial string? BorgProblemText { get; set; }

    public Uri InstallationGuideUrl { get; } = new("https://borgbackup.readthedocs.io/en/stable/installation.html");

    [RelayCommand]
    private async Task CheckBorgAsync()
    {
        BorgStatus = Strings.BorgChecking;
        try
        {
            ShowBorgResult(await Task.Run(() => _services.BorgDetector.DetectAsync()));
        }
        catch (Exception exception)
        {
            Borg = null;
            BorgStatus = Strings.BorgStatusNotFound;
            ShowProblem(Strings.BorgCheckFailedTitle, exception.Message);
        }
    }

    private void ShowBorgResult(BorgInstallation? borg)
    {
        Borg = borg;
        if (borg is null)
        {
            BorgStatus = Strings.BorgStatusNotFound;
            var text = BorgTexts.Format(Strings.BorgMissingText, BorgCompatibility.MinimumVersion);
            ShowProblem(Strings.BorgMissingTitle,
                OperatingSystem.IsWindows() ? text + " " + Strings.BorgMissingWindowsHint : text);
            return;
        }

        BorgStatus = BorgTexts.Format(borg.Runtime == BorgRuntime.Wsl ? Strings.BorgStatusWsl : Strings.BorgStatusNative,
            borg.Version, borg.Path);

        switch (borg.Support)
        {
            case BorgSupport.TooOld:
                ShowProblem(BorgTexts.Format(Strings.BorgTooOldTitle, borg.Version),
                    BorgTexts.Format(Strings.BorgTooOldText, BorgCompatibility.MinimumVersion));
                break;
            case BorgSupport.NotYetSupported:
                ShowProblem(BorgTexts.Format(Strings.BorgNotYetSupportedTitle, borg.Version), Strings.BorgNotYetSupportedText);
                break;
            default:
                HasBorgProblem = false;
                break;
        }
    }

    private void ShowProblem(string title, string text)
    {
        BorgProblemTitle = title;
        BorgProblemText = text;
        HasBorgProblem = true;
    }

    /// <summary>For the designer, which has no windows to show dialogs in.</summary>
    private sealed class NoDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> AskPassphraseAsync(string repositoryName) => Task.FromResult<string?>(null);
        public Task<string?> AskPinAsync(string? error = null) => Task.FromResult<string?>(null);
        public Task<string?> AskNewPinAsync() => Task.FromResult<string?>(null);
        public Task<string?> AskSecretAsync(string title, string prompt, string? error = null) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string? defaultExtension = "txt") => Task.FromResult<string?>(null);
        public Task<string?> PickOpenFileAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task ShowBackupJobEditorAsync(BackupJobEditorViewModel editor) => Task.CompletedTask;
        public Task<bool> ConfirmPruneAsync(PrunePreviewViewModel preview) => Task.FromResult(false);
        public Task ShowArchiveBrowserAsync(ArchiveBrowserViewModel browser) => Task.CompletedTask;
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
        public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) => Task.CompletedTask;
    }
}
