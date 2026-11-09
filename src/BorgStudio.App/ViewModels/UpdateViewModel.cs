using System.Text.Json;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The update window: looks for the newest release (unless one is known), shows what is new, downloads and
/// checks the package with progress, then closes the app so the new version installs itself.
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    public enum Stage { Checking, Result, Downloading, Ready }

    private readonly Updater _updater;
    private UpdateCheck? _check;
    private StagedUpdate? _staged;
    private CancellationTokenSource? _cancellation;

    public UpdateViewModel(MainViewModel main, IDialogService dialogs, UpdateCheck? known)
    {
        _updater = main.Updates;
        Dialogs = dialogs;
        InstalledText = BorgTexts.Format(Strings.UpdateInstalledVersion, _updater.CurrentText);
        if (known is not null)
            Show(known);
    }

    /// <summary>Questions belong to the update window; set by it.</summary>
    public IDialogService Dialogs { get; set; }

    public ProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsResult), nameof(IsDownloading), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial Stage State { get; private set; } = Stage.Checking;

    public bool IsChecking => State == Stage.Checking;
    public bool IsResult => State == Stage.Result;
    public bool IsDownloading => State == Stage.Downloading;
    public bool IsReady => State == Stage.Ready;

    public string InstalledText { get; }

    /// <summary>"BorgStudio 0.3.0 is available." / "… is the newest version."</summary>
    [ObservableProperty]
    public partial string Headline { get; private set; } = Strings.UpdateChecking;

    /// <summary>The release notes without the download table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string? Notes { get; private set; }

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Why the app cannot install this release itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlocker))]
    public partial string? Blocker { get; private set; }

    public bool HasBlocker => Blocker is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage))]
    public partial Uri? PageUrl { get; private set; }

    public bool HasPage => PageUrl is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; private set; }

    /// <summary>"BorgStudio 0.3.0 is ready …".</summary>
    [ObservableProperty]
    public partial string ReadyText { get; private set; } = "";

    public event EventHandler? CloseRequested;

    /// <summary>Called when the window opens: asks GitHub unless a release is known.</summary>
    public async Task BeginAsync()
    {
        if (State != Stage.Checking)
            return;
        try
        {
            Show(await _updater.CheckAsync());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Headline = Strings.UpdateTitle;
            Error = BorgTexts.Format(Strings.UpdateCheckFailed, exception.Message);
            State = Stage.Result;
        }
    }

    private void Show(UpdateCheck check)
    {
        _check = check;
        var release = check.Latest;
        Headline = release is null ? Strings.UpdateNoRelease
            : check.IsNewer ? BorgTexts.Format(Strings.UpdateAvailable, release.Version)
            : BorgTexts.Format(Strings.UpdateUpToDate, _updater.Current?.ToString() ?? _updater.CurrentText);
        Notes = check.IsNewer && release is not null ? ReleaseNotes(release.Notes) : null;
        PageUrl = release is not null && Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var page) ? page : null;
        Blocker = check.IsNewer || check.Blocker == UpdateBlocker.DevelopmentBuild ? BlockerText(check.Blocker) : null;
        CanInstall = check.CanInstall;
        State = Stage.Result;
    }

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallAsync()
    {
        if (_check is not { Latest: { } release, Package: { } package } check)
            return;

        Error = null;
        State = Stage.Downloading;
        _cancellation = new CancellationTokenSource();
        Progress.Start(BorgTexts.Format(Strings.UpdateDownloading, release.Version), cancel: _cancellation.Cancel, rate: BorgTexts.Rate);
        try
        {
            var progress = new ViewModelProgress(this, bytes => Progress.Report(bytes, Math.Max(package.Size, 1)));
            _staged = await _updater.DownloadAsync(check, progress, _cancellation.Token);
            ReadyText = BorgTexts.Format(Strings.UpdateReady, release.Version);
            State = Stage.Ready;
            Restart();
        }
        catch (OperationCanceledException)
        {
            State = Stage.Result;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                              or UpdateException or InvalidDataException)
        {
            Error = BorgTexts.Format(Strings.UpdateFailed, exception is UpdateException update ? ErrorText(update.Error) : exception.Message);
            State = Stage.Result;
        }
        finally
        {
            Progress.Stop();
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private bool CanInstallNow() => CanInstall && State == Stage.Result;

    /// <summary>Closes the app (a running backup asks first) and starts the new version's installer.</summary>
    [RelayCommand]
    private void Restart()
    {
        if (_staged is not null)
            Dialogs.RestartApp(_updater.ApplyCommand(_staged, Environment.ProcessId));
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The window closes: stop a running download.</summary>
    public void Abandon() => _cancellation?.Cancel();

    /// <summary>GitHub's release text without the download table every release ends with.</summary>
    /// <remarks>Markdown is shown as plain text: "## Added" becomes "Added", "- item" becomes "• item".</remarks>
    public static string ReleaseNotes(string notes)
    {
        var downloads = notes.IndexOf("### Downloads", StringComparison.Ordinal);
        var lines = (downloads < 0 ? notes : notes[..downloads]).Replace("\r\n", "\n").Trim().Split('\n')
            .Select(line => line.TrimEnd() switch
            {
                var heading when heading.StartsWith('#') => heading.TrimStart('#').Trim(),
                var item when item.StartsWith("- ", StringComparison.Ordinal) || item.StartsWith("* ", StringComparison.Ordinal) => "• " + item[2..],
                var other => other,
            });
        return string.Join('\n', lines);
    }

    private string? BlockerText(UpdateBlocker blocker) => blocker switch
    {
        UpdateBlocker.DevelopmentBuild => Strings.UpdateBlockedDevelopment,
        UpdateBlocker.UnsupportedPlatform => Strings.UpdateBlockedPlatform,
        UpdateBlocker.NoPackage => Strings.UpdateBlockedPackage,
        UpdateBlocker.NotWritable => BorgTexts.Format(Strings.UpdateBlockedNotWritable, _updater.Location?.Directory ?? ""),
        _ => null,
    };

    private static string ErrorText(UpdateError error) => error switch
    {
        UpdateError.ChecksumMismatch => Strings.UpdateChecksumMismatch,
        UpdateError.ChecksumMissing => Strings.UpdateChecksumMissing,
        _ => Strings.UpdateUnexpectedPackage,
    };

    /// <summary>Hands download progress to the view model's thread.</summary>
    private sealed class ViewModelProgress(UpdateViewModel owner, Action<long> report) : IProgress<long>
    {
        public void Report(long value) => owner.OnViewModelThread(() => report(value));
    }
}
