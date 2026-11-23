using System.Xml.Linq;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>Monthly schedules (the nth weekday, a day of the month) and scheduled checks in the system's schedulers.</summary>
public sealed class MonthlyScheduleTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly BackupSchedule FirstSunday = new() { Kind = ScheduleKind.MonthlyWeekday, Week = 1, Weekday = DayOfWeek.Sunday, Time = new TimeOnly(3, 0) };

    private static readonly BackupSchedule LastFriday = new() { Kind = ScheduleKind.MonthlyWeekday, Week = BackupSchedule.Last, Weekday = DayOfWeek.Friday, Time = new TimeOnly(22, 30) };

    private static readonly BackupSchedule Fifth = new() { Kind = ScheduleKind.MonthlyDay, DayOfMonth = 5, Time = new TimeOnly(3, 0) };

    private static readonly BackupSchedule LastDay = new() { Kind = ScheduleKind.MonthlyDay, DayOfMonth = BackupSchedule.Last, Time = new TimeOnly(23, 0) };

    private static RepositoryConfig Repository(params BackupSchedule[] schedules) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "NAS",
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = "/backups" },
        Check = new CheckPolicy { Schedules = [.. schedules] },
    };

    [Theory]
    [InlineData("2026-12-06", true)]   // the first Sunday of December 2026
    [InlineData("2026-12-13", false)]
    [InlineData("2026-12-05", false)]
    [InlineData("2027-02-07", true)]
    public void The_first_sunday(string date, bool runs) => Assert.Equal(runs, FirstSunday.RunsOn(DateTime.Parse(date)));

    [Theory]
    [InlineData("2026-11-27", true)]   // the last Friday of November 2026
    [InlineData("2026-11-20", false)]
    [InlineData("2027-01-29", true)]
    public void The_last_friday(string date, bool runs) => Assert.Equal(runs, LastFriday.RunsOn(DateTime.Parse(date)));

    [Theory]
    [InlineData("2027-02-28", true)]
    [InlineData("2028-02-28", false)]  // a leap year
    [InlineData("2028-02-29", true)]
    [InlineData("2026-11-30", true)]
    [InlineData("2026-12-30", false)]
    public void The_last_day(string date, bool runs) => Assert.Equal(runs, LastDay.RunsOn(DateTime.Parse(date)));

    [Fact]
    public void Finds_the_next_and_previous_monthly_time()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);

        Assert.Equal(new DateTime(2026, 12, 6, 3, 0, 0), ScheduleCalendar.Next([FirstSunday], now));
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0), ScheduleCalendar.Previous([FirstSunday], now));
        Assert.Equal(new DateTime(2026, 12, 5, 3, 0, 0), ScheduleCalendar.Next([Fifth], now));
        Assert.Equal(new DateTime(2026, 11, 30, 23, 0, 0), ScheduleCalendar.Next([LastDay], now));
        Assert.True(ScheduleCalendar.IsScheduledNow([FirstSunday], new DateTime(2026, 12, 6, 3, 1, 0)));
        Assert.False(ScheduleCalendar.IsScheduledNow([FirstSunday], new DateTime(2026, 12, 13, 3, 1, 0)));
    }

    [Fact]
    public void Monthly_schedules_need_a_valid_week_or_day()
    {
        Assert.True(FirstSunday.IsValid);
        Assert.True(LastDay.IsValid);
        Assert.False((FirstSunday with { Week = 5 }).IsValid);
        Assert.False((Fifth with { DayOfMonth = 30 }).IsValid);
    }

    [Fact]
    public void Windows_gets_monthly_triggers_and_a_task_for_the_check()
    {
        var task = ScheduledTask.ForCheck(Repository(FirstSunday, LastDay));
        var xml = XDocument.Parse(WindowsTaskScheduler.TaskXml(task, @"C:\BorgStudio\BorgStudio.exe", new DateTime(2026, 11, 20, 9, 0, 0)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var triggers = xml.Descendants(ns + "CalendarTrigger").ToList();
        Assert.Equal(2, triggers.Count);
        var byWeekday = triggers[0].Element(ns + "ScheduleByMonthDayOfWeek")!;
        Assert.Equal("1", byWeekday.Element(ns + "Weeks")!.Element(ns + "Week")!.Value);
        Assert.NotNull(byWeekday.Element(ns + "DaysOfWeek")!.Element(ns + "Sunday"));
        Assert.Equal(12, byWeekday.Element(ns + "Months")!.Elements().Count());
        Assert.Equal("Last", triggers[1].Element(ns + "ScheduleByMonth")!.Element(ns + "DaysOfMonth")!.Element(ns + "Day")!.Value);
        Assert.Equal("2026-11-21T03:00:00", triggers[0].Element(ns + "StartBoundary")!.Value);
        Assert.Equal(@"\BorgStudio\check-11111111222233334444555555555555", xml.Descendants(ns + "URI").Single().Value);
        Assert.Equal("--check-repository 11111111-2222-3333-4444-555555555555", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("true", xml.Descendants(ns + "StartWhenAvailable").Single().Value);
    }

    [Theory]
    [InlineData(ScheduleKind.MonthlyWeekday, 1, 0, "Sun *-*-01..07 03:00:00")]
    [InlineData(ScheduleKind.MonthlyWeekday, 3, 0, "Sun *-*-15..21 03:00:00")]
    [InlineData(ScheduleKind.MonthlyWeekday, BackupSchedule.Last, 0, "Sun *-*~07/1 03:00:00")]
    [InlineData(ScheduleKind.MonthlyDay, 0, 5, "*-*-05 03:00:00")]
    [InlineData(ScheduleKind.MonthlyDay, 0, BackupSchedule.Last, "*-*~01 03:00:00")]
    public void Systemd_calendar_expressions(ScheduleKind kind, int week, int day, string expected)
    {
        var schedule = new BackupSchedule { Kind = kind, Week = week == 0 ? 1 : week, DayOfMonth = day == 0 ? 1 : day, Time = new TimeOnly(3, 0) };

        Assert.Equal(expected, SystemdScheduler.Calendar(schedule));
    }

    [Fact]
    public void Launchd_starts_what_it_cannot_express_more_often_and_lets_the_run_decide()
    {
        var weekday = LaunchdScheduler.TaskPlist(ScheduledTask.ForCheck(Repository(FirstSunday)), "/BorgStudio");
        Assert.Contains("<key>Weekday</key><integer>0</integer><key>Hour</key><integer>3</integer>", weekday);
        Assert.Contains("<string>--if-due</string>", weekday);
        Assert.Contains("<string>--check-repository</string>", weekday);

        var fifth = LaunchdScheduler.TaskPlist(ScheduledTask.ForCheck(Repository(Fifth)), "/BorgStudio");
        Assert.Contains("<key>Day</key><integer>5</integer>", fifth);
        Assert.DoesNotContain("--if-due", fifth);

        var last = LaunchdScheduler.TaskPlist(ScheduledTask.ForCheck(Repository(LastDay)), "/BorgStudio");
        Assert.Equal(4, last.Split("<key>Day</key>").Length - 1);
        Assert.Contains("<string>--if-due</string>", last);
    }

    [Fact]
    public async Task Scheduled_checks_get_their_own_entries_next_to_the_jobs()
    {
        var units = _directory.Combine("units");

        await new SystemdScheduler(new RecordingProcesses(), units).ApplyAsync([], "/opt/BorgStudio/BorgStudio", null,
            [Repository(FirstSunday), Repository() with { Id = Guid.NewGuid(), Name = "Unscheduled" }]);

        var service = File.ReadAllText(Path.Combine(units, "borgstudio-check-11111111222233334444555555555555.service"));
        Assert.Contains("--check-repository 11111111-2222-3333-4444-555555555555", service);
        Assert.Contains("OnCalendar=Sun *-*-01..07 03:00:00", File.ReadAllText(Path.Combine(units, "borgstudio-check-11111111222233334444555555555555.timer")));
        Assert.Single(Directory.GetFiles(units, "*.timer"));

        // Without schedules the entry goes.
        await new SystemdScheduler(new RecordingProcesses(), units).ApplyAsync([], "/opt/BorgStudio/BorgStudio", null, [Repository()]);
        Assert.Empty(Directory.GetFiles(units, "*.timer"));
    }
}
