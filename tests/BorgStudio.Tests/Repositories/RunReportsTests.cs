using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Repositories;

/// <summary>Reports of runs for providers that want them: queued in files, delivered in order, kept while they cannot go.</summary>
public sealed class RunReportsTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly ReportingProvider _provider = new();
    private readonly RunReports _reports;
    private readonly RepositoryConfig _repository;
    private readonly RepositoryConfig _other;

    public RunReportsTests()
    {
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        _repository = Repository("Server", "reporting");
        _other = Repository("Other", "reporting");
        var local = Repository("Local", "local");
        _test.Store.Save([_repository, _other, local]);
        _reports = RunReports.ForDataDirectory(_test.Directory.Path, PluginCatalog.Load([new ReportingPlugin(_provider)], "no-plugins-here"), _test.Store);
    }

    public void Dispose() => _test.Dispose();

    /// <summary>The fake provider tells repositories apart by the id in their values.</summary>
    private RepositoryConfig Repository(string name, string provider)
    {
        var id = Guid.NewGuid();
        return new RepositoryConfig
        {
            Id = id,
            Name = name,
            ProviderId = provider,
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo"), ["id"] = id.ToString() },
            EncryptionMode = "repokey-blake2",
        };
    }

    private static RunReport Report(RunReportKind kind, int minute) =>
        new(kind, RunReportOutcome.Succeeded, new DateTimeOffset(2026, 11, 25, 10, minute, 0, TimeSpan.Zero), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Reports_wait_per_repository_and_go_in_order()
    {
        Assert.True(_reports.Add(_repository.Id, Report(RunReportKind.Backup, 0)));
        Assert.True(_reports.Add(_other.Id, Report(RunReportKind.Check, 1)));
        Assert.True(_reports.Add(_repository.Id, Report(RunReportKind.Prune, 2)));
        // Providers without reports, and unknown repositories: nothing queued.
        Assert.False(_reports.Add(_test.Store.Load().Repositories.Single(r => r.Name == "Local").Id, Report(RunReportKind.Backup, 3)));
        Assert.False(_reports.Add(Guid.NewGuid(), Report(RunReportKind.Backup, 3)));
        Assert.Equal(3, _reports.Pending);

        // The first repository's service is down: its reports wait (in order), the other one's go.
        _provider.Unavailable.Add(_repository.Id);
        Assert.Equal(1, await _reports.SendAsync());
        Assert.Equal([(_other.Id, RunReportKind.Check)], _provider.Received);
        Assert.Equal(2, _reports.Pending);

        _provider.Unavailable.Clear();
        Assert.Equal(2, await _reports.SendAsync());
        Assert.Equal([RunReportKind.Backup, RunReportKind.Prune], _provider.Received.Skip(1).Select(received => received.Kind));
        Assert.Equal(0, _reports.Pending);
    }

    [Fact]
    public async Task Refused_reports_and_those_of_removed_repositories_are_dropped()
    {
        _reports.Add(_repository.Id, Report(RunReportKind.Backup, 0));
        _reports.Add(_other.Id, Report(RunReportKind.Backup, 1));
        _provider.Rejected.Add(_repository.Id);
        _test.Store.Save([_repository]);

        Assert.Equal(0, await _reports.SendAsync());

        Assert.Equal(0, _reports.Pending);
        Assert.Empty(_provider.Received);
    }

    [Fact]
    public void Only_the_newest_reports_are_kept()
    {
        for (var i = 0; i < RunReports.Limit + 5; i++)
            _reports.Add(_repository.Id, Report(RunReportKind.Check, i % 60));
        Assert.Equal(RunReports.Limit, _reports.Pending);
    }

    [Fact]
    public async Task A_backup_with_clean_up_reports_backup_prune_and_compact()
    {
        _test.Borg.Archives.Add(("Docs-host-2026-11-01T10:00:00", new DateTime(2026, 11, 1, 10, 0, 0)));
        _test.Borg.Archives.Add(("Docs-host-2026-11-02T10:00:00", new DateTime(2026, 11, 2, 10, 0, 0)));
        _test.Borg.Archives.Add(("Docs-host-2026-11-03T10:00:00", new DateTime(2026, 11, 3, 10, 0, 0)));
        Directory.CreateDirectory(_test.Directory.Combine("Docs"));
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Docs",
            RepositoryId = _repository.Id,
            Sources = [_test.Directory.Combine("Docs")],
            Retention = new RetentionPolicy { Daily = 1 },
        };
        _test.Jobs.Save([job]);
        var runner = new BackupRunner(_test.Services.BorgClient, _test.Jobs, _test.Durations, error => error.ToString(), reports: _reports);

        var result = await runner.RunAsync(new BackupRunRequest(FakeBorg.Installation, new RepositoryLocation(_test.Directory.Combine("repo")), job, "pw", null));

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(3, await _reports.SendAsync());
        Assert.Equal([RunReportKind.Prune, RunReportKind.Compact, RunReportKind.Backup], _provider.Received.Select(received => received.Kind));
        var backup = _provider.Reports[^1];
        Assert.Equal(result.Summary.ArchiveName, backup.ArchiveName);
        Assert.Equal(result.Repository?.UniqueCompressedSize, backup.UniqueCompressedSize);
    }

    [Fact]
    public void Runs_become_reports()
    {
        var started = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        Assert.Null(RunReports.FromBackup(new BackupRunSummary { Started = started, Duration = TimeSpan.Zero, Outcome = BackupOutcome.Skipped }, null));
        var failed = RunReports.FromBackup(new BackupRunSummary
        {
            Started = started, Duration = TimeSpan.FromMinutes(1), Outcome = BackupOutcome.Failed, Message = "No space left",
        }, null)!;
        Assert.Equal((RunReportKind.Backup, RunReportOutcome.Failed, "No space left"), (failed.Kind, failed.Outcome, failed.Message));

        Assert.Equal(RunReportOutcome.Failed, RunReports.FromCheck(new CheckRecord(started, TimeSpan.Zero, CheckDepth.Normal, CheckOutcome.ProblemsFound)
        {
            Messages = ["segment 12 damaged"],
        }).Outcome);
        Assert.Equal(RunReportOutcome.Warnings, RunReports.FromCheck(new CheckRecord(started, TimeSpan.Zero, CheckDepth.Repair, CheckOutcome.Repaired)).Outcome);
        Assert.Equal(RunReportOutcome.Cancelled, RunReports.Cleanup(RunReportKind.Compact, started, TimeSpan.Zero, cancelled: true, null).Outcome);
    }

    private sealed class ReportingPlugin(ReportingProvider provider) : IBorgStudioPlugin
    {
        public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(provider);
    }

    /// <summary>A provider whose service takes reports; some repositories unreachable or refused.</summary>
    private sealed class ReportingProvider : IRepositoryProvider, IRunReportReceiver
    {
        public HashSet<Guid> Unavailable { get; } = [];

        public HashSet<Guid> Rejected { get; } = [];

        public List<(Guid Repository, RunReportKind Kind)> Received { get; } = [];

        public List<RunReport> Reports { get; } = [];

        public string Id => "reporting";

        public string DisplayName => "Reporting";

        public string Description => "";

        public IReadOnlyList<ProviderField> Fields => [new("path", "Path", ProviderFieldKind.FolderPath)];

        public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values) => [];

        public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) => new(values["path"]);

        public Task SendReportAsync(RunReportRequest request, CancellationToken cancellationToken)
        {
            var repository = Guid.Parse(request.Values["id"]);
            if (Unavailable.Contains(repository))
                throw new RepositoryUnavailableException("down");
            if (Rejected.Contains(repository))
                throw new RunReportRejectedException("gone");
            Received.Add((repository, request.Report.Kind));
            Reports.Add(request.Report);
            return Task.CompletedTask;
        }
    }
}
