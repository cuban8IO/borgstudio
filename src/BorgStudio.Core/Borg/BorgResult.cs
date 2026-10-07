namespace BorgStudio.Core.Borg;

/// <summary>What went wrong in a borg command, as far as the UI needs to tell the user.</summary>
public enum BorgErrorKind
{
    PassphraseWrong,
    RepositoryNotFound,
    NotARepository,

    /// <summary>The target already holds a repository or other files.</summary>
    RepositoryExists,

    /// <summary>Another borg process holds the repository lock.</summary>
    Locked,

    ConnectionFailed,
    Timeout,

    /// <summary>borg (or wsl.exe) could not be started.</summary>
    NotStartable,

    /// <summary>A path that cannot be handed to borg (e.g. a network share for borg inside WSL).</summary>
    UnsupportedPath,

    Other,
}

/// <param name="Message">borg's own message (English), shown as detail.</param>
public sealed record BorgError(BorgErrorKind Kind, string? Message = null);

public sealed record BorgResult<T>(T? Value, BorgError? Error)
{
    public bool Succeeded => Error is null;

    public static BorgResult<T> Success(T value) => new(value, null);

    public static BorgResult<T> Failure(BorgError error) => new(default, error);
}

/// <summary>Encryption modes offered when creating a repository (borg 1.x names).</summary>
public enum BorgEncryption
{
    /// <summary>Key stored in the repository, protected by the passphrase (borg's recommendation).</summary>
    RepokeyBlake2,

    /// <summary>Key stored only on this computer – it must be backed up separately.</summary>
    KeyfileBlake2,

    None,
}

/// <summary>From <c>borg info --json</c>.</summary>
/// <param name="EncryptionMode">e.g. "repokey-blake2", "keyfile-blake2", "none".</param>
public sealed record BorgRepositoryInfo(string Id, string EncryptionMode, DateTimeOffset? LastModified);
