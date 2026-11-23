using System.Xml.Linq;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;

namespace BorgStudio.Tests.Backups;

/// <summary>Schedules every N minutes: around the clock, on the hour and after it – the same in every system scheduler.</summary>
public sealed class MinuteScheduleTests
{
    private static readonly BackupSchedule Quarter = new() { Kind = ScheduleKind.EveryMinutes, Minutes = 15 };

    private static BackupJob Job(BackupSchedule schedule) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "Docs",
        RepositoryId = Guid.NewGuid(),
        Sources = ["/home/me"],
        Schedules = [schedule],
        CatchUp = true,
    };

    [Fact]
    public void Runs_every_n_minutes_on_the_hour()
    {
        var now = new DateTime(2026, 11, 23, 10, 47, 12);

        Assert.Equal(96, Quarter.TimesOfDay().Count);
        Assert.Equal(new DateTime(2026, 11, 23, 11, 0, 0), ScheduleCalendar.Next([Quarter], now));
        Assert.Equal(new DateTime(2026, 11, 23, 10, 45, 0), ScheduleCalendar.Previous([Quarter], now));
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0), ScheduleCalendar.Next([Quarter], new DateTime(2026, 11, 23, 23, 50, 0)));
        Assert.Equal(new DateTime(2026, 11, 23, 10, 48, 0),
            ScheduleCalendar.Next([new BackupSchedule { Kind = ScheduleKind.EveryMinutes, Minutes = 1 }], now));

        Assert.True(ScheduleCalendar.IsDue([Quarter], catchUp: false, new DateTime(2026, 11, 23, 10, 44, 50), new DateTime(2026, 11, 23, 10, 45, 20)));
        Assert.False(ScheduleCalendar.IsDue([Quarter], catchUp: false, new DateTime(2026, 11, 23, 10, 45, 20), new DateTime(2026, 11, 23, 10, 45, 50)));
        // Only steps that divide an hour.
        Assert.False(new BackupSchedule { Kind = ScheduleKind.EveryMinutes, Minutes = 7 }.IsValid);
    }

    [Fact]
    public void Every_system_scheduler_starts_it_at_the_same_times()
    {
        Assert.Equal("*-*-* *:00/15:00", SystemdScheduler.Calendar(Quarter));

        var plist = LaunchdScheduler.JobPlist(Job(Quarter), "/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");
        var minutes = XDocument.Parse(plist).Descendants("dict").Skip(1)
            .Select(dict => dict.Elements().ToList())
            .Where(elements => elements.Count == 2 && elements[0].Value == "Minute")
            .Select(elements => int.Parse(elements[1].Value)).ToList();
        Assert.Equal([0, 15, 30, 45], minutes);
        Assert.DoesNotContain("<key>Hour</key>", plist);

        var xml = XDocument.Parse(WindowsTaskScheduler.TaskXml(Job(Quarter), @"C:\BorgStudio\BorgStudio.exe", new DateTime(2026, 11, 23, 10, 47, 12)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var trigger = xml.Descendants(ns + "CalendarTrigger").Single();
        Assert.Equal("2026-11-23T11:00:00", trigger.Element(ns + "StartBoundary")!.Value);
        Assert.Equal("PT15M", trigger.Element(ns + "Repetition")!.Element(ns + "Interval")!.Value);
        Assert.Null(trigger.Element(ns + "Repetition")!.Element(ns + "Duration")); // without end
    }

    [Fact]
    public void The_editor_offers_the_steps_and_hides_the_time()
    {
        var item = new ScheduleItemViewModel(Quarter, _ => { });
        Assert.True(item.IsEveryMinutes);
        Assert.False(item.HasTime);
        Assert.Equal(BackupSchedule.MinuteSteps, item.MinuteOptions.Select(option => option.Value));
        item.SelectedMinutes = item.MinuteOptions.Single(option => option.Value == 5);
        Assert.Equal(new BackupSchedule { Kind = ScheduleKind.EveryMinutes, Minutes = 5, Time = item.ToSchedule().Time }.Minutes, item.ToSchedule().Minutes);
        Assert.Equal(ScheduleKind.EveryMinutes, item.ToSchedule().Kind);
        Assert.Equal(BorgTexts.Format(BorgStudio.App.Resources.Strings.ScheduleEveryMinutes, 15), BorgTexts.Schedule(Quarter));
    }
}
