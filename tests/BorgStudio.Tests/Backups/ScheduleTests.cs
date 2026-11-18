using System.Xml.Linq;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Processes;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>When scheduled jobs run, and how the systems' task schedulers are told.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly BackupSchedule Daily = new() { Kind = ScheduleKind.Daily, Time = new TimeOnly(20, 0) };
    private static readonly BackupSchedule Weekdays = new() { Kind = ScheduleKind.Weekdays, Time = new TimeOnly(12, 30), Days = [DayOfWeek.Monday, DayOfWeek.Friday] };
    private static readonly BackupSchedule EveryFourHours = new() { Kind = ScheduleKind.EveryHours, Time = new TimeOnly(2, 15), Hours = 4 };

    // Monday, 16 November 2026.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private static BackupJob Job(params BackupSchedule[] schedules) => new()
    {
        Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        Name = "Documents",
        RepositoryId = Guid.NewGuid(),
        Schedules = [.. schedules],
    };

    [Fact]
    public void Every_few_hours_runs_from_its_time_to_midnight()
    {
        Assert.Equal([new TimeOnly(2, 15), new TimeOnly(6, 15), new TimeOnly(10, 15), new TimeOnly(14, 15), new TimeOnly(18, 15), new TimeOnly(22, 15)],
            EveryFourHours.TimesOfDay());
        Assert.Equal([new TimeOnly(20, 0)], Daily.TimesOfDay());
    }

    [Fact]
    public void The_next_run_is_the_earliest_of_all_schedules()
    {
        Assert.Equal(new DateTime(2026, 11, 16, 10, 15, 0), ScheduleCalendar.Next([Daily, Weekdays, EveryFourHours], Monday));
        Assert.Equal(new DateTime(2026, 11, 16, 12, 30, 0), ScheduleCalendar.Next([Daily, Weekdays], Monday));
        // Monday evening: the weekday schedule's next day is Friday, the daily one is earlier.
        Assert.Equal(new DateTime(2026, 11, 20, 12, 30, 0), ScheduleCalendar.Next([Weekdays], Monday.AddHours(3)));
        Assert.Null(ScheduleCalendar.Next([], Monday));
        Assert.Null(ScheduleCalendar.Next([new BackupSchedule { Kind = ScheduleKind.Weekdays }], Monday));
    }

    [Fact]
    public void A_missed_time_is_made_up_for_once()
    {
        var job = Job(Daily) with
        {
            LastRun = new BackupRunSummary { Started = new DateTimeOffset(new DateTime(2026, 11, 14, 20, 0, 5)), Duration = TimeSpan.Zero, Outcome = BackupOutcome.Succeeded },
        };
        // Sunday 20:00 was missed (computer off).
        Assert.True(ScheduleCalendar.IsMissed(job, Monday));
        Assert.Equal(new DateTime(2026, 11, 15, 20, 0, 0), ScheduleCalendar.Previous(job.Schedules, Monday));

        var caughtUp = job with { LastRun = job.LastRun! with { Started = new DateTimeOffset(Monday.AddMinutes(-5)) } };
        Assert.False(ScheduleCalendar.IsMissed(caughtUp, Monday));
        Assert.False(ScheduleCalendar.IsMissed(job with { CatchUp = false }, Monday));
        Assert.True(ScheduleCalendar.IsMissed(job with { LastRun = null }, Monday));
    }

    [Fact]
    public void A_scheduler_that_looks_now_and_then_sees_each_time_once()
    {
        var job = Job(Daily) with { CatchUp = false };
        var evening = new DateTime(2026, 11, 16, 20, 0, 0);
        Assert.False(ScheduleCalendar.IsDue(job, evening.AddMinutes(-1), evening.AddSeconds(-1)));
        Assert.True(ScheduleCalendar.IsDue(job, evening.AddSeconds(-20), evening.AddSeconds(10)));
        // Seen at the last look already.
        Assert.False(ScheduleCalendar.IsDue(job, evening.AddSeconds(10), evening.AddSeconds(40)));
        // Asleep from 19:00 to 23:00: too late without catch-up, made up for with it.
        Assert.False(ScheduleCalendar.IsDue(job, evening.AddHours(-1), evening.AddHours(3)));
        Assert.True(ScheduleCalendar.IsDue(job with { CatchUp = true }, evening.AddHours(-1), evening.AddHours(3)));
    }

    [Fact]
    public void Windows_gets_one_trigger_per_schedule_and_catches_up()
    {
        var xml = WindowsTaskScheduler.TaskXml(Job(Daily, Weekdays, EveryFourHours), @"C:\Tools\Borg Studio\BorgStudio.exe", Monday);
        var task = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var triggers = task.Descendants(ns + "CalendarTrigger").ToList();
        Assert.Equal(3, triggers.Count);
        // Today 20:00 is still ahead; today 12:30 too; 02:15 has passed, so tomorrow.
        Assert.Equal(["2026-11-16T20:00:00", "2026-11-16T12:30:00", "2026-11-17T02:15:00"], triggers.Select(t => t.Element(ns + "StartBoundary")!.Value));
        Assert.Equal(["Monday", "Friday"], triggers[1].Descendants(ns + "DaysOfWeek").Elements().Select(day => day.Name.LocalName));
        Assert.Equal("PT4H", triggers[2].Descendants(ns + "Interval").Single().Value);
        Assert.Equal("true", task.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal(@"C:\Tools\Borg Studio\BorgStudio.exe", task.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--run-job 0f8fad5b-d9cb-469f-a165-70867728950e", task.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"\BorgStudio\0f8fad5bd9cb469fa16570867728950e", task.Descendants(ns + "URI").Single().Value);
    }

    [Fact]
    public async Task Windows_tasks_are_created_and_leftovers_removed()
    {
        var runner = new Commands
        {
            QueryOutput = "\"\\BorgStudio\\0f8fad5bd9cb469fa16570867728950e\",\"N/A\",\"Ready\"\n\"\\BorgStudio\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"N/A\",\"Ready\"\n\"\\Microsoft\\Other\",\"N/A\",\"Ready\"",
        };
        var scheduler = new WindowsTaskScheduler(runner, _directory.Combine("tasks"));

        await scheduler.ApplyAsync([Job(Daily), Job() with { Id = Guid.NewGuid(), Name = "Manual" }], "BorgStudio.exe");

        Assert.Equal("/Query", runner.Calls[0].Arguments[0]);
        Assert.Equal(["/Create", "/TN", @"\BorgStudio\0f8fad5bd9cb469fa16570867728950e", "/XML"], runner.Calls[1].Arguments.Take(4));
        Assert.Equal(["/Delete", "/TN", @"\BorgStudio\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "/F"], runner.Calls[2].Arguments);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Empty(Directory.GetFiles(_directory.Combine("tasks")));
    }

    [Fact]
    public async Task A_refusing_scheduler_is_reported()
    {
        var runner = new Commands { FailOn = "/Create" };
        var scheduler = new WindowsTaskScheduler(runner, _directory.Combine("tasks"));
        var error = await Assert.ThrowsAsync<SchedulerException>(() => scheduler.ApplyAsync([Job(Daily)], "BorgStudio.exe"));
        Assert.Contains("access denied", error.Message);
    }

    [Fact]
    public void Systemd_gets_a_calendar_line_per_schedule()
    {
        var timer = SystemdScheduler.Timer(Job(Daily, Weekdays, EveryFourHours));
        Assert.Contains("OnCalendar=*-*-* 20:00:00\n", timer);
        Assert.Contains("OnCalendar=Mon,Fri *-*-* 12:30:00\n", timer);
        Assert.Contains("OnCalendar=*-*-* 02/4:15:00\n", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("ExecStart=\"/opt/Borg Studio/BorgStudio\" --run-job 0f8fad5b-d9cb-469f-a165-70867728950e",
            SystemdScheduler.Service(Job(Daily), "/opt/Borg Studio/BorgStudio"));
        Assert.Contains("Persistent=false", SystemdScheduler.Timer(Job(Daily) with { CatchUp = false }));
    }

    [Fact]
    public async Task Systemd_units_are_written_enabled_and_removed()
    {
        var units = _directory.Combine("units");
        Directory.CreateDirectory(units);
        File.WriteAllText(Path.Combine(units, "borgstudio-old.timer"), "");
        File.WriteAllText(Path.Combine(units, "borgstudio-old.service"), "");
        var runner = new Commands();

        await new SystemdScheduler(runner, units).ApplyAsync([Job(Daily)], "/opt/BorgStudio/BorgStudio");

        Assert.False(File.Exists(Path.Combine(units, "borgstudio-old.timer")));
        Assert.True(File.Exists(Path.Combine(units, "borgstudio-0f8fad5bd9cb469fa16570867728950e.timer")));
        Assert.Equal(
        [
            "--user disable --now borgstudio-old.timer",
            "--user daemon-reload",
            "--user enable borgstudio-0f8fad5bd9cb469fa16570867728950e.timer",
            "--user restart borgstudio-0f8fad5bd9cb469fa16570867728950e.timer",
        ], runner.Calls.Select(call => string.Join(' ', call.Arguments)));
    }

    [Fact]
    public async Task Launchd_gets_calendar_times_and_a_catch_up_agent()
    {
        var plist = LaunchdScheduler.JobPlist(Job(Weekdays, EveryFourHours), "/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");
        Assert.Contains("<dict><key>Weekday</key><integer>1</integer><key>Hour</key><integer>12</integer><key>Minute</key><integer>30</integer></dict>", plist);
        Assert.Contains("<dict><key>Weekday</key><integer>5</integer>", plist);
        Assert.Contains("<dict><key>Hour</key><integer>22</integer><key>Minute</key><integer>15</integer></dict>", plist);
        Assert.Contains("<string>--run-job</string>", plist);

        var agents = _directory.Combine("agents");
        var runner = new Commands { IdOutput = "501\n" };
        await new LaunchdScheduler(runner, agents).ApplyAsync([Job(Daily)], "/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");
        Assert.True(File.Exists(Path.Combine(agents, "net.cuban8io.borgstudio.0f8fad5bd9cb469fa16570867728950e.plist")));
        Assert.Contains("--catch-up", File.ReadAllText(Path.Combine(agents, "net.cuban8io.borgstudio.catch-up.plist")));
        Assert.Contains(runner.Calls, call => call.Program == "launchctl" && call.Arguments[0] == "bootstrap" && call.Arguments[1] == "gui/501");

        // No scheduled job left: the agents go.
        await new LaunchdScheduler(runner, agents).ApplyAsync([Job()], "/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");
        Assert.Empty(Directory.GetFiles(agents));
    }

    /// <summary>Records the commands a scheduler runs and answers like the real tools.</summary>
    private sealed class Commands : IProcessRunner
    {
        public List<(string Program, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public string QueryOutput { get; init; } = "";

        public string IdOutput { get; init; } = "1000\n";

        public string? FailOn { get; init; }

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null, CancellationToken cancellationToken = default,
            Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null)
        {
            if (fileName == "id")
                return Task.FromResult<ProcessResult?>(new ProcessResult(0, IdOutput, ""));
            Calls.Add((fileName, arguments));
            if (FailOn is not null && arguments.Contains(FailOn))
                return Task.FromResult<ProcessResult?>(new ProcessResult(1, "", "ERROR: access denied"));
            if (arguments is ["/Query", ..])
                return Task.FromResult<ProcessResult?>(new ProcessResult(0, QueryOutput, ""));
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", ""));
        }
    }
}
