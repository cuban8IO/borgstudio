using System.Collections.ObjectModel;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>A drive in the job editor's list.</summary>
/// <param name="Root">Where it is attached; <c>null</c>: not attached (the job's own drive).</param>
/// <param name="Id">Its id; <c>null</c> if it has none yet (its id file is written when saving).</param>
public sealed record DriveOption(string Text, string? Root, string? Id, string Label)
{
    public override string ToString() => Text;
}

/// <summary>Creates or edits a backup job: name, source folders (or a removable drive), what to leave out, compression.</summary>
public sealed partial class BackupJobEditorViewModel : ViewModelBase
{
    private readonly RepositoryConfig _repository;
    private readonly BorgInstallation? _borg;
    private readonly BackupJob? _existing;

    public BackupJobEditorViewModel(RepositoryConfig repository, BorgInstallation? borg, IDialogService dialogs, BackupJob? existing = null,
        SourceDriveLocator? drives = null)
    {
        _repository = repository;
        _borg = borg;
        _existing = existing;
        _drives = drives;
        Dialogs = dialogs;

        var job = existing ?? new BackupJob { Id = Guid.NewGuid(), Name = "", RepositoryId = repository.Id };
        Name = job.Name;
        foreach (var source in job.Sources)
            Sources.Add(source);
        _drive = job.Drive;
        IsDriveSource = job.Drive is not null;
        if (job.Drive is { } drive)
        {
            WholeDrive = drive.IsWholeDrive;
            foreach (var folder in drive.Folders)
                DriveFolders.Add(folder);
            DrivePlugIn = drive.PlugIn.Enabled;
            DrivePlugInHours = drive.PlugIn.MinimumHours;
            DriveEject = drive.Eject;
        }
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

        // Without a rule the numbers stay empty until clean-up is switched on (then the suggestion is filled in).
        if (job.Retention is { HasRules: true } retention)
            ShowRetention(retention);
        CleanUp = job.Retention is { HasRules: true };

        foreach (var schedule in job.Schedules)
            Schedules.Add(new ScheduleItemViewModel(schedule, RemoveSchedule));
        CatchUp = job.CatchUp;
        Notify = job.Notify;
        OverdueDays = job.OverdueDays;
        WebhookTrigger = job.WebhookTrigger;
        SelectedRestoreTest = RestoreTestOptions.First(option => option.Value == job.RestoreTest);
        NotOnBattery = job.Conditions.NotOnBattery;
        NotMetered = job.Conditions.NotMetered;
        WiredOnly = job.Conditions.WiredOnly;
        TargetReachable = job.Conditions.TargetReachable;
        HookBefore = job.Hooks.Before;
        HookAfter = job.Hooks.After;
        SelectedHookShell = HookShellOptions.FirstOrDefault(option => option.Value == job.Hooks.EffectiveShell) ?? HookShellOptions[0];
        HookContinueOnFailure = job.Hooks.ContinueOnBeforeFailure;
        HookAfterAlways = job.Hooks.AfterAlways;
        RestoreTestFiles = job.RestoreTestFiles;
        Schedules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSchedules));

        Sources.CollectionChanged += (_, _) => Refresh();
        ExcludedFolders.CollectionChanged += (_, _) => Refresh();
        Refresh();
        DrivesLoaded = drives is null ? Task.CompletedTask : RefreshDrivesAsync();
    }

    private readonly SourceDriveLocator? _drives;

    /// <summary>The drive the job had when the editor opened.</summary>
    private readonly SourceDrive? _drive;

    // --- A removable drive as the source ---

    /// <summary>Drives can be found here (the USB drive plugin is there).</summary>
    public bool CanBackUpDrive => _drives is not null;

    /// <summary>The job backs up a removable drive instead of folders on this computer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderSource))]
    public partial bool IsDriveSource { get; set; }

    public bool IsFolderSource
    {
        get => !IsDriveSource;
        set
        {
            if (value)
                IsDriveSource = false;
        }
    }

    partial void OnIsDriveSourceChanged(bool value) => Refresh();

    /// <summary>The attached removable drives (and the job's own one, also when it is not attached).</summary>
    public ObservableCollection<DriveOption> AttachedDrives { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveNeedsIdFile))]
    public partial DriveOption? SelectedDrive { get; set; }

    /// <summary>The chosen drive has no UUID: BorgStudio writes its id file onto it when saving.</summary>
    public bool DriveNeedsIdFile => SelectedDrive is { Root: not null, Id: null };

    /// <summary>The attached drives were listed (tests wait for it).</summary>
    public Task DrivesLoaded { get; private set; }

    /// <summary>Back up the whole drive, or only <see cref="DriveFolders"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SomeDriveFolders))]
    public partial bool WholeDrive { get; set; } = true;

    public bool SomeDriveFolders
    {
        get => !WholeDrive;
        set
        {
            if (value)
                WholeDrive = false;
        }
    }

    /// <summary>Folders on the drive, relative to its root ("DCIM").</summary>
    public ObservableCollection<string> DriveFolders { get; } = [];

    [ObservableProperty]
    public partial string? SelectedDriveFolder { get; set; }

    [ObservableProperty]
    public partial bool DrivePlugIn { get; set; }

    [ObservableProperty]
    public partial decimal? DrivePlugInHours { get; set; } = PlugInBackup.DefaultMinimumHours;

    [ObservableProperty]
    public partial bool DriveEject { get; set; }

    [RelayCommand]
    private Task RefreshDrives() => DrivesLoaded = RefreshDrivesAsync();

    private async Task RefreshDrivesAsync()
    {
        if (_drives is null)
            return;
        IReadOnlyList<AttachedDrive> attached;
        try
        {
            attached = await _drives.AttachedAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            attached = [];
        }

        var keep = SelectedDrive;
        AttachedDrives.Clear();
        foreach (var drive in attached)
            AttachedDrives.Add(new DriveOption(DriveText(drive.Label, drive.Root, drive.Size), drive.Root, drive.Id, drive.Label));
        // The job's own drive: where it is attached now – or shown as missing.
        if (_drive is { } own)
        {
            var root = await _drives.FindAsync(own);
            var option = AttachedDrives.FirstOrDefault(candidate => candidate.Root == root && root is not null);
            if (option is null)
            {
                option = root is null
                    ? new DriveOption(BorgTexts.Format(Strings.JobEditorDriveNotAttached, BorgTexts.DriveName(own)), null, own.Id, own.Label)
                    : new DriveOption(DriveText(own.Label, root, _drives.Drives.Size(root)), root, own.Id, own.Label);
                AttachedDrives.Insert(0, option);
            }
            else if (option.Id != own.Id)
            {
                // Found by its id file while its UUID is listed first: keep the id it was chosen with.
                AttachedDrives[AttachedDrives.IndexOf(option)] = option = option with { Id = own.Id };
            }
            SelectedDrive = keep is not null && keep.Id != own.Id && AttachedDrives.FirstOrDefault(candidate => candidate.Root == keep.Root) is { } same
                ? same
                : option;
        }
        else
        {
            SelectedDrive = AttachedDrives.FirstOrDefault(candidate => candidate.Root == keep?.Root) ?? AttachedDrives.FirstOrDefault();
        }
    }

    private static string DriveText(string label, string root, long? size) =>
        BorgTexts.Format(Strings.JobEditorDriveOption, string.IsNullOrWhiteSpace(label) ? Strings.SourceDriveUnnamed : label, root,
            size is { } bytes ? BorgTexts.Size(bytes) : "?");

    [RelayCommand]
    private async Task AddDriveFolderAsync()
    {
        if (SelectedDrive?.Root is not { } root)
        {
            await Dialogs.ShowMessageAsync(Strings.JobEditorDriveFolders, Strings.JobEditorDriveFolderNeedsDrive);
            return;
        }
        if (await Dialogs.PickFolderAsync(Strings.JobEditorAddDriveFolder) is not { } folder)
            return;
        if (SourceDrive.Below(root, folder) is not { } relative)
        {
            await Dialogs.ShowMessageAsync(Strings.JobEditorDriveFolders, BorgTexts.Format(Strings.JobEditorDriveFolderOutside, folder, root));
            return;
        }
        if (!DriveFolders.Contains(relative))
            DriveFolders.Add(relative);
    }

    [RelayCommand]
    private void RemoveDriveFolder(string? folder)
    {
        if ((folder ?? SelectedDriveFolder) is { } remove)
            DriveFolders.Remove(remove);
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

    // --- Clean-up (borg prune + compact after each backup) ---

    [ObservableProperty]
    public partial bool CleanUp { get; set; }

    // decimal? for the NumericUpDowns; empty counts as 0.
    [ObservableProperty]
    public partial decimal? KeepDaily { get; set; }

    [ObservableProperty]
    public partial decimal? KeepWeekly { get; set; }

    [ObservableProperty]
    public partial decimal? KeepMonthly { get; set; }

    [ObservableProperty]
    public partial decimal? KeepYearly { get; set; }

    [ObservableProperty]
    public partial decimal? KeepHourly { get; set; }

    [ObservableProperty]
    public partial decimal? KeepLast { get; set; }

    [ObservableProperty]
    public partial decimal? KeepWithinDays { get; set; }

    partial void OnCleanUpChanged(bool value)
    {
        if (value && !CurrentRetention().HasRules)
            ShowRetention(RetentionPolicy.Suggested);
    }

    private void ShowRetention(RetentionPolicy retention)
    {
        KeepDaily = retention.Daily;
        KeepWeekly = retention.Weekly;
        KeepMonthly = retention.Monthly;
        KeepYearly = retention.Yearly;
        KeepHourly = retention.Hourly;
        KeepLast = retention.Last;
        KeepWithinDays = retention.WithinDays;
    }

    private RetentionPolicy CurrentRetention()
    {
        static int Count(decimal? value) => value is > 0 ? (int)Math.Min(value.Value, int.MaxValue) : 0;
        return new RetentionPolicy
        {
            Daily = Count(KeepDaily),
            Weekly = Count(KeepWeekly),
            Monthly = Count(KeepMonthly),
            Yearly = Count(KeepYearly),
            Hourly = Count(KeepHourly),
            Last = Count(KeepLast),
            WithinDays = Count(KeepWithinDays),
        };
    }

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
        if (await Dialogs.PickFolderAsync(Strings.JobEditorAddExcludedFolder) is not { } folder)
            return;
        // On a drive: kept relative to its root, like the archive's paths.
        if (IsDriveSource)
        {
            if (SelectedDrive?.Root is not { } root || SourceDrive.Below(root, folder) is not { } relative)
            {
                await Dialogs.ShowMessageAsync(Strings.JobEditorExcludedFolders,
                    SelectedDrive?.Root is { } on ? BorgTexts.Format(Strings.JobEditorDriveFolderOutside, folder, on) : Strings.JobEditorDriveFolderNeedsDrive);
                return;
            }
            folder = relative;
        }
        if (!ExcludedFolders.Contains(folder))
            ExcludedFolders.Add(folder);
    }

    [RelayCommand]
    private void RemoveExcludedFolder(string? folder)
    {
        if ((folder ?? SelectedExcludedFolder) is { } remove)
            ExcludedFolders.Remove(remove);
    }

    // --- Schedules ---

    public ObservableCollection<ScheduleItemViewModel> Schedules { get; } = [];

    public bool HasSchedules => Schedules.Count > 0;

    [ObservableProperty]
    public partial bool CatchUp { get; set; }

    /// <summary>The job's runs are reported through the notification channels.</summary>
    [ObservableProperty]
    public partial bool Notify { get; set; } = true;

    /// <summary>Report the job after this many days without a successful backup; 0: never.</summary>
    [ObservableProperty]
    public partial decimal? OverdueDays { get; set; } = 0;

    /// <summary>The incoming webhook may start the job (Settings → Webhook).</summary>
    [ObservableProperty]
    public partial bool WebhookTrigger { get; set; }

    // --- Conditions of scheduled runs ---

    [ObservableProperty]
    public partial bool NotOnBattery { get; set; }

    [ObservableProperty]
    public partial bool NotMetered { get; set; }

    [ObservableProperty]
    public partial bool WiredOnly { get; set; }

    [ObservableProperty]
    public partial bool TargetReachable { get; set; }

    /// <summary>macOS cannot tell a metered connection.</summary>
    public bool CanTellMetered => !OperatingSystem.IsMacOS();

    // --- Commands ---

    /// <summary>Runs before the backup; empty: none.</summary>
    [ObservableProperty]
    public partial string? HookBefore { get; set; } = "";

    /// <summary>Runs after the backup; empty: none.</summary>
    [ObservableProperty]
    public partial string? HookAfter { get; set; } = "";

    /// <summary>The shells of this system.</summary>
    public IReadOnlyList<Option<HookShell>> HookShellOptions { get; } = OperatingSystem.IsWindows()
        ? [new(HookShell.PowerShell, Strings.HookShellPowerShell), new(HookShell.Cmd, Strings.HookShellCmd), new(HookShell.Wsl, Strings.HookShellWsl)]
        : OperatingSystem.IsMacOS()
            ? [new(HookShell.Sh, "sh"), new(HookShell.Bash, "bash"), new(HookShell.Zsh, "zsh")]
            : [new(HookShell.Sh, "sh"), new(HookShell.Bash, "bash")];

    [ObservableProperty]
    public partial Option<HookShell> SelectedHookShell { get; set; }

    /// <summary>The backup runs even if the command before it fails.</summary>
    [ObservableProperty]
    public partial bool HookContinueOnFailure { get; set; }

    /// <summary>The command after runs whatever the outcome (otherwise only after a backup that worked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HookAfterOnSuccess))]
    public partial bool HookAfterAlways { get; set; }

    public bool HookAfterOnSuccess
    {
        get => !HookAfterAlways;
        set
        {
            if (value)
                HookAfterAlways = false;
        }
    }

    // --- Restore test ---

    public IReadOnlyList<Option<Core.Checks.RestoreTestMode>> RestoreTestOptions { get; } =
    [
        new(Core.Checks.RestoreTestMode.Off, Strings.RestoreTestModeOff),
        new(Core.Checks.RestoreTestMode.Read, Strings.RestoreTestModeRead),
        new(Core.Checks.RestoreTestMode.Compare, Strings.RestoreTestModeCompare),
        new(Core.Checks.RestoreTestMode.WholeArchive, Strings.RestoreTestModeWholeArchive),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestoreTestSample))]
    public partial Option<Core.Checks.RestoreTestMode> SelectedRestoreTest { get; set; }

    /// <summary>A sample is restored (its size can be chosen).</summary>
    public bool IsRestoreTestSample => SelectedRestoreTest?.Value is Core.Checks.RestoreTestMode.Read or Core.Checks.RestoreTestMode.Compare;

    [ObservableProperty]
    public partial decimal? RestoreTestFiles { get; set; } = 20;

    [RelayCommand]
    private void AddSchedule() => Schedules.Add(new ScheduleItemViewModel(new BackupSchedule(), RemoveSchedule));

    private void RemoveSchedule(ScheduleItemViewModel schedule) => Schedules.Remove(schedule);

    [RelayCommand]
    private async Task SaveAsync()
    {
        Errors.Clear();
        if (string.IsNullOrWhiteSpace(Name))
            Errors.Add(Strings.JobEditorNameRequired);
        if (IsDriveSource)
        {
            if (SelectedDrive is null)
                Errors.Add(Strings.JobEditorDriveRequired);
            else if (!WholeDrive && DriveFolders.Count == 0)
                Errors.Add(Strings.JobEditorDriveFoldersRequired);
        }
        else
        {
            if (Sources.Count == 0)
                Errors.Add(Strings.JobEditorSourceRequired);
            if (_borg is { Runtime: BorgRuntime.Wsl } && Sources.FirstOrDefault(source => BorgClient.ToWslPath(source) is null) is { } unreachable)
                Errors.Add(BorgTexts.Format(Strings.JobEditorSourceUnreachable, unreachable));
        }
        var retention = CurrentRetention();
        if (CleanUp && !retention.HasRules)
            Errors.Add(Strings.JobEditorRetentionRequired);
        var schedules = Schedules.Select(schedule => schedule.ToSchedule()).ToList();
        if (schedules.Any(schedule => !schedule.IsValid))
            Errors.Add(Strings.JobEditorScheduleDaysRequired);
        if (Errors.Count > 0)
            return;

        // The drive's id – for a drive without UUID, its id file is written now.
        SourceDrive? drive = null;
        if (IsDriveSource && SelectedDrive is { } chosen)
        {
            var id = chosen.Id;
            if (id is null && chosen.Root is { } root && _drives is not null)
            {
                try
                {
                    id = await _drives.EnsureIdAsync(root);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Errors.Add(BorgTexts.Format(Strings.JobEditorDriveIdFailed, root, exception.Message));
                    return;
                }
            }
            if (id is null)
            {
                Errors.Add(Strings.JobEditorDriveRequired);
                return;
            }
            drive = new SourceDrive
            {
                Id = id,
                Label = chosen.Label,
                Folders = WholeDrive ? [] : [.. DriveFolders],
                Eject = DriveEject,
                PlugIn = new PlugInBackup
                {
                    Enabled = DrivePlugIn,
                    MinimumHours = (int)Math.Clamp(DrivePlugInHours ?? PlugInBackup.DefaultMinimumHours, 0, 24 * 30),
                },
            };
        }

        Result = (_existing ?? new BackupJob { Id = Guid.NewGuid(), Name = "", RepositoryId = _repository.Id }) with
        {
            Name = Name.Trim(),
            Sources = drive is null ? [.. Sources] : [],
            Drive = drive,
            Exclusions = CurrentExclusions(),
            Compression = SelectedCompression.Value,
            Retention = CleanUp ? retention : null,
            Schedules = schedules,
            CatchUp = CatchUp,
            Notify = Notify,
            WebhookTrigger = WebhookTrigger,
            OverdueDays = (int)Math.Clamp(OverdueDays ?? 0, 0, 365),
            Conditions = new RunConditions
            {
                NotOnBattery = NotOnBattery,
                NotMetered = NotMetered,
                WiredOnly = WiredOnly,
                TargetReachable = TargetReachable,
            },
            Hooks = new JobHooks
            {
                Before = (HookBefore ?? "").Trim(),
                After = (HookAfter ?? "").Trim(),
                Shell = SelectedHookShell.Value,
                ContinueOnBeforeFailure = HookContinueOnFailure,
                AfterAlways = HookAfterAlways,
            },
            RestoreTest = SelectedRestoreTest.Value,
            RestoreTestFiles = (int)Math.Clamp(RestoreTestFiles ?? 20, 1, 1000),
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
        // A drive's folders are relative to its root, as the archive's paths.
        var borgPath = IsDriveSource ? path => SourceDrive.RelativePath(path)
            : _borg is { } borg ? (Func<string, string?>)(path => BorgClient.BorgPath(borg, path)) : path => path;
        var preview = BackupPatterns.Patterns(exclusions, borgPath);
        if (IsDriveSource)
            preview.AddRange(SourceDrive.SystemPatterns);
        if (exclusions.CacheDirTag)
            preview.Insert(0, "--exclude-caches");
        PatternPreview = string.Join("\n", preview);

        Warnings.Clear();
        foreach (var source in IsDriveSource ? Enumerable.Empty<string>() : Sources)
        {
            if (!Directory.Exists(source))
                Warnings.Add(BorgTexts.Format(Strings.JobEditorSourceMissing, source));
            else if (SourceScanner.IsExcluded(source, exclusions))
                Warnings.Add(BorgTexts.Format(Strings.JobEditorSourceExcluded, source));
        }
    }
}
