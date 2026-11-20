using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;

namespace BorgStudio.App.ViewModels;

/// <summary>User-facing texts for borg results and repository settings.</summary>
internal static class BorgTexts
{
    public static string Describe(BorgError error)
    {
        var text = error.Kind switch
        {
            BorgErrorKind.PassphraseWrong => Strings.BorgErrorPassphraseWrong,
            BorgErrorKind.RepositoryNotFound => Strings.BorgErrorRepositoryNotFound,
            BorgErrorKind.NotARepository => Strings.BorgErrorNotARepository,
            BorgErrorKind.RepositoryExists => Strings.BorgErrorRepositoryExists,
            BorgErrorKind.Locked => Strings.BorgErrorLocked,
            BorgErrorKind.ConnectionFailed => Strings.BorgErrorConnectionFailed,
            BorgErrorKind.SshHostKeyFailed => Strings.BorgErrorSshHostKeyFailed,
            BorgErrorKind.SshAuthenticationFailed => Strings.BorgErrorSshAuthenticationFailed,
            BorgErrorKind.Timeout => Strings.BorgErrorTimeout,
            BorgErrorKind.NotStartable => Strings.BorgErrorNotStartable,
            BorgErrorKind.UnsupportedPath => Strings.BorgErrorUnsupportedPath,
            BorgErrorKind.MountNotAllowed => Strings.BorgErrorMountNotAllowed,
            BorgErrorKind.MountUnavailable => Strings.BorgErrorMountUnavailable,
            BorgErrorKind.NoSpace => Strings.BorgErrorNoSpace,
            _ => Strings.BorgErrorOther,
        };

        // borg's own (English) message helps with everything we don't explain ourselves.
        return error.Message is { Length: > 0 } detail && error.Kind is not BorgErrorKind.PassphraseWrong
            ? $"{text} ({detail})"
            : text;
    }

    public static string PassphraseMode(PassphraseMode mode) => mode switch
    {
        Core.Repositories.PassphraseMode.None => Strings.PassphraseModeNone,
        Core.Repositories.PassphraseMode.Stored => Strings.PassphraseModeStored,
        Core.Repositories.PassphraseMode.Vault => Strings.PassphraseModeVault,
        _ => Strings.PassphraseModeAsk,
    };

    public static string VaultState(Core.Security.VaultState state) => state switch
    {
        Core.Security.VaultState.Unlocked => Strings.VaultStatusUnlocked,
        Core.Security.VaultState.Locked => Strings.VaultStatusLocked,
        _ => Strings.VaultStatusNotSetUp,
    };

    /// <summary>"Täglich 20:00 · Mo, Fr 12:30 · alle 4 Std. ab 08:00".</summary>
    public static string Schedules(IEnumerable<Core.Backups.BackupSchedule> schedules) =>
        string.Join(" · ", schedules.Where(schedule => schedule.IsValid).Select(Schedule));

    public static string Schedule(Core.Backups.BackupSchedule schedule)
    {
        var time = schedule.Time.ToString("t", CultureInfo.CurrentCulture);
        return schedule.Kind switch
        {
            Core.Backups.ScheduleKind.Weekdays => Format(Strings.ScheduleOnDays,
                string.Join(", ", schedule.Days.Distinct().OrderBy(day => ((int)day + 6) % 7)
                    .Select(day => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day))), time),
            Core.Backups.ScheduleKind.EveryHours => Format(Strings.ScheduleEveryHours, schedule.Hours, time),
            _ => Format(Strings.ScheduleDaily, time),
        };
    }

    /// <summary>Why a run did not start.</summary>
    public static string SkipReason(Core.Backups.BackupSkipReason reason) => reason switch
    {
        Core.Backups.BackupSkipReason.RepositoryBusy => Strings.SkipRepositoryBusy,
        Core.Backups.BackupSkipReason.PassphraseAsked => Strings.SkipPassphraseAsked,
        Core.Backups.BackupSkipReason.PassphraseMissing => Strings.SkipPassphraseMissing,
        Core.Backups.BackupSkipReason.ScheduledAccessMissing => Strings.SkipScheduledAccessMissing,
        Core.Backups.BackupSkipReason.TargetMissing => Strings.SkipTargetMissing,
        Core.Backups.BackupSkipReason.BorgMissing => Strings.SkipBorgMissing,
        Core.Backups.BackupSkipReason.RepositoryMissing => Strings.SkipRepositoryMissing,
        Core.Backups.BackupSkipReason.SshKeyMissing => Strings.SkipSshKeyMissing,
        _ => Strings.SkipHostUnknown,
    };

    public static string BackupOutcome(Core.Backups.BackupOutcome outcome) => outcome switch
    {
        Core.Backups.BackupOutcome.Succeeded => Strings.OutcomeSucceeded,
        Core.Backups.BackupOutcome.Warnings => Strings.OutcomeWarnings,
        Core.Backups.BackupOutcome.Cancelled => Strings.OutcomeCancelled,
        Core.Backups.BackupOutcome.Skipped => Strings.OutcomeSkipped,
        _ => Strings.OutcomeFailed,
    };

    /// <summary>"7 täglich, 4 wöchentlich, 6 monatlich"; <c>null</c> without rules.</summary>
    public static string? Retention(Core.Backups.RetentionPolicy? policy)
    {
        if (policy is not { HasRules: true })
            return null;

        var parts = new List<string>();
        void Add(int value, string format)
        {
            if (value > 0)
                parts.Add(Format(format, value));
        }

        Add(policy.WithinDays, Strings.RetentionWithin);
        Add(policy.Last, Strings.RetentionLast);
        Add(policy.Hourly, Strings.RetentionHourly);
        Add(policy.Daily, Strings.RetentionDaily);
        Add(policy.Weekly, Strings.RetentionWeekly);
        Add(policy.Monthly, Strings.RetentionMonthly);
        Add(policy.Yearly, Strings.RetentionYearly);
        return string.Join(", ", parts);
    }

    /// <summary>borg's prune rule ("daily", "secondly" for --keep-last, …) with its number: "täglich #1".</summary>
    public static string PruneRule(string rule, int number) => Format(Strings.PruneRuleFormat, rule switch
    {
        "secondly" => Strings.RuleSecondly,
        "minutely" => Strings.RuleMinutely,
        "hourly" => Strings.RuleHourly,
        "daily" => Strings.RuleDaily,
        "weekly" => Strings.RuleWeekly,
        "monthly" => Strings.RuleMonthly,
        "yearly" => Strings.RuleYearly,
        "within" => Strings.RuleWithin,
        _ => rule,
    }, number);

    /// <summary>The clean-up after a backup (or on its own) in one sentence.</summary>
    public static string Cleanup(Core.Backups.CleanupSummary cleanup) => cleanup switch
    {
        { PrunedArchives: 0, Cancelled: true } => Strings.CleanupCancelled,
        { PrunedArchives: 0, Error: { } error } => Format(Strings.CleanupFailed, error),
        { PrunedArchives: 0 } => Strings.CleanupNothing,
        { Cancelled: true } => Format(Strings.CleanupCompactCancelled, cleanup.PrunedArchives),
        { Error: { } error } => Format(Strings.CleanupCompactFailed, cleanup.PrunedArchives, error),
        { FreedBytes: { } freed } => Format(Strings.CleanupResult, cleanup.PrunedArchives, Size(freed)),
        _ => Format(Strings.CleanupResultNoSize, cleanup.PrunedArchives),
    };

    /// <summary>"120 GB von 1,00 TB belegt · 880 GB frei (88 %)", or the repository's size alone; <c>null</c> if nothing is known.</summary>
    public static string? StorageUsage(Core.Storage.StorageUsage usage) => usage switch
    {
        { Total: { } total, Free: { } free, FreeFraction: { } fraction } => Format(Strings.StorageUsed, Size(total - free), Size(total), Size(free),
            Math.Round(fraction * 100, fraction < 0.1 ? 1 : 0)),
        { RepositoryBytes: { } size } => Format(Strings.StorageRepositoryOnly, Size(size)),
        _ => null,
    };

    /// <summary>
    /// "Wächst um ~11 GB pro Woche → voll voraussichtlich Mitte März 2027 (Spanne Feb. 2027 bis Mai 2027)"; without a total
    /// only the growth, without growth "Wächst derzeit nicht."
    /// </summary>
    public static string Forecast(Core.Storage.StorageForecast forecast, DateTimeOffset now)
    {
        if (!forecast.Growing)
            return Strings.StorageNotGrowing;
        var text = Format(Strings.StorageGrowing, Size((long)Math.Round(forecast.BytesPerWeek)));
        if (forecast.FullAt is not { } at)
            return text;

        var days = (at - now).TotalDays;
        text += " " + (days < 21
            ? Format(Strings.StorageFullIn, Math.Max(1, (int)Math.Round(days)))
            : Format(Strings.StorageFullBy, PartOfMonth(at.ToLocalTime())));
        if (forecast.Earliest is { } earliest && forecast.Latest is { } latest && Month(earliest) != Month(latest))
            text += " " + Format(Strings.StorageFullRange, Month(earliest), Month(latest));
        else if (forecast.Earliest is { } soonest && forecast.Latest is null)
            text += " " + Format(Strings.StorageFullEarliest, Month(soonest));
        return text;
    }

    /// <summary>"Anfang März 2027", "Mitte März 2027", "Ende März 2027".</summary>
    private static string PartOfMonth(DateTimeOffset date) => Format(date.Day switch
    {
        <= 10 => Strings.StoragePartStart,
        <= 20 => Strings.StoragePartMiddle,
        _ => Strings.StoragePartEnd,
    }, date.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

    /// <summary>"März 2027".</summary>
    private static string Month(DateTimeOffset date) => date.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"512 B", "3,4 MB", "1,25 GB" (binary units, current culture).</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>"3,4 MB/s".</summary>
    public static string Rate(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    /// <summary>"normal", "gründlich", "Reparatur".</summary>
    public static string CheckDepth(Core.Checks.CheckDepth depth) => depth switch
    {
        Core.Checks.CheckDepth.Full => Strings.CheckDepthFull,
        Core.Checks.CheckDepth.Repair => Strings.CheckDepthRepair,
        _ => Strings.CheckDepthNormal,
    };

    /// <summary>"0:42", "12:05", "1:02:03".</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    public static string BorgVersion(BorgVersionPreference preference) => preference switch
    {
        BorgVersionPreference.Borg1 => Strings.BorgVersionBorg1,
        BorgVersionPreference.Borg2 => Strings.BorgVersionBorg2,
        _ => Strings.BorgVersionAuto,
    };

    public static string Format(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);
}

/// <summary>A choice in a combo box: value plus localized label.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
