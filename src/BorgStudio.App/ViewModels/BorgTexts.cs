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
