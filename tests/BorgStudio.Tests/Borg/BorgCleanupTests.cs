using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Tests.Borg;

/// <summary>borg prune and compact: the keep rules, the arguments and reading borg's output.</summary>
public class BorgCleanupTests
{
    private static readonly BorgInstallation NativeBorg = new("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Native);

    private const string KeepId = "3289a50a66be17669eff18e6acf5730576c02eec768e0bf0ddda075857d112e8";
    private const string PruneId = "5da562bd7d8a5a9ca4d16cc8c623dc595e1d715ec6585dad2793f6fd2f3f0568";

    /// <summary>Output of borg 1.4.4 (prune --list --log-json).</summary>
    private static string ListLine(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "log_message", ["time"] = 1793995577.33, ["message"] = message, ["levelname"] = "INFO", ["name"] = "borg.output.list",
        });

    /// <summary>Answers with fixed standard error lines and records the call.</summary>
    private sealed class LinesRunner(params string[] lines) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public IReadOnlyDictionary<string, string?>? Environment { get; private set; }

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
            CancellationToken cancellationToken = default, Action<string>? standardErrorLine = null)
        {
            // Linux/macOS run interruptible calls through "sh -c <wrapper> borg …".
            Arguments = fileName == "sh" ? [.. arguments.Skip(3)] : arguments;
            Environment = environment;
            foreach (var line in lines)
                standardErrorLine?.Invoke(line);
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", string.Join('\n', lines)));
        }
    }

    [Fact]
    public void Retention_rules_become_keep_options()
    {
        Assert.False(new RetentionPolicy().HasRules);
        Assert.Empty(new RetentionPolicy().Arguments());
        Assert.Equal(["--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "6"], RetentionPolicy.Suggested.Arguments());

        var all = new RetentionPolicy { WithinDays = 2, Last = 3, Hourly = 24, Daily = 7, Weekly = 4, Monthly = 12, Yearly = 5 };
        Assert.True(all.HasRules);
        Assert.Equal(
            ["--keep-within", "2d", "--keep-last", "3", "--keep-hourly", "24", "--keep-daily", "7", "--keep-weekly", "4",
             "--keep-monthly", "12", "--keep-yearly", "5"],
            all.Arguments());
    }

    [Fact]
    public void Prune_list_lines_tell_kept_and_deleted_archives()
    {
        var kept = BorgClient.ParsePruneLine(ListLine(
            $"Keeping archive (rule: daily #1):         Docs-NB-NFI-2026-11-06T10:00:00         Fri, 2026-11-06 21:06:16 [{KeepId}]"));
        Assert.Equal(new BorgPruneEntry("Docs-NB-NFI-2026-11-06T10:00:00", KeepId,
            new DateTimeOffset(new DateTime(2026, 11, 6, 21, 6, 16)), Kept: true, "daily", 1), kept);

        var wouldPrune = BorgClient.ParsePruneLine(ListLine(
            $"Would prune:                                 Docs-NB-NFI-2026-11-05T10:00:00         Thu, 2026-11-05 21:06:15 [{PruneId}]"));
        Assert.Equal(new BorgPruneEntry("Docs-NB-NFI-2026-11-05T10:00:00", PruneId,
            new DateTimeOffset(new DateTime(2026, 11, 5, 21, 6, 15)), Kept: false), wouldPrune);

        var pruning = BorgClient.ParsePruneLine(ListLine(
            $"Pruning archive (1/2):                       Docs-NB-NFI-2026-11-05T10:00:00         Thu, 2026-11-05 21:06:15 [{PruneId}]"));
        Assert.False(pruning!.Kept);
        Assert.Equal(PruneId, pruning.Id);

        var checkpoint = BorgClient.ParsePruneLine(ListLine(
            $"Keeping checkpoint archive:                  Docs-NB-NFI-2026-11-07T10:00:00.checkpoint Sat, 2026-11-07 10:00:00 [{KeepId}]"));
        Assert.True(checkpoint!.IsCheckpoint);
        Assert.Equal("Docs-NB-NFI-2026-11-07T10:00:00.checkpoint", checkpoint.Name);

        // borg 1.4 marks the oldest archive kept to fill up a rule.
        var oldest = BorgClient.ParsePruneLine(ListLine(
            $"Keeping archive (rule: monthly[oldest] #3):  Docs with space         Mon, 2026-01-05 08:00:00 [{KeepId}]"));
        Assert.Equal(("monthly", 3, "Docs with space"), (oldest!.Rule, oldest.RuleNumber, oldest.Name));

        Assert.Null(BorgClient.ParsePruneLine("""{"type": "log_message", "message": "Keeping archive", "levelname": "INFO", "name": "borg.archiver"}"""));
        Assert.Null(BorgClient.ParsePruneLine(ListLine("something else entirely")));
        Assert.Null(BorgClient.ParsePruneLine("not json borg.output.list"));
    }

    [Fact]
    public void Compact_progress_and_freed_space_are_read()
    {
        Assert.Equal(0.5, BorgClient.ParseCompactProgress(
            """{"message": "Compacting segments  25%", "current": 2, "total": 4, "info": null, "operation": 1, "msgid": "repository.compact_segments", "type": "progress_percent", "finished": false, "time": 1.0}"""));
        Assert.Equal(1, BorgClient.ParseCompactProgress(
            """{"operation": 1, "msgid": "repository.compact_segments", "type": "progress_percent", "finished": true, "time": 1.0}"""));
        Assert.Null(BorgClient.ParseCompactProgress("""{"type": "log_message", "message": "progress_percent"}"""));

        static string Freed(string size) =>
            $$"""{"type": "log_message", "time": 1.0, "message": "compaction freed about {{size}} repository space.", "levelname": "INFO", "name": "borg.repository"}""";
        Assert.Equal(601, BorgClient.ParseCompactFreed(Freed("601 B")));
        Assert.Equal(5350, BorgClient.ParseCompactFreed(Freed("5.35 kB")));
        Assert.Equal(1_230_000_000, BorgClient.ParseCompactFreed(Freed("1.23 GB")));
        Assert.Equal(0, BorgClient.ParseCompactFreed(Freed("-12 B")));
        Assert.Null(BorgClient.ParseCompactFreed("""{"type": "log_message", "message": "compaction freed nothing useful"}"""));
    }

    [Fact]
    public async Task Prune_looks_at_the_glob_only_and_reads_the_list()
    {
        var runner = new LinesRunner(
            ListLine($"Would prune:                                 Docs-host-1                          Thu, 2026-11-05 21:06:15 [{PruneId}]"),
            ListLine($"Keeping archive (rule: daily #1):            Docs-host-2                          Fri, 2026-11-06 21:06:16 [{KeepId}]"),
            """{"type": "log_message", "message": "unrelated", "levelname": "WARNING", "name": "borg.archiver"}""");

        var result = await new BorgClient(runner).PruneAsync(NativeBorg, new RepositoryLocation("/repo"), "Docs-{hostname}-*",
            RetentionPolicy.Suggested.Arguments(), dryRun: true, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["prune", "--log-json", "--dry-run", "--list", "--glob-archives", "Docs-{hostname}-*", "--keep-daily", "7", "--keep-weekly",
             "4", "--keep-monthly", "6", "/repo"],
            runner.Arguments);
        Assert.Equal("secret", runner.Environment!["BORG_PASSPHRASE"]);
        Assert.Equal(["Docs-host-2", "Docs-host-1"], result.Value!.Archives.Select(archive => archive.Name)); // newest first
        Assert.Equal("Docs-host-1", Assert.Single(result.Value.Pruned).Name);
    }

    [Fact]
    public async Task Compact_needs_no_passphrase_and_reports_progress_and_freed_space()
    {
        var runner = new LinesRunner(
            """{"message": "Compacting segments  50%", "current": 1, "total": 2, "msgid": "repository.compact_segments", "type": "progress_percent", "finished": false, "time": 1.0}""",
            """{"type": "log_message", "time": 1.0, "message": "compaction freed about 2.44 kB repository space.", "levelname": "INFO", "name": "borg.repository"}""");
        var progress = new List<double>();

        var result = await new BorgClient(runner).CompactAsync(NativeBorg, new RepositoryLocation("/repo"),
            progress: new SynchronousProgress<double>(progress.Add));

        Assert.True(result.Succeeded);
        Assert.Equal(["compact", "--log-json", "--info", "--progress", "/repo"], runner.Arguments);
        Assert.Equal("", runner.Environment!["BORG_PASSPHRASE"]);
        Assert.Equal([0.5], progress);
        Assert.Equal(2440, result.Value!.FreedBytes);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
