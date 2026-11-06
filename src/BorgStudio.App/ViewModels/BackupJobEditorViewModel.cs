using System.Collections.ObjectModel;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Creates or edits a backup job: name, source folders, what to leave out, compression.</summary>
public sealed partial class BackupJobEditorViewModel : ViewModelBase
{
    private readonly RepositoryConfig _repository;
    private readonly BorgInstallation? _borg;
    private readonly BackupJob? _existing;

    public BackupJobEditorViewModel(RepositoryConfig repository, BorgInstallation? borg, IDialogService dialogs, BackupJob? existing = null)
    {
        _repository = repository;
        _borg = borg;
        _existing = existing;
        Dialogs = dialogs;

        var job = existing ?? new BackupJob { Id = Guid.NewGuid(), Name = "", RepositoryId = repository.Id };
        Name = job.Name;
        foreach (var source in job.Sources)
            Sources.Add(source);
        CachesAndTemp = job.Exclusions.CachesAndTemp;
        DevelopmentFolders = job.Exclusions.DevelopmentFolders;
        RecycleBin = job.Exclusions.RecycleBin;
        CacheDirTag = job.Exclusions.CacheDirTag;
        Extensions = string.Join(", ", job.Exclusions.Extensions);
        foreach (var folder in job.Exclusions.Folders)
            ExcludedFolders.Add(folder);
        Patterns = string.Join("\n", job.Exclusions.Patterns);
        SelectedCompression = CompressionOptions.FirstOrDefault(option => option.Value == job.Compression)
            ?? new Option<string>(job.Compression, job.Compression);
        if (!CompressionOptions.Contains(SelectedCompression))
            CompressionOptions = [.. CompressionOptions, SelectedCompression];

        Sources.CollectionChanged += (_, _) => Refresh();
        ExcludedFolders.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Where the folder dialogs appear; set by the editor window.</summary>
    public IDialogService Dialogs { get; set; }

    public string Title => _existing is null ? Strings.JobEditorTitleNew : Strings.JobEditorTitleEdit;

    /// <summary>"Backs up into „NAS“".</summary>
    public string RepositoryTitle => BorgTexts.Format(Strings.JobEditorRepository, _repository.Name);

    public BackupJob? Result { get; private set; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    public partial string Name { get; set; }

    public ObservableCollection<string> Sources { get; } = [];

    [ObservableProperty]
    public partial string? SelectedSource { get; set; }

    // --- What to leave out ---

    [ObservableProperty]
    public partial bool CachesAndTemp { get; set; }

    [ObservableProperty]
    public partial bool DevelopmentFolders { get; set; }

    [ObservableProperty]
    public partial bool RecycleBin { get; set; }

    [ObservableProperty]
    public partial bool CacheDirTag { get; set; }

    /// <summary>"iso, .tmp, *.log".</summary>
    [ObservableProperty]
    public partial string Extensions { get; set; }

    public ObservableCollection<string> ExcludedFolders { get; } = [];

    [ObservableProperty]
    public partial string? SelectedExcludedFolder { get; set; }

    /// <summary>borg patterns, one per line.</summary>
    [ObservableProperty]
    public partial string Patterns { get; set; }

    public IReadOnlyList<Option<string>> CompressionOptions { get; private set; } =
    [
        new("zstd,3", Strings.CompressionZstd3),
        new("lz4", Strings.CompressionLz4),
        new("zstd,9", Strings.CompressionZstd9),
        new("none", Strings.CompressionNone),
    ];

    [ObservableProperty]
    public partial Option<string> SelectedCompression { get; set; }

    /// <summary>The patterns borg will get, for the curious.</summary>
    [ObservableProperty]
    public partial string PatternPreview { get; private set; } = "";

    /// <summary>Sources that would end up empty or don't exist.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    partial void OnCachesAndTempChanged(bool value) => Refresh();

    partial void OnDevelopmentFoldersChanged(bool value) => Refresh();

    partial void OnRecycleBinChanged(bool value) => Refresh();

    partial void OnCacheDirTagChanged(bool value) => Refresh();

    partial void OnExtensionsChanged(string value) => Refresh();

    partial void OnPatternsChanged(string value) => Refresh();

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (await Dialogs.PickFolderAsync(Strings.JobEditorAddSource) is { } folder && !Sources.Contains(folder))
            Sources.Add(folder);
    }

    [RelayCommand]
    private void RemoveSource(string? source)
    {
        if ((source ?? SelectedSource) is { } remove)
            Sources.Remove(remove);
    }

    [RelayCommand]
    private async Task AddExcludedFolderAsync()
    {
        if (await Dialogs.PickFolderAsync(Strings.JobEditorAddExcludedFolder) is { } folder && !ExcludedFolders.Contains(folder))
            ExcludedFolders.Add(folder);
    }

    [RelayCommand]
    private void RemoveExcludedFolder(string? folder)
    {
        if ((folder ?? SelectedExcludedFolder) is { } remove)
            ExcludedFolders.Remove(remove);
    }

    [RelayCommand]
    private void Save()
    {
        Errors.Clear();
        if (string.IsNullOrWhiteSpace(Name))
            Errors.Add(Strings.JobEditorNameRequired);
        if (Sources.Count == 0)
            Errors.Add(Strings.JobEditorSourceRequired);
        if (_borg is { Runtime: BorgRuntime.Wsl } && Sources.FirstOrDefault(source => BorgClient.ToWslPath(source) is null) is { } unreachable)
            Errors.Add(BorgTexts.Format(Strings.JobEditorSourceUnreachable, unreachable));
        if (Errors.Count > 0)
            return;

        Result = (_existing ?? new BackupJob { Id = Guid.NewGuid(), Name = "", RepositoryId = _repository.Id }) with
        {
            Name = Name.Trim(),
            Sources = [.. Sources],
            Exclusions = CurrentExclusions(),
            Compression = SelectedCompression.Value,
        };
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private BackupExclusions CurrentExclusions() => new()
    {
        CachesAndTemp = CachesAndTemp,
        DevelopmentFolders = DevelopmentFolders,
        RecycleBin = RecycleBin,
        CacheDirTag = CacheDirTag,
        Extensions = BackupPatterns.ParseExtensions(Extensions ?? ""),
        Folders = [.. ExcludedFolders],
        Patterns = [.. (Patterns ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
    };

    private void Refresh()
    {
        // Called while the constructor fills in the fields.
        if (Sources is null || ExcludedFolders is null || Warnings is null)
            return;

        var exclusions = CurrentExclusions();
        var borgPath = _borg is { } borg ? (Func<string, string?>)(path => BorgClient.BorgPath(borg, path)) : path => path;
        var preview = BackupPatterns.Patterns(exclusions, borgPath);
        if (exclusions.CacheDirTag)
            preview.Insert(0, "--exclude-caches");
        PatternPreview = string.Join("\n", preview);

        Warnings.Clear();
        foreach (var source in Sources)
        {
            if (!Directory.Exists(source))
                Warnings.Add(BorgTexts.Format(Strings.JobEditorSourceMissing, source));
            else if (SourceScanner.IsExcluded(source, exclusions))
                Warnings.Add(BorgTexts.Format(Strings.JobEditorSourceExcluded, source));
        }
    }
}
