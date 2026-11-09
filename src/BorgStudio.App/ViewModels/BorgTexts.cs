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

    public static string BackupOutcome(Core.Backups.BackupOutcome outcome) => outcome switch
    {
        Core.Backups.BackupOutcome.Succeeded => Strings.OutcomeSucceeded,
        Core.Backups.BackupOutcome.Warnings => Strings.OutcomeWarnings,
        Core.Backups.BackupOutcome.Cancelled => Strings.OutcomeCancelled,
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
