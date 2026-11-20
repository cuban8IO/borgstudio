using System.Text.Json;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>Where <c>borg check</c> is.</summary>
public enum CheckPhase
{
    /// <summary>Reading the repository's segments (on the server for SSH repositories).</summary>
    Segments,

    /// <summary>Downloading and decrypting all data (<see cref="CheckDepth.Full"/> only).</summary>
    Data,

    /// <summary>Going through the archives' metadata.</summary>
    Archives,
}

/// <summary>A progress update of <c>borg check</c>: its phase and how far it is in it (0..1).</summary>
public sealed record BorgCheckProgress(CheckPhase Phase, double Fraction);

/// <summary>The outcome of a <c>borg check</c> that ran to its end.</summary>
/// <param name="ProblemsFound">borg found damage (with <see cref="CheckDepth.Repair"/>: found and repaired as far as it could).</param>
/// <param name="Messages">What borg reported as errors and warnings, in its words.</param>
public sealed record BorgCheckResult(bool ProblemsFound, IReadOnlyList<string> Messages);

public sealed partial class BorgClient
{
    /// <summary>borg asks before repairing unless this says YES.</summary>
    private const string RepairConfirmation = "BORG_CHECK_I_KNOW_WHAT_I_AM_DOING";

    /// <summary>The messages a check keeps at most (a badly damaged repository yields one per object).</summary>
    internal const int MaximumCheckMessages = 200;

    /// <summary>
    /// <c>borg check</c>: checks the repository and its archives (<see cref="CheckDepth.Normal"/>), also reads and
    /// decrypts all data (<see cref="CheckDepth.Full"/>), or repairs what it can (<see cref="CheckDepth.Repair"/> – the
    /// caller must have asked the user; borg's own question is answered with YES). Damage found is a result, not an
    /// error: <see cref="BorgCheckResult.ProblemsFound"/> (borg exits with 1 and logs errors). A normal or full check can
    /// be cancelled cleanly; a repair runs to its end.
    /// </summary>
    public async Task<BorgResult<BorgCheckResult>> CheckAsync(
        BorgInstallation borg, RepositoryLocation location, CheckDepth depth, string? passphrase, SshAccess? ssh = null,
        IProgress<BorgCheckProgress>? progress = null, CancellationToken cancellationToken = default, BorgThrottle? throttle = null)
    {
        var messages = new List<string>();
        var errors = false;
        void OnLine(string line)
        {
            if (ParseProgressPercent(line) is { } update)
            {
                if (CheckPhaseOf(update.MessageId) is { } phase)
                    progress?.Report(new BorgCheckProgress(phase, update.Finished ? 1 : update.Fraction));
            }
            else if (ParseCheckMessage(line) is { } message)
            {
                errors |= message.IsError;
                if (messages.Count < MaximumCheckMessages)
                    messages.Add(message.Text);
            }
        }

        List<string> options = ["--progress"];
        if (depth == CheckDepth.Full)
            options.Add("--verify-data");
        if (depth == CheckDepth.Repair)
            options.Add("--repair");
        var code = 0;
        var result = await RunAsync(borg, location, ssh, ["check"], options, [], passphrase, CreateTimeout, cancellationToken,
            standardErrorLine: OnLine, interruptible: depth != CheckDepth.Repair,
            extraEnvironment: depth == CheckDepth.Repair ? new Dictionary<string, string> { [RepairConfirmation] = "YES" } : null,
            exitCode: exit => code = exit, throttle: throttle);
        if (!result.Succeeded)
            return BorgResult<BorgCheckResult>.Failure(result.Error!);
        // borg check exits with 1 ("warning") when it found damage.
        return BorgResult<BorgCheckResult>.Success(new BorgCheckResult(errors || code == 1, messages));
    }

    private static CheckPhase? CheckPhaseOf(string? messageId) => messageId switch
    {
        "repository.check" => CheckPhase.Segments,
        "check.verify_data" => CheckPhase.Data,
        null => null,
        _ when messageId.StartsWith("check.", StringComparison.Ordinal) => CheckPhase.Archives,
        _ => null,
    };

    /// <summary>An error or warning <c>borg check</c> logged; <c>null</c> for any other line.</summary>
    internal static (string Text, bool IsError)? ParseCheckMessage(string line)
    {
        if (!line.Contains("\"log_message\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "log_message"
                || !root.TryGetProperty("levelname", out var levelElement) || levelElement.GetString() is not { } level
                || level is not ("WARNING" or "ERROR" or "CRITICAL")
                || !root.TryGetProperty("message", out var text) || text.GetString() is not { Length: > 0 } message)
                return null;
            return (message.TrimEnd(), level != "WARNING");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
