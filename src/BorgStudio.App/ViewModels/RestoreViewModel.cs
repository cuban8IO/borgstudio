using System.Collections.ObjectModel;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Runs one <c>borg extract</c> (the archive, login and passphrase are known to whoever provides it).</summary>
public delegate Task<BorgResult<BorgExtractResult>> RestoreExecutor(RestoreRun run, IProgress<BorgExtractProgress> progress,
    CancellationToken cancellationToken);

public enum RestoreStage
{
    Options,
    Running,
    Done,
}

/// <summary>
/// Restoring files and folders of an archive: into a folder (directly or with the full path) or back to the original
/// location. Before writing it checks what would be overwritten and asks; then borg runs with progress, pace and
/// remaining time.
/// </summary>
public sealed partial class RestoreViewModel : ViewModelBase
{
    /// <summary>How many overwritten files the question lists by name.</summary>
    private const int ConflictsListed = 10;

    private readonly IReadOnlyList<ArchiveNode> _items;
    private readonly BorgRuntime _runtime;
    private readonly RestoreExecutor _extract;
    private readonly TimeSpan? _expectedPreparing;
    private CancellationTokenSource? _cancellation;

    /// <param name="items">What to restore (no item inside another).</param>
    /// <param name="expectedPreparing">How long borg took last time to read the archive's file list – about what it
    /// needs before extracting, to work out the size.</param>
    public RestoreViewModel(string archiveName, IReadOnlyList<ArchiveNode> items, bool wholeArchive, BorgRuntime runtime,
        RestoreExecutor extract, IDialogService dialogs, TimeSpan? expectedPreparing = null)
    {
        _items = items;
        _runtime = runtime;
        _extract = extract;
        _expectedPreparing = expectedPreparing;
        Dialogs = dialogs;
        var size = BorgTexts.Size(items.Sum(item => item.Size));
        WhatText = wholeArchive
            ? BorgTexts.Format(Strings.RestoreWhatAll, archiveName, size)
            : BorgTexts.Format(Strings.RestoreWhatSelection, items.Count, archiveName, size);
        CanRestoreToOriginal = RestorePlan.CanRestoreToOriginal(items, runtime);
        OriginalUnavailableText = CanRestoreToOriginal ? null
            : runtime == BorgRuntime.Wsl ? Strings.RestoreOriginalUnavailableWsl : Strings.RestoreOriginalUnavailableNative;
        UpdateExample();
    }

    /// <summary>Where the folder picker and questions appear; set by the window.</summary>
    public IDialogService Dialogs { get; set; }

    public string WhatText { get; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptions), nameof(IsRunning), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PickFolderCommand))]
    public partial RestoreStage Stage { get; private set; }

    public bool IsOptions => Stage == RestoreStage.Options;

    public bool IsRunning => Stage == RestoreStage.Running;

    public bool IsDone => Stage == RestoreStage.Done;

    // --- Options ---

    public bool CanRestoreToOriginal { get; }

    public string? OriginalUnavailableText { get; }

    public bool HasOriginalUnavailableText => OriginalUnavailableText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToFolder))]
    public partial bool ToOriginal { get; set; }

    public bool ToFolder
    {
        get => !ToOriginal;
        set => ToOriginal = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFolderText))]
    public partial string? TargetFolder { get; set; }

    public string TargetFolderText => TargetFolder ?? Strings.RestoreFolderNone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Direct))]
    public partial bool KeepFullPath { get; set; }

    public bool Direct
    {
        get => !KeepFullPath;
        set => KeepFullPath = !value;
    }

    /// <summary>"Beispiel: D:\Wiederherstellung\Rechnungen\2025\rechnung-001.pdf".</summary>
    [ObservableProperty]
    public partial string? ExampleText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    partial void OnToOriginalChanged(bool value) => UpdateExample();

    partial void OnTargetFolderChanged(string? value) => UpdateExample();

    partial void OnKeepFullPathChanged(bool value) => UpdateExample();

    [RelayCommand(CanExecute = nameof(IsOptions))]
    private async Task PickFolderAsync()
    {
        if (await Dialogs.PickFolderAsync(Strings.RestorePickFolder) is { } folder)
        {
            TargetFolder = folder;
            ToOriginal = false;
        }
    }

    private void UpdateExample()
    {
        var plan = Plan(TargetFolder ?? "…");
        var first = plan?.Files().FirstOrDefault() ?? _items.FirstOrDefault();
        ExampleText = plan is null || first is null ? null : BorgTexts.Format(Strings.RestoreExample, plan.Destination(first));
    }

    private RestorePlan? Plan(string folder) => ToOriginal
        ? RestorePlan.ToOriginal(_items, _runtime)
        : RestorePlan.IntoFolder(_items, folder, KeepFullPath);

    // --- Running ---

    public ProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? ResultText { get; private set; }

    public bool HasResult => ResultText is not null;

    [ObservableProperty]
    public partial bool ResultIsError { get; private set; }

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial int WarningCount { get; private set; }

    public bool HasWarnings => WarningCount > 0;

    /// <summary>The folder to open afterwards; <c>null</c> if there is none (yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    public partial string? OpenFolderPath { get; private set; }

    public bool CanOpenFolder => IsDone && OpenFolderPath is not null && !ResultIsError;

    /// <summary>The run started by <see cref="StartCommand"/> (tests wait for it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsOptions))]
    private Task StartAsync() => Running = RestoreAsync();

    private async Task RestoreAsync()
    {
        Error = null;
        if (PrepareTarget() is not { } plan)
            return;

        using var cancellation = _cancellation = new CancellationTokenSource();
        Stage = RestoreStage.Running;
        try
        {
            if (!await ConfirmOverwritingAsync(plan, cancellation.Token))
            {
                Stage = RestoreStage.Options;
                return;
            }

            var warnings = new List<string>();
            for (var index = 0; index < plan.Runs.Count; index++)
            {
                var run = plan.Runs[index];
                Progress.Start(plan.Runs.Count > 1 ? BorgTexts.Format(Strings.RestoreRunningDrive, run.Label, index + 1, plan.Runs.Count)
                    : Strings.RestoreRunning, _expectedPreparing, cancellation.Cancel, BorgTexts.Rate);
                var progress = new ViewModelProgress<BorgExtractProgress>(this, Report);
                var result = await _extract(run, progress, cancellation.Token);
                if (!result.Succeeded)
                {
                    Finish(result.Error!.Kind == BorgErrorKind.Cancelled ? Strings.RestoreCancelled
                        : BorgTexts.Format(Strings.RestoreFailed, BorgTexts.Describe(result.Error)), isError: result.Error.Kind != BorgErrorKind.Cancelled);
                    return;
                }
                warnings.AddRange(result.Value!.Warnings);
            }

            foreach (var warning in warnings.Take(50))
                Warnings.Add(Readable(warning));
            WarningCount = warnings.Count;
            var size = BorgTexts.Size(_items.Sum(item => item.Size));
            Finish(ToOriginal ? BorgTexts.Format(Strings.RestoreDoneOriginal, size) : BorgTexts.Format(Strings.RestoreDone, size, TargetFolder),
                isError: false);
            OpenFolderPath = ToOriginal ? Path.GetDirectoryName(plan.Destination(_items[0])) : TargetFolder;
        }
        catch (OperationCanceledException)
        {
            // Cancelled while checking: nothing was written.
            Progress.Stop();
            Stage = RestoreStage.Options;
        }
        finally
        {
            _cancellation = null;
        }
    }

    /// <summary>The plan for the chosen target, with the folder created; <c>null</c> (and <see cref="Error"/>) if not possible.</summary>
    private RestorePlan? PrepareTarget()
    {
        if (ToOriginal)
            return RestorePlan.ToOriginal(_items, _runtime);

        if (string.IsNullOrWhiteSpace(TargetFolder))
        {
            Error = Strings.RestoreFolderRequired;
            return null;
        }
        if (_runtime == BorgRuntime.Wsl && BorgClient.ToWslPath(TargetFolder) is null)
        {
            Error = BorgTexts.Format(Strings.RestoreFolderUnreachable, TargetFolder);
            return null;
        }
        try
        {
            Directory.CreateDirectory(TargetFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            Error = BorgTexts.Format(Strings.RestoreFolderNotCreated, exception.Message);
            return null;
        }
        return RestorePlan.IntoFolder(_items, TargetFolder, KeepFullPath);
    }

    /// <summary>Looks for files that would be overwritten and asks; <c>true</c> to go on.</summary>
    private async Task<bool> ConfirmOverwritingAsync(RestorePlan plan, CancellationToken cancellationToken)
    {
        Progress.Start(Strings.RestoreChecking, cancel: _cancellation!.Cancel);
        var total = await Task.Run(() => plan.Files().LongCount(), cancellationToken);
        var progress = new ViewModelProgress<long>(this, done =>
            Progress.Report(done, Math.Max(total, 1), BorgTexts.Format(Strings.RestoreCheckedFiles, done, total)));
        var conflicts = await Task.Run(() => plan.FindConflicts(progress, cancellationToken), cancellationToken);
        Progress.Stop();
        if (conflicts.Count == 0)
            return true;

        var listed = conflicts.OrderByDescending(conflict => conflict.IsNewer).Take(ConflictsListed).Select(conflict => conflict.Destination).ToList();
        if (conflicts.Count > ConflictsListed)
            listed.Add(BorgTexts.Format(Strings.RestoreOverwriteMore, conflicts.Count - ConflictsListed));
        var message = BorgTexts.Format(Strings.RestoreOverwriteMessage, conflicts.Count, conflicts.Count(conflict => conflict.IsNewer))
            + "\n\n" + string.Join("\n", listed);
        return await Dialogs.ConfirmAsync(Strings.RestoreOverwriteTitle, message, Strings.RestoreOverwriteConfirm);
    }

    private void Report(BorgExtractProgress update)
    {
        if (update.Preparing)
            Progress.SetDetail(Strings.RestorePreparing);
        else
            Progress.Report(update.Done, update.Total, update.Path is null ? null : ArchiveTree.DisplayPathOf(update.Path));
    }

    /// <summary>A warning about a file on a Windows drive with the path as the user knows it ("mnt/c/a.txt: …" → "C:\a.txt: …").</summary>
    internal static string Readable(string warning)
    {
        var colon = warning.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && warning.StartsWith("mnt/", StringComparison.Ordinal)
            ? ArchiveTree.DisplayPathOf(warning[..colon]) + warning[colon..]
            : warning;
    }

    private void Finish(string text, bool isError)
    {
        Progress.Stop();
        ResultText = text;
        ResultIsError = isError;
        Stage = RestoreStage.Done;
        OnPropertyChanged(nameof(CanOpenFolder));
    }

    /// <summary>Before the restore: closes the window. While it runs: stops borg (what was restored stays).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation is { } running)
            running.Cancel();
        else
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The window is closing (e.g. the app locked): stop borg if it still runs.</summary>
    public void Abandon() => _cancellation?.Cancel();

    /// <summary>Hands progress to the view model's thread (borg reports on a background thread).</summary>
    private sealed class ViewModelProgress<T>(RestoreViewModel owner, Action<T> report) : IProgress<T>
    {
        public void Report(T value) => owner.OnViewModelThread(() => report(value));
    }
}
