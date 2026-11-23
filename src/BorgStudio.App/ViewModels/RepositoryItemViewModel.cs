using BorgStudio.App.Resources;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>A repository in the list and its details panel.</summary>
public sealed partial class RepositoryItemViewModel(RepositoryConfig config, PluginCatalog plugins) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Provider), nameof(ProviderName), nameof(Location), nameof(LocationText),
        nameof(EncryptionText), nameof(IsKeyfile), nameof(IsEncrypted), nameof(PassphraseText), nameof(BorgVersionText),
        nameof(IsSsh), nameof(SshKeyText), nameof(StorageLevelText), nameof(StorageHint), nameof(HasStorageHint), nameof(CheckPlanText),
        nameof(CheckHealth), nameof(CheckHealthText), nameof(HasCheckHealthText))]
    public partial RepositoryConfig Config { get; set; } = config;

    public string Name => Config.Name;

    /// <summary><c>null</c> if the plugin providing it is gone.</summary>
    public IRepositoryProvider? Provider => plugins.FindRepositoryProvider(Config.ProviderId);

    public string ProviderName => Provider?.DisplayName ?? Config.ProviderId;

    /// <summary>The repository's overall state, as the overview judges it (for the dot in the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateIsCritical), nameof(StateIsWarning), nameof(StateIsOk), nameof(StateIsInfo))]
    public partial Core.Backups.HealthLevel StateLevel { get; set; } = Core.Backups.HealthLevel.Info;

    public bool StateIsCritical => StateLevel == Core.Backups.HealthLevel.Critical;

    public bool StateIsWarning => StateLevel == Core.Backups.HealthLevel.Warning;

    public bool StateIsOk => StateLevel == Core.Backups.HealthLevel.Ok;

    public bool StateIsInfo => StateLevel == Core.Backups.HealthLevel.Info;

    public RepositoryLocation? Location
    {
        get
        {
            try
            {
                return Provider?.GetLocation(Config.ProviderValues);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
            {
                return null;
            }
        }
    }

    public string LocationText => Location?.Url ?? BorgTexts.Format(Strings.ProviderMissing, Config.ProviderId);

    public string EncryptionText => Config.EncryptionMode ?? Strings.EncryptionUnknown;

    public bool IsEncrypted => Config.EncryptionMode is not "none";

    public bool IsKeyfile => Config.EncryptionMode?.StartsWith("keyfile", StringComparison.Ordinal) == true;

    public string PassphraseText => BorgTexts.PassphraseMode(Config.PassphraseMode);

    public string BorgVersionText => BorgTexts.BorgVersion(Config.BorgVersion);

    public bool IsSsh => Provider?.UsesSsh == true;

    // --- Storage ---

    /// <summary>The last measurement of the repository's storage; <c>null</c> if never measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorage), nameof(HasStorageBar), nameof(StorageUsedFraction), nameof(StorageText),
        nameof(StorageMeasuredText), nameof(StorageHint), nameof(HasStorageHint), nameof(StorageSegments), nameof(HasStorageSplit),
        nameof(StorageRepositoryText), nameof(StorageOtherText), nameof(StorageFreeText), nameof(StorageRatiosText), nameof(HasStorageRatios))]
    public partial Core.Storage.StorageUsage? Storage { get; set; }

    /// <summary>How fast the storage grows and when it will be full; <c>null</c> without anything to go by.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageForecastText), nameof(HasStorageForecast), nameof(StorageForecastNote), nameof(HasStorageForecastNote),
        nameof(IsForecastSoon))]
    public partial Core.Storage.StorageForecast? Forecast { get; set; }

    /// <summary>The bar's parts: the repository, other data next to it (the rest of the track is free).</summary>
    public IReadOnlyList<Controls.BarSegment> StorageSegments
    {
        get
        {
            if (Storage is not { Total: > 0 and var total } usage || usage.Used is not { } used)
                return [];
            var repository = usage.Source == Core.Storage.StorageSource.Capacity ? used : usage.RepositoryBytes ?? used;
            var other = usage.OtherBytes ?? 0;
            return
            [
                new Controls.BarSegment((double)Math.Min(repository, used) / total, "series"),
                new Controls.BarSegment((double)other / total, "forecast"),
            ];
        }
    }

    /// <summary>The repository and other data are told apart (disk or server with borg's statistics).</summary>
    public bool HasStorageSplit => Storage?.OtherBytes is not null;

    /// <summary>"Dieses Repository 520 GB".</summary>
    public string StorageRepositoryText => Storage is { } usage
        ? BorgTexts.Format(Strings.StorageLegendRepository,
            BorgTexts.Size(usage.Source == Core.Storage.StorageSource.Capacity ? usage.Used ?? 0 : usage.RepositoryBytes ?? usage.Used ?? 0))
        : "";

    /// <summary>"Andere Daten 240 GB".</summary>
    public string StorageOtherText => Storage?.OtherBytes is { } other ? BorgTexts.Format(Strings.StorageLegendOther, BorgTexts.Size(other)) : "";

    /// <summary>"Frei 240 GB von 1000 GB (24 %)".</summary>
    public string StorageFreeText => Storage is { Total: { } total, Free: { } free, FreeFraction: { } fraction }
        ? BorgTexts.Format(Strings.StorageLegendFree, BorgTexts.Size(free), BorgTexts.Size(total), Math.Round(fraction * 100, fraction < 0.1 ? 1 : 0))
        : "";

    /// <summary>"Ersparnis 94 % · Deduplizierung 18,2 : 1 · Kompression 2,1 : 1".</summary>
    public string StorageRatiosText => Storage?.Repository is { Savings: { } savings, DeduplicationRatio: { } dedup, CompressionRatio: { } compression }
        ? BorgTexts.Format(Strings.StorageRatios, Math.Round(savings * 100), dedup.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture),
            compression.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture))
        : "";

    public bool HasStorageRatios => StorageRatiosText.Length > 0;

    /// <summary>"Wächst um ~11 GB pro Woche → voll voraussichtlich Mitte März 2027 (Spanne Feb. 2027 bis Mai 2027)".</summary>
    public string StorageForecastText => Forecast is { } forecast ? BorgTexts.Forecast(forecast, DateTimeOffset.Now) : "";

    public bool HasStorageForecast => Forecast is not null;

    /// <summary>The forecast is only an estimate from the last backups (too few measurements yet).</summary>
    public string? StorageForecastNote => Forecast?.Basis == Core.Storage.ForecastBasis.Backups ? Strings.StorageForecastFromBackups : null;

    public bool HasStorageForecastNote => StorageForecastNote is not null;

    /// <summary>Full within the next 90 days: the forecast is shown as a warning.</summary>
    public bool IsForecastSoon => Forecast?.FullAt is { } at && at - DateTimeOffset.Now < TimeSpan.FromDays(90);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStorageLow), nameof(IsStorageFull), nameof(IsStorageOk), nameof(StorageLevelText))]
    public partial Core.Storage.StorageLevel StorageLevel { get; set; }

    /// <summary>"Weniger als 10 % frei", "Der Speicher ist voll"; empty when all is well.</summary>
    public string StorageLevelText => StorageLevel switch
    {
        Core.Storage.StorageLevel.Low => BorgTexts.Format(Strings.StorageLowText, Config.StorageWarnPercent),
        Core.Storage.StorageLevel.Full => Strings.StorageFullText,
        _ => "",
    };

    public bool HasStorage => Storage is not null;

    public bool HasStorageBar => Storage?.FreeFraction is not null;

    public double StorageUsedFraction => 1 - (Storage?.FreeFraction ?? 1);

    public string StorageText => Storage is { } usage ? BorgTexts.StorageUsage(usage) ?? "" : Strings.StorageNotMeasured;

    /// <summary>"Gemessen 19.11.2026 10:30 (Laufwerk)".</summary>
    public string StorageMeasuredText => Storage is { } usage
        ? BorgTexts.Format(Strings.StorageMeasured, usage.Measured.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            usage.Source switch
            {
                Core.Storage.StorageSource.Disk => Strings.StorageSourceDisk,
                Core.Storage.StorageSource.Server => Strings.StorageSourceServer,
                Core.Storage.StorageSource.Capacity => Strings.StorageSourceCapacity,
                _ => Strings.StorageSourceRepository,
            })
        : "";

    /// <summary>Why there is no bar for a server repository: enter its capacity.</summary>
    public string? StorageHint => Storage is { Source: Core.Storage.StorageSource.RepositoryOnly } && IsSsh ? Strings.StorageCapacityMissing : null;

    public bool HasStorageHint => StorageHint is not null;

    public bool IsStorageOk => StorageLevel == Core.Storage.StorageLevel.Ok;

    public bool IsStorageLow => StorageLevel == Core.Storage.StorageLevel.Low;

    public bool IsStorageFull => StorageLevel == Core.Storage.StorageLevel.Full;

    // --- Checks ---

    /// <summary>The repository's checks, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastVerdict), nameof(HasCheckProblems), nameof(IsCheckOk), nameof(CheckStateText), nameof(CheckDetailText),
        nameof(CheckAttemptText), nameof(HasCheckAttempt), nameof(CheckMessages), nameof(HasCheckMessages), nameof(CheckMessagesHeader),
        nameof(CheckHealth), nameof(CheckHealthText), nameof(HasCheckHealthText))]
    public partial IReadOnlyList<Core.Checks.CheckRecord> Checks { get; set; } = [];

    /// <summary>The last check that ran to its end; <c>null</c> if never checked.</summary>
    public Core.Checks.CheckRecord? LastVerdict => Checks.FirstOrDefault(record => record.IsVerdict);

    public bool HasCheckProblems => LastVerdict?.Outcome == Core.Checks.CheckOutcome.ProblemsFound;

    public bool IsCheckOk => LastVerdict?.Outcome == Core.Checks.CheckOutcome.Ok;

    /// <summary>"Keine Fehler gefunden", "Fehler gefunden", "Noch nie geprüft".</summary>
    public string CheckStateText => LastVerdict?.Outcome switch
    {
        Core.Checks.CheckOutcome.Ok => Strings.CheckStateOk,
        Core.Checks.CheckOutcome.ProblemsFound => Strings.CheckStateProblems,
        _ => Strings.CheckStateNever,
    };

    /// <summary>"Zuletzt geprüft 20.11.2026 14:03 (normal, 3 Min.)".</summary>
    public string CheckDetailText => LastVerdict is { } record
        ? BorgTexts.Format(Strings.CheckLast, record.Started.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            BorgTexts.CheckDepth(record.Depth), BorgTexts.Duration(record.Duration))
        : Strings.CheckNeverHint;

    /// <summary>A later attempt that did not get to the end: "Letzter Versuch 21.11.2026 09:12 abgebrochen".</summary>
    public string CheckAttemptText => Checks.FirstOrDefault() is { IsVerdict: false, Outcome: not Core.Checks.CheckOutcome.Repaired } attempt
        ? BorgTexts.Format(attempt.Outcome == Core.Checks.CheckOutcome.Cancelled ? Strings.CheckAttemptCancelled : Strings.CheckAttemptFailed,
            attempt.Started.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), attempt.Error ?? "")
        : "";

    public bool HasCheckAttempt => CheckAttemptText.Length > 0;

    /// <summary>"Automatisch: nach einem Backup, wenn 30 Tage nicht geprüft; nach Zeitplan, nächste So. 06.12. 03:00".</summary>
    public string CheckPlanText
    {
        get
        {
            var policy = Config.Check;
            var parts = new List<string>();
            if (policy.AfterBackup)
                parts.Add(BorgTexts.Format(Strings.CheckPlanAfterBackup, policy.IntervalDays));
            if (policy.IsScheduled && Core.Backups.ScheduleCalendar.Next(policy.Schedules, DateTime.Now) is { } next)
                parts.Add(BorgTexts.Format(Strings.CheckPlanScheduled,
                    next.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture) + " " + next.ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
            return parts.Count == 0 ? Strings.CheckPlanNone : BorgTexts.Format(Strings.CheckPlan, string.Join("; ", parts));
        }
    }

    /// <summary>How the repository is doing as far as its checks tell (damage, a check that could not run, overdue).</summary>
    public (Core.Backups.HealthLevel Level, Core.Checks.CheckHealthReason Reason) CheckHealth =>
        Core.Checks.CheckHealth.Evaluate(Config.Check, Checks, DateTime.Now);

    /// <summary>"Die Prüfung ist überfällig." – a warning about the checks; empty when there is none (damage has its own line).</summary>
    public string CheckHealthText => CheckHealth.Reason switch
    {
        Core.Checks.CheckHealthReason.NotRun => Strings.CheckHealthNotRun,
        Core.Checks.CheckHealthReason.Overdue => Strings.CheckHealthOverdue,
        _ => "",
    };

    public bool HasCheckHealthText => CheckHealthText.Length > 0;

    /// <summary>What borg reported in the last completed check.</summary>
    public IReadOnlyList<string> CheckMessages => LastVerdict?.Messages ?? [];

    public bool HasCheckMessages => CheckMessages.Count > 0;

    /// <summary>"Meldungen von borg (12)".</summary>
    public string CheckMessagesHeader => BorgTexts.Format(Strings.CheckMessagesHeader, CheckMessages.Count);

    /// <summary>Name of the key in the vault; <c>null</c> while unknown (locked vault) or if the key is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshKeyText))]
    public partial string? SshKeyName { get; set; }

    public string SshKeyText => Config switch
    {
        { HasLegacySshKey: true } => Strings.SshKeyLegacy,
        { SshKeyId: null } => Strings.SshKeyMissing,
        _ when SshKeyName is null => Strings.SshKeyMissing,
        { SshKeyRestricted: true } => $"{SshKeyName} {Strings.SshKeyRestrictedSuffix}",
        _ => SshKeyName,
    };

    /// <summary>Since when borg reports the repository locked; <c>null</c> once it got through again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    public partial DateTimeOffset? LockedSince { get; set; }

    /// <summary>borg found the repository locked: "Sperre lösen …" is offered.</summary>
    public bool IsLocked => LockedSince is not null;

    /// <summary>Outcome of the last action on this repository (test, key export, ...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(HasSuccess), nameof(HasError))]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess), nameof(HasError))]
    public partial bool ResultIsError { get; private set; }

    public bool HasResult => ResultText is not null;

    public bool HasSuccess => HasResult && !ResultIsError;

    public bool HasError => HasResult && ResultIsError;

    public void ShowResult(string text, bool isError)
    {
        ResultIsError = isError;
        ResultText = text;
    }
}
