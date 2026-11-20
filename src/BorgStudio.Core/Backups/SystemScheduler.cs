using System.Globalization;
using System.Security;
using System.Text;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Backups;

public sealed class SchedulerException(string message) : Exception(message);

/// <summary>
/// The operating system's task scheduler, which starts <c>BorgStudio --run-job &lt;job&gt;</c> by itself – also while
/// BorgStudio is closed (as long as the user is logged in). One entry per scheduled job, named after its id.
/// </summary>
public interface ISystemScheduler
{
    /// <summary>
    /// Brings the system's entries for BorgStudio in line with <paramref name="jobs"/>: one per job with a schedule,
    /// pointing at <paramref name="executable"/>; entries of jobs without schedule (or deleted ones) go. With
    /// <paramref name="dailyCheck"/>, one more entry runs <c>--daily-check</c> at that time every day (made up for when
    /// missed). Each of <paramref name="repositories"/> with check schedules gets an entry running <c>--check-repository</c>.
    /// </summary>
    /// <exception cref="SchedulerException">The system refused a change.</exception>
    Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, TimeOnly? dailyCheck = null,
        IReadOnlyList<RepositoryConfig>? repositories = null, CancellationToken cancellationToken = default);
}

/// <summary>One entry in the system's scheduler: what BorgStudio is started with, and when.</summary>
/// <param name="Id">The entry's name: a job's id, "check-" and a repository's id, or "daily-check".</param>
public sealed record ScheduledTask(string Id, string Description, IReadOnlyList<BackupSchedule> Schedules, bool CatchUp,
    IReadOnlyList<string> Arguments)
{
    public static ScheduledTask ForJob(BackupJob job) =>
        new(SystemScheduler.Id(job.Id), $"BorgStudio: backup job \"{job.Name}\"", job.Schedules, job.CatchUp,
            [ScheduledArguments.RunJob, job.Id.ToString()]);

    public static ScheduledTask ForCheck(RepositoryConfig repository) =>
        new(SystemScheduler.CheckId(repository.Id), $"BorgStudio: check of repository \"{repository.Name}\"", repository.Check.Schedules,
            repository.Check.CatchUp, [ScheduledArguments.CheckRepository, repository.Id.ToString()]);

    public static ScheduledTask DailyCheck(TimeOnly time) =>
        new(SystemScheduler.DailyCheckId, "BorgStudio: daily check (notifications)",
            [new BackupSchedule { Kind = ScheduleKind.Daily, Time = time }], CatchUp: true, [SystemScheduler.DailyCheckArgument]);

    /// <summary>The entries for the jobs and repositories with schedules, and the daily check.</summary>
    public static List<ScheduledTask> All(IEnumerable<BackupJob> jobs, IEnumerable<RepositoryConfig>? repositories, TimeOnly? dailyCheck)
    {
        List<ScheduledTask> tasks = [.. jobs.Where(job => job.IsScheduled).Select(ForJob)];
        tasks.AddRange((repositories ?? []).Where(repository => repository.Check.IsScheduled).Select(ForCheck));
        if (dailyCheck is { } time)
            tasks.Add(DailyCheck(time));
        return tasks;
    }
}

/// <summary>How the system's scheduler starts BorgStudio.</summary>
public static class ScheduledArguments
{
    /// <summary><c>--run-job &lt;job id&gt;</c>: one backup job.</summary>
    public const string RunJob = "--run-job";

    /// <summary><c>--check-repository &lt;repository id&gt;</c>: a normal check of one repository.</summary>
    public const string CheckRepository = "--check-repository";

    /// <summary>Everything with "catch up" whose last scheduled time passed since it last ran.</summary>
    public const string CatchUp = "--catch-up";

    /// <summary>
    /// Added where the scheduler cannot express a monthly time itself (launchd starts such runs on more days): the run
    /// only goes ahead if one of its times lies within the last day.
    /// </summary>
    public const string IfDue = "--if-due";
}

public static class SystemScheduler
{
    /// <summary>The scheduler of this system: Windows Task Scheduler, launchd on macOS, systemd user timers on Linux.</summary>
    public static ISystemScheduler CreateDefault(IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return new WindowsTaskScheduler(runner, Path.Combine(AppPaths.DataDirectory, "tasks"));
        if (OperatingSystem.IsMacOS())
            return new LaunchdScheduler(runner, Path.Combine(home, "Library", "LaunchAgents"));
        return new SystemdScheduler(runner, Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"),
            "systemd", "user"));
    }

    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<ProcessResult> RunAsync(IProcessRunner runner, string program, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, bool mayFail = false)
    {
        var result = await runner.RunAsync(program, arguments, CommandTimeout, cancellationToken: cancellationToken)
            ?? throw new SchedulerException($"{program} could not be started.");
        if (result.ExitCode != 0 && !mayFail)
        {
            var output = (result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput).Trim();
            throw new SchedulerException($"{program} {string.Join(' ', arguments.Take(2))}: {output}");
        }
        return result;
    }

    internal static string Id(Guid jobId) => jobId.ToString("N");

    /// <summary>The name of a repository's check entry.</summary>
    internal static string CheckId(Guid repositoryId) => $"check-{repositoryId:N}";

    /// <summary>The name of the daily check's entry (next to the jobs' ids).</summary>
    public const string DailyCheckId = "daily-check";

    /// <summary>Starts the daily check (summary of the day, overdue jobs).</summary>
    public const string DailyCheckArgument = "--daily-check";
}

/// <summary>Windows Task Scheduler: a task per entry in the folder \BorgStudio, registered with schtasks from XML.</summary>
public sealed class WindowsTaskScheduler(IProcessRunner runner, string workDirectory) : ISystemScheduler
{
    public const string Folder = @"\BorgStudio\";

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, TimeOnly? dailyCheck = null,
        IReadOnlyList<RepositoryConfig>? repositories = null, CancellationToken cancellationToken = default)
    {
        var existing = await ListAsync(cancellationToken);
        foreach (var task in ScheduledTask.All(jobs, repositories, dailyCheck))
        {
            await RegisterAsync(task.Id, TaskXml(task, executable, DateTime.Now), cancellationToken);
            existing.Remove(task.Id);
        }
        foreach (var leftover in existing)
            await SystemScheduler.RunAsync(runner, "schtasks", ["/Delete", "/TN", Folder + leftover, "/F"], cancellationToken);
    }

    private async Task RegisterAsync(string id, string xml, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var file = Path.Combine(workDirectory, $"{id}.xml");
        // The format schtasks expects (UTF-16 with byte order mark).
        await File.WriteAllTextAsync(file, xml, Encoding.Unicode, cancellationToken);
        try
        {
            await SystemScheduler.RunAsync(runner, "schtasks", ["/Create", "/TN", Folder + id, "/XML", file, "/F"], cancellationToken);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static string TaskName(Guid jobId) => Folder + SystemScheduler.Id(jobId);

    /// <summary>The daily check: every day at <paramref name="time"/>, made up for when the computer was off.</summary>
    public static string DailyCheckXml(TimeOnly time, string executable, DateTime now) =>
        TaskXml(ScheduledTask.DailyCheck(time), executable, now);

    /// <summary>The ids of BorgStudio's tasks that exist now.</summary>
    private async Task<HashSet<string>> ListAsync(CancellationToken cancellationToken)
    {
        // No task in the folder yet: schtasks says so with an error.
        var result = await SystemScheduler.RunAsync(runner, "schtasks", ["/Query", "/FO", "CSV", "/NH"], cancellationToken, mayFail: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var name = line.Trim().Split(',').FirstOrDefault()?.Trim('"') ?? "";
            if (name.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) && name.Length > Folder.Length)
                names.Add(name[Folder.Length..]);
        }
        return names;
    }

    /// <summary>The task definition: one trigger per schedule; missed runs follow when the computer is back (catch-up).</summary>
    public static string TaskXml(BackupJob job, string executable, DateTime now) => TaskXml(ScheduledTask.ForJob(job), executable, now);

    public static string TaskXml(ScheduledTask task, string executable, DateTime now)
    {
        var xml = new StringBuilder();
        xml.AppendLine("""<?xml version="1.0" encoding="UTF-16"?>""");
        xml.AppendLine("""<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""");
        xml.AppendLine("  <RegistrationInfo>");
        xml.AppendLine($"    <Description>{Escape(task.Description)}</Description>");
        xml.AppendLine($"    <URI>{Escape(Folder + task.Id)}</URI>");
        xml.AppendLine("  </RegistrationInfo>");
        xml.AppendLine("  <Triggers>");
        foreach (var schedule in task.Schedules.Where(schedule => schedule.IsValid))
        {
            // The first start after now, so registering never counts as a missed run.
            var start = now.Date + schedule.Time.ToTimeSpan();
            if (start <= now)
                start = start.AddDays(1);
            xml.AppendLine("    <CalendarTrigger>");
            if (schedule.Kind == ScheduleKind.EveryHours)
            {
                xml.AppendLine("      <Repetition>");
                xml.AppendLine($"        <Interval>PT{schedule.Hours}H</Interval>");
                xml.AppendLine($"        <Duration>PT{(int)(TimeSpan.FromDays(1) - schedule.Time.ToTimeSpan()).TotalMinutes}M</Duration>");
                xml.AppendLine("        <StopAtDurationEnd>false</StopAtDurationEnd>");
                xml.AppendLine("      </Repetition>");
            }
            xml.AppendLine($"      <StartBoundary>{start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}</StartBoundary>");
            xml.AppendLine("      <Enabled>true</Enabled>");
            switch (schedule.Kind)
            {
                case ScheduleKind.Weekdays:
                    xml.AppendLine("      <ScheduleByWeek>");
                    xml.AppendLine("        <DaysOfWeek>");
                    foreach (var day in schedule.Days.Distinct().Order())
                        xml.AppendLine($"          <{day} />");
                    xml.AppendLine("        </DaysOfWeek>");
                    xml.AppendLine("        <WeeksInterval>1</WeeksInterval>");
                    xml.AppendLine("      </ScheduleByWeek>");
                    break;
                case ScheduleKind.MonthlyDay:
                    xml.AppendLine("      <ScheduleByMonth>");
                    xml.AppendLine("        <DaysOfMonth>");
                    xml.AppendLine($"          <Day>{(schedule.DayOfMonth == BackupSchedule.Last ? "Last" : schedule.DayOfMonth.ToString(CultureInfo.InvariantCulture))}</Day>");
                    xml.AppendLine("        </DaysOfMonth>");
                    AppendMonths(xml);
                    xml.AppendLine("      </ScheduleByMonth>");
                    break;
                case ScheduleKind.MonthlyWeekday:
                    xml.AppendLine("      <ScheduleByMonthDayOfWeek>");
                    xml.AppendLine("        <Weeks>");
                    xml.AppendLine($"          <Week>{(schedule.Week == BackupSchedule.Last ? "Last" : schedule.Week.ToString(CultureInfo.InvariantCulture))}</Week>");
                    xml.AppendLine("        </Weeks>");
                    xml.AppendLine("        <DaysOfWeek>");
                    xml.AppendLine($"          <{schedule.Weekday} />");
                    xml.AppendLine("        </DaysOfWeek>");
                    AppendMonths(xml);
                    xml.AppendLine("      </ScheduleByMonthDayOfWeek>");
                    break;
                default:
                    xml.AppendLine("      <ScheduleByDay>");
                    xml.AppendLine("        <DaysInterval>1</DaysInterval>");
                    xml.AppendLine("      </ScheduleByDay>");
                    break;
            }
            xml.AppendLine("    </CalendarTrigger>");
        }
        xml.AppendLine("  </Triggers>");
        xml.AppendLine("  <Principals>");
        xml.AppendLine("""    <Principal id="Author">""");
        xml.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        xml.AppendLine("      <RunLevel>LeastPrivilege</RunLevel>");
        xml.AppendLine("    </Principal>");
        xml.AppendLine("  </Principals>");
        xml.AppendLine("  <Settings>");
        xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        xml.AppendLine($"    <StartWhenAvailable>{(task.CatchUp ? "true" : "false")}</StartWhenAvailable>");
        xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        xml.AppendLine("    <IdleSettings>");
        xml.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        xml.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        xml.AppendLine("    </IdleSettings>");
        xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        xml.AppendLine("    <Enabled>true</Enabled>");
        xml.AppendLine("    <Hidden>false</Hidden>");
        xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        xml.AppendLine("    <Priority>7</Priority>");
        xml.AppendLine("  </Settings>");
        xml.AppendLine("""  <Actions Context="Author">""");
        xml.AppendLine("    <Exec>");
        xml.AppendLine($"      <Command>{Escape(executable)}</Command>");
        xml.AppendLine($"      <Arguments>{Escape(string.Join(' ', task.Arguments))}</Arguments>");
        xml.AppendLine("    </Exec>");
        xml.AppendLine("  </Actions>");
        xml.AppendLine("</Task>");
        return xml.ToString();
    }

    /// <summary>Monthly triggers name the months they run in: all of them.</summary>
    private static void AppendMonths(StringBuilder xml)
    {
        xml.AppendLine("        <Months>");
        foreach (var month in CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(name => name.Length > 0))
            xml.AppendLine($"          <{month} />");
        xml.AppendLine("        </Months>");
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}

/// <summary>
/// launchd on macOS: a LaunchAgent per entry with its calendar times. launchd makes up for times missed while the Mac
/// slept; for times missed while it was off, one more agent runs <c>--catch-up</c> at login. Monthly times launchd cannot
/// express (the nth weekday, the last day) start on more days with <c>--if-due</c>, which lets only the right day go ahead.
/// </summary>
public sealed class LaunchdScheduler(IProcessRunner runner, string agentsDirectory) : ISystemScheduler
{
    public const string Prefix = "net.cuban8io.borgstudio.";

    public const string CatchUpLabel = Prefix + "catch-up";

    public const string DailyCheckLabel = Prefix + SystemScheduler.DailyCheckId;

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, TimeOnly? dailyCheck = null,
        IReadOnlyList<RepositoryConfig>? repositories = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(agentsDirectory);
        var domain = $"gui/{(await SystemScheduler.RunAsync(runner, "id", ["-u"], cancellationToken)).StandardOutput.Trim()}";
        var tasks = ScheduledTask.All(jobs, repositories, dailyCheck);
        var wanted = tasks.ToDictionary(task => Prefix + task.Id, task => TaskPlist(task, executable));
        // At login: what was missed while the Mac was off (the catch-up also makes up for a missed daily check).
        if (tasks.Any(task => task.CatchUp))
            wanted[CatchUpLabel] = Plist(CatchUpLabel, [executable, ScheduledArguments.CatchUp], runAtLoad: true, calendar: "");

        foreach (var file in Directory.EnumerateFiles(agentsDirectory, Prefix + "*.plist"))
        {
            var label = Path.GetFileNameWithoutExtension(file);
            await SystemScheduler.RunAsync(runner, "launchctl", ["bootout", $"{domain}/{label}"], cancellationToken, mayFail: true);
            if (!wanted.ContainsKey(label))
                File.Delete(file);
        }
        foreach (var (label, plist) in wanted)
        {
            var file = Path.Combine(agentsDirectory, label + ".plist");
            await File.WriteAllTextAsync(file, plist, cancellationToken);
            await SystemScheduler.RunAsync(runner, "launchctl", ["bootstrap", domain, file], cancellationToken);
        }
    }

    public static string JobPlist(BackupJob job, string executable) => TaskPlist(ScheduledTask.ForJob(job), executable);

    public static string TaskPlist(ScheduledTask task, string executable)
    {
        var entries = new StringBuilder();
        foreach (var schedule in task.Schedules.Where(schedule => schedule.IsValid))
        {
            foreach (var (weekday, day) in CalendarDays(schedule))
            {
                foreach (var time in schedule.TimesOfDay())
                {
                    entries.Append("      <dict>");
                    if (weekday is { } dayOfWeek)
                        entries.Append($"<key>Weekday</key><integer>{(int)dayOfWeek}</integer>");
                    if (day is { } dayOfMonth)
                        entries.Append($"<key>Day</key><integer>{dayOfMonth}</integer>");
                    entries.Append($"<key>Hour</key><integer>{time.Hour}</integer><key>Minute</key><integer>{time.Minute}</integer></dict>\n");
                }
            }
        }
        List<string> arguments = [executable, .. task.Arguments];
        if (task.Schedules.Any(schedule => schedule.IsValid && NeedsDueCheck(schedule)))
            arguments.Add(ScheduledArguments.IfDue);
        // The daily check keeps its single calendar entry (as before).
        var calendar = task.Id == SystemScheduler.DailyCheckId
            ? $"    <key>StartCalendarInterval</key>\n    <dict><key>Hour</key><integer>{task.Schedules[0].Time.Hour}</integer><key>Minute</key><integer>{task.Schedules[0].Time.Minute}</integer></dict>\n"
            : $"    <key>StartCalendarInterval</key>\n    <array>\n{entries}    </array>\n";
        return Plist(Prefix + task.Id, arguments, runAtLoad: false, calendar);
    }

    /// <summary>
    /// The days launchd starts a schedule on: (weekday, day of month), either may be left out. launchd has no "first
    /// Sunday" or "last day": those start every such weekday, or on days 28 to 31, and <c>--if-due</c> picks the right one.
    /// </summary>
    private static IEnumerable<(DayOfWeek? Weekday, int? Day)> CalendarDays(BackupSchedule schedule) => schedule.Kind switch
    {
        ScheduleKind.Weekdays => schedule.Days.Distinct().Order().Select(day => ((DayOfWeek?)day, (int?)null)),
        ScheduleKind.MonthlyWeekday => [(schedule.Weekday, null)],
        ScheduleKind.MonthlyDay when schedule.DayOfMonth == BackupSchedule.Last => Enumerable.Range(28, 4).Select(day => ((DayOfWeek?)null, (int?)day)),
        ScheduleKind.MonthlyDay => [(null, schedule.DayOfMonth)],
        _ => [(null, null)],
    };

    private static bool NeedsDueCheck(BackupSchedule schedule) =>
        schedule.Kind == ScheduleKind.MonthlyWeekday || schedule is { Kind: ScheduleKind.MonthlyDay, DayOfMonth: BackupSchedule.Last };

    internal static string Plist(string label, IReadOnlyList<string> arguments, bool runAtLoad, string calendar) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Escape(label)}</string>
            <key>ProgramArguments</key>
            <array>
        {string.Concat(arguments.Select(argument => $"        <string>{Escape(argument)}</string>\n"))}    </array>
            <key>RunAtLoad</key>
            <{(runAtLoad ? "true" : "false")}/>
        {calendar}</dict>
        </plist>

        """.ReplaceLineEndings("\n"); // A Windows checkout gives the template CRLF; the files are Unix files.

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}

/// <summary>
/// systemd on Linux: a user service and timer per entry; <c>Persistent=true</c> makes up for times missed while the
/// computer was off.
/// </summary>
public sealed class SystemdScheduler(IProcessRunner runner, string unitDirectory) : ISystemScheduler
{
    public const string Prefix = "borgstudio-";

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, TimeOnly? dailyCheck = null,
        IReadOnlyList<RepositoryConfig>? repositories = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(unitDirectory);
        var wanted = ScheduledTask.All(jobs, repositories, dailyCheck)
            .ToDictionary(task => Prefix + task.Id, task => (Service: Service(task, executable), Timer: Timer(task)));

        foreach (var timer in Directory.EnumerateFiles(unitDirectory, Prefix + "*.timer"))
        {
            var unit = Path.GetFileNameWithoutExtension(timer);
            if (wanted.ContainsKey(unit))
                continue;
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "disable", "--now", unit + ".timer"], cancellationToken, mayFail: true);
            File.Delete(timer);
            File.Delete(Path.Combine(unitDirectory, unit + ".service"));
        }
        foreach (var (unit, files) in wanted)
        {
            await File.WriteAllTextAsync(Path.Combine(unitDirectory, unit + ".service"), files.Service, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(unitDirectory, unit + ".timer"), files.Timer, cancellationToken);
        }

        await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "daemon-reload"], cancellationToken);
        foreach (var unit in wanted.Keys)
        {
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "enable", unit + ".timer"], cancellationToken);
            // Picks up changed times of a timer that was running already.
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "restart", unit + ".timer"], cancellationToken);
        }
    }

    public static string Service(BackupJob job, string executable) => Service(ScheduledTask.ForJob(job), executable);

    public static string DailyCheckService(string executable) => Service(ScheduledTask.DailyCheck(default), executable);

    public static string Service(ScheduledTask task, string executable) => $"""
        [Unit]
        Description={UnitText(task.Description)}

        [Service]
        Type=oneshot
        ExecStart="{executable.Replace("\"", "\\\"", StringComparison.Ordinal)}" {string.Join(' ', task.Arguments)}

        """.ReplaceLineEndings("\n");

    public static string DailyCheckTimer(TimeOnly time) => Timer(ScheduledTask.DailyCheck(time));

    public static string Timer(BackupJob job) => Timer(ScheduledTask.ForJob(job));

    public static string Timer(ScheduledTask task)
    {
        var lines = new StringBuilder();
        foreach (var schedule in task.Schedules.Where(schedule => schedule.IsValid))
            lines.Append($"OnCalendar={Calendar(schedule)}\n");
        return $"""
            [Unit]
            Description=Schedule of {UnitText(task.Description)}

            [Timer]
            {lines}Persistent={(task.CatchUp ? "true" : "false")}

            [Install]
            WantedBy=timers.target

            """.ReplaceLineEndings("\n");
    }

    /// <summary>systemd's calendar expression, e.g. "Mon,Fri *-*-* 12:30:00", "Sun *-*-01..07 03:00:00" (first Sunday).</summary>
    internal static string Calendar(BackupSchedule schedule)
    {
        var time = schedule.Kind == ScheduleKind.EveryHours
            ? $"{schedule.Time.Hour:00}/{schedule.Hours}:{schedule.Time.Minute:00}:00"
            : $"{schedule.Time.Hour:00}:{schedule.Time.Minute:00}:00";
        return schedule.Kind switch
        {
            ScheduleKind.Weekdays => $"{string.Join(',', schedule.Days.Distinct().Order().Select(Abbreviation))} *-*-* {time}",
            // "~07/1": every one of the last seven days of the month – with the weekday, the last such day.
            ScheduleKind.MonthlyWeekday when schedule.Week == BackupSchedule.Last => $"{Abbreviation(schedule.Weekday)} *-*~07/1 {time}",
            ScheduleKind.MonthlyWeekday => $"{Abbreviation(schedule.Weekday)} *-*-{(schedule.Week - 1) * 7 + 1:00}..{schedule.Week * 7:00} {time}",
            ScheduleKind.MonthlyDay when schedule.DayOfMonth == BackupSchedule.Last => $"*-*~01 {time}",
            ScheduleKind.MonthlyDay => $"*-*-{schedule.DayOfMonth:00} {time}",
            _ => $"*-*-* {time}",
        };
    }

    private static string Abbreviation(DayOfWeek day) => day.ToString()[..3];

    /// <summary>"BorgStudio: backup job "Docs"" → "BorgStudio backup job 'Docs'" (systemd's descriptions have no quoting).</summary>
    private static string UnitText(string description) =>
        description.Replace("BorgStudio: ", "BorgStudio ", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);
}
