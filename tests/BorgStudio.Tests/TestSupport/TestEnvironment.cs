using BorgStudio.Core.Borg;

namespace BorgStudio.Tests.TestSupport;

/// <summary>A fresh temporary directory, deleted again after the test.</summary>
public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } =
        Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "borgstudio-tests", Guid.NewGuid().ToString("N"))).FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Locked files (e.g. loaded assemblies on Windows): the temp folder gets cleaned up eventually.
        }
    }
}

/// <summary>Runs only where a supported borg (1.2+) is installed, e.g. in the Linux CI job.</summary>
public sealed class BorgFactAttribute : FactAttribute
{
    public static readonly Lazy<BorgInstallation?> Borg = new(() =>
    {
        var borg = BorgDetector.CreateDefault().DetectAsync().GetAwaiter().GetResult();
        return borg is { Support: BorgSupport.Supported } ? borg : null;
    });

    public BorgFactAttribute()
    {
        if (Borg.Value is null)
            Skip = "No supported borg installed.";
    }
}

/// <summary>
/// Runs against a real SSH server with password login and borg installed (prepared by the Linux CI job):
/// BORGSTUDIO_SSH_TEST_HOST, _USER and _PASSWORD (optionally _PORT).
/// </summary>
public sealed class SshServerFactAttribute : FactAttribute
{
    public static string? Host => Environment.GetEnvironmentVariable("BORGSTUDIO_SSH_TEST_HOST");
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("BORGSTUDIO_SSH_TEST_PORT"), out var port) ? port : 22;
    public static string User => Environment.GetEnvironmentVariable("BORGSTUDIO_SSH_TEST_USER") ?? "";
    public static string Password => Environment.GetEnvironmentVariable("BORGSTUDIO_SSH_TEST_PASSWORD") ?? "";

    public SshServerFactAttribute()
    {
        if (Host is null)
            Skip = "Set BORGSTUDIO_SSH_TEST_HOST/_USER/_PASSWORD to test against an SSH server.";
        else if (BorgFactAttribute.Borg.Value is null)
            Skip = "No supported borg installed.";
    }
}

/// <summary>
/// Runs against the real keychain: always on Windows (Credential Manager), on macOS and Linux only when
/// BORGSTUDIO_KEYCHAIN_TESTS=1 (CI prepares an unlocked keychain there; locally it might pop up dialogs).
/// </summary>
public sealed class KeychainFactAttribute : FactAttribute
{
    public KeychainFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("BORGSTUDIO_KEYCHAIN_TESTS") != "1")
            Skip = "Set BORGSTUDIO_KEYCHAIN_TESTS=1 to test against the keychain.";
    }
}
