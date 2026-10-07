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
        _ => Strings.PassphraseModeAsk,
    };

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
