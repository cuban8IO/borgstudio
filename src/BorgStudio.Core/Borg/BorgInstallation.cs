namespace BorgStudio.Core.Borg;

public enum BorgRuntime
{
    /// <summary>borg runs directly on the host.</summary>
    Native,

    /// <summary>borg runs inside the Windows Subsystem for Linux.</summary>
    Wsl,
}

/// <summary>A borg executable that was found and answered <c>borg --version</c>.</summary>
/// <param name="Path">Path of the executable; inside WSL for <see cref="BorgRuntime.Wsl"/>.</param>
public sealed record BorgInstallation(string Path, BorgVersion Version, BorgRuntime Runtime)
{
    public BorgSupport Support => BorgCompatibility.Evaluate(Version);
}
