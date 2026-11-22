using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// An archive (or a part of it) exported into one file – zip, tar, tar.gz or tar.zst. The export starts when its window
/// opens and can be stopped; a stopped or failed export leaves no file.
/// </summary>
public sealed partial class ArchiveExportViewModel(string what, string targetFile, ExportFormat format,
    Func<IProgress<BorgExtractProgress>, CancellationToken, Task<BorgResult<BorgExportResult>>> export) : ViewModelBase
{
    private CancellationTokenSource? _cancellation;

    /// <summary>"Archiv „Dokumente-pc-…“" or "3 Einträge aus „Dokumente-pc-…“".</summary>
    public string What => what;

    public string TargetFile => targetFile;

    public ExportFormat Format => format;

    /// <summary>Where questions and opening the folder go (set by the window).</summary>
    public IDialogService? Dialogs { get; set; }

    public ProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(OpenFolderCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    public partial bool IsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    public partial bool Succeeded { get; private set; }

    public bool HasResult => ResultText is not null;

    /// <summary>The export running (tests wait for it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Starts the export (when the window opens).</summary>
    public Task Begin()
    {
        if (IsRunning || HasResult)
            return Running;
        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Progress.Start(Strings.ArchiveExportPreparing, cancel: () => _cancellation.Cancel());
        Running = RunAsync(_cancellation.Token);
        return Running;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<BorgExtractProgress>(update =>
        {
            if (update.Preparing)
                Progress.Text = Strings.ArchiveExportPreparing;
            else
            {
                Progress.Text = Strings.ArchiveExportWorking;
                Progress.Report(update.Done, update.Total, update.Path is { } path ? ArchiveTree.DisplayPathOf(path) : null);
            }
        });
        var result = await Task.Run(() => export(progress, cancellationToken));
        Progress.Stop();
        IsRunning = false;
        switch (result)
        {
            case { Value: { } exported }:
                Succeeded = true;
                var size = new FileInfo(targetFile) is { Exists: true } file ? BorgTexts.Size(file.Length) : "";
                ResultText = BorgTexts.Format(Strings.ArchiveExportDone, targetFile, size)
                    + (exported.SkippedLinks > 0 ? " " + BorgTexts.Format(Strings.ArchiveExportSkippedLinks, exported.SkippedLinks.ToString("N0", CultureInfo.CurrentCulture)) : "");
                break;
            case { Error.Kind: BorgErrorKind.Cancelled }:
                ResultText = Strings.ArchiveExportCancelled;
                break;
            default:
                IsError = true;
                ResultText = format == ExportFormat.TarZst && result.Error!.Message?.Contains("zstd", StringComparison.Ordinal) == true
                    ? Strings.ArchiveExportZstdMissing
                    : BorgTexts.Format(Strings.ArchiveExportFailed, BorgTexts.Describe(result.Error!));
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private Task OpenFolderAsync() => Dialogs?.OpenFolderAsync(Path.GetDirectoryName(targetFile)!) ?? Task.CompletedTask;

    private bool CanOpenFolder() => Succeeded && !IsRunning;

    /// <summary>Closing while it runs stops it.</summary>
    public void Abandon() => _cancellation?.Cancel();
}
