using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>
/// Runs borg 1.x commands on a repository – natively or, on Windows, inside WSL – without ever
/// letting borg ask on a terminal: the passphrase comes from the environment, confirmations are pre-answered.
/// </summary>
public sealed partial class BorgClient(IProcessRunner processRunner, string? wslExecutable = null)
{
    // The first access to a big repository builds borg's cache, which can take a while.
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeyExportTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] ForwardedVariables =
    [
        "BORG_PASSPHRASE", "BORG_DISPLAY_PASSPHRASE",
        "BORG_RELOCATED_REPO_ACCESS_IS_OK", "BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK",
    ];

    public static BorgClient CreateDefault() => new(
        new ProcessRunner(),
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "wsl.exe") : null);

    public async Task<BorgResult<BorgRepositoryInfo>> InfoAsync(
        BorgInstallation borg, RepositoryLocation location, string? passphrase, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ["info"], ["--json"], [], passphrase, InfoTimeout, cancellationToken);
        if (!result.Succeeded)
            return BorgResult<BorgRepositoryInfo>.Failure(result.Error!);

        try
        {
            return BorgResult<BorgRepositoryInfo>.Success(ParseInfo(result.Value!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return BorgResult<BorgRepositoryInfo>.Failure(new BorgError(BorgErrorKind.Other, exception.Message));
        }
    }

    public async Task<BorgResult<bool>> InitAsync(
        BorgInstallation borg, RepositoryLocation location, BorgEncryption encryption, string? passphrase,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ["init"],
            [$"--encryption={EncryptionName(encryption)}", "--make-parent-dirs"], [], passphrase, InitTimeout, cancellationToken);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    /// <summary>Writes the repository key to <paramref name="targetFile"/> (a path on this computer).</summary>
    public async Task<BorgResult<bool>> ExportKeyAsync(
        BorgInstallation borg, RepositoryLocation location, string targetFile, CancellationToken cancellationToken = default)
    {
        var target = borg.Runtime == BorgRuntime.Wsl ? ToWslPath(targetFile) : targetFile;
        if (target is null)
            return BorgResult<bool>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, targetFile));

        var result = await RunAsync(borg, location, ["key", "export"], [], [target], null, KeyExportTimeout, cancellationToken);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    public static string EncryptionName(BorgEncryption encryption) => encryption switch
    {
        BorgEncryption.RepokeyBlake2 => "repokey-blake2",
        BorgEncryption.KeyfileBlake2 => "keyfile-blake2",
        BorgEncryption.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(encryption)),
    };

    /// <summary>
    /// Windows path as seen from WSL ("C:\Backups\repo" -> "/mnt/c/Backups/repo"); <c>null</c> for paths WSL cannot
    /// reach this way (network shares).
    /// </summary>
    public static string? ToWslPath(string windowsPath)
    {
        var match = DrivePath().Match(windowsPath);
        if (!match.Success)
            return null;

        var drive = char.ToLowerInvariant(match.Groups["drive"].Value[0]);
        var rest = match.Groups["rest"].Value.Replace('\\', '/').TrimEnd('/');
        return rest.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
    }

    /// <summary>
    /// <c>borg &lt;command&gt; --log-json &lt;provider arguments&gt; &lt;options&gt; &lt;repository&gt; &lt;trailing&gt;</c>;
    /// returns standard output on success.
    /// </summary>
    private async Task<BorgResult<string>> RunAsync(
        BorgInstallation borg, RepositoryLocation location,
        IReadOnlyList<string> command, IReadOnlyList<string> options, IReadOnlyList<string> trailing,
        string? passphrase, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var wsl = borg.Runtime == BorgRuntime.Wsl;
        var repository = location.Url;
        if (wsl && !IsUrl(repository))
        {
            if (ToWslPath(repository) is not { } translated)
                return BorgResult<string>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, location.Url));
            repository = translated;
        }

        List<string> arguments = [.. command, "--log-json", .. location.BorgArguments, .. options, repository, .. trailing];

        var environment = new Dictionary<string, string?>
        {
            // Always set, so borg never prompts for it: an encrypted repository with an empty passphrase fails fast.
            ["BORG_PASSPHRASE"] = passphrase ?? "",
            ["BORG_DISPLAY_PASSPHRASE"] = "no",
            // The user picked the location: accept a moved repository (e.g. an external disk with a new drive letter)
            // and unencrypted repositories instead of asking on a terminal the GUI doesn't have.
            ["BORG_RELOCATED_REPO_ACCESS_IS_OK"] = "yes",
            ["BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK"] = "yes",
        };

        string fileName;
        if (wsl)
        {
            if (wslExecutable is null)
                return BorgResult<string>.Failure(new BorgError(BorgErrorKind.NotStartable, "wsl.exe"));

            fileName = wslExecutable;
            arguments.InsertRange(0, ["-e", borg.Path]);
            // WSL only passes on the Windows environment variables listed in WSLENV.
            var existing = Environment.GetEnvironmentVariable("WSLENV");
            environment["WSLENV"] = string.Join(':', new[] { existing }.Where(value => !string.IsNullOrEmpty(value)).Concat(ForwardedVariables));
        }
        else
        {
            fileName = borg.Path;
        }

        ProcessResult? result;
        try
        {
            result = await processRunner.RunAsync(fileName, arguments, timeout, environment, cancellationToken);
        }
        catch (TimeoutException)
        {
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.Timeout));
        }

        if (result is null)
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.NotStartable, fileName));

        // 0 = success, 1 = success with warnings, everything else is an error.
        return result.ExitCode is 0 or 1
            ? BorgResult<string>.Success(result.StandardOutput)
            : BorgResult<string>.Failure(ParseError(result.StandardError));
    }

    /// <summary>The last error from borg's JSON log on standard error, classified by its message id.</summary>
    internal static BorgError ParseError(string standardError)
    {
        string? messageId = null;
        string? message = null;
        var plainText = new List<string>();

        foreach (var line in standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.GetProperty("type").GetString() != "log_message"
                    || root.GetProperty("levelname").GetString() is not ("ERROR" or "CRITICAL"))
                    continue;

                message = root.TryGetProperty("message", out var text) ? text.GetString() : message;
                if (root.TryGetProperty("msgid", out var id) && id.GetString() is { Length: > 0 } value)
                    messageId = value;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                plainText.Add(line);
            }
        }

        var kind = messageId switch
        {
            null => BorgErrorKind.Other,
            _ when messageId.Contains("PassphraseWrong", StringComparison.Ordinal) => BorgErrorKind.PassphraseWrong,
            _ when messageId.Contains("DoesNotExist", StringComparison.Ordinal) => BorgErrorKind.RepositoryNotFound,
            _ when messageId.Contains("InvalidRepository", StringComparison.Ordinal) => BorgErrorKind.NotARepository,
            _ when messageId.Contains("AlreadyExists", StringComparison.Ordinal) => BorgErrorKind.RepositoryExists,
            _ when messageId.Contains("Lock", StringComparison.Ordinal) => BorgErrorKind.Locked,
            _ when messageId.Contains("ConnectionClosed", StringComparison.Ordinal) => BorgErrorKind.ConnectionFailed,
            _ => BorgErrorKind.Other,
        };
        return new BorgError(kind, message ?? (plainText.Count > 0 ? string.Join(Environment.NewLine, plainText) : null));
    }

    internal static BorgRepositoryInfo ParseInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var repository = document.RootElement.GetProperty("repository");
        var encryption = document.RootElement.GetProperty("encryption");

        DateTimeOffset? lastModified = null;
        if (repository.TryGetProperty("last_modified", out var modified)
            && DateTimeOffset.TryParse(modified.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            lastModified = parsed;

        return new BorgRepositoryInfo(
            repository.GetProperty("id").GetString()!,
            encryption.GetProperty("mode").GetString()!,
            lastModified);
    }

    private static bool IsUrl(string repository) => repository.Contains("://", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<drive>[A-Za-z]):[\\/]?(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();
}
