using System.Globalization;
using System.Security;
using System.Text;
using BorgStudio.Core.Processes;

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
    /// pointing at <paramref name="executable"/>; entries of jobs without schedule (or deleted ones) go.
    /// </summary>
    /// <exception cref="SchedulerException">The system refused a change.</exception>
    Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, CancellationToken cancellationToken = default);
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
}

/// <summary>Windows Task Scheduler: a task per job in the folder \BorgStudio, registered with schtasks from XML.</summary>
public sealed class WindowsTaskScheduler(IProcessRunner runner, string workDirectory) : ISystemScheduler
{
    public const string Folder = @"\BorgStudio\";

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, CancellationToken cancellationToken = default)
    {
        var existing = await ListAsync(cancellationToken);
        foreach (var job in jobs.Where(job => job.IsScheduled))
        {
            Directory.CreateDirectory(workDirectory);
            var file = Path.Combine(workDirectory, $"{SystemScheduler.Id(job.Id)}.xml");
            // The format schtasks expects (UTF-16 with byte order mark).
            await File.WriteAllTextAsync(file, TaskXml(job, executable, DateTime.Now), Encoding.Unicode, cancellationToken);
            try
            {
                await SystemScheduler.RunAsync(runner, "schtasks", ["/Create", "/TN", TaskName(job.Id), "/XML", file, "/F"], cancellationToken);
            }
            finally
            {
                File.Delete(file);
            }
            existing.Remove(SystemScheduler.Id(job.Id));
        }
        foreach (var leftover in existing)
            await SystemScheduler.RunAsync(runner, "schtasks", ["/Delete", "/TN", Folder + leftover, "/F"], cancellationToken);
    }

    public static string TaskName(Guid jobId) => Folder + SystemScheduler.Id(jobId);

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
    public static string TaskXml(BackupJob job, string executable, DateTime now)
    {
        var xml = new StringBuilder();
        xml.AppendLine("""<?xml version="1.0" encoding="UTF-16"?>""");
        xml.AppendLine("""<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""");
        xml.AppendLine("  <RegistrationInfo>");
        xml.AppendLine($"    <Description>{Escape($"BorgStudio: backup job \"{job.Name}\"")}</Description>");
        xml.AppendLine($"    <URI>{Escape(TaskName(job.Id))}</URI>");
        xml.AppendLine("  </RegistrationInfo>");
        xml.AppendLine("  <Triggers>");
        foreach (var schedule in job.Schedules.Where(schedule => schedule.IsValid))
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
            if (schedule.Kind == ScheduleKind.Weekdays)
            {
                xml.AppendLine("      <ScheduleByWeek>");
                xml.AppendLine("        <DaysOfWeek>");
                foreach (var day in schedule.Days.Distinct().Order())
                    xml.AppendLine($"          <{day} />");
                xml.AppendLine("        </DaysOfWeek>");
                xml.AppendLine("        <WeeksInterval>1</WeeksInterval>");
                xml.AppendLine("      </ScheduleByWeek>");
            }
            else
            {
                xml.AppendLine("      <ScheduleByDay>");
                xml.AppendLine("        <DaysInterval>1</DaysInterval>");
                xml.AppendLine("      </ScheduleByDay>");
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
        xml.AppendLine($"    <StartWhenAvailable>{(job.CatchUp ? "true" : "false")}</StartWhenAvailable>");
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
        xml.AppendLine($"      <Arguments>--run-job {job.Id}</Arguments>");
        xml.AppendLine("    </Exec>");
        xml.AppendLine("  </Actions>");
        xml.AppendLine("</Task>");
        return xml.ToString();
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}

/// <summary>
/// launchd on macOS: a LaunchAgent per job with its calendar times. launchd makes up for times missed while the Mac slept;
/// for times missed while it was off, one more agent runs <c>--catch-up</c> at login.
/// </summary>
public sealed class LaunchdScheduler(IProcessRunner runner, string agentsDirectory) : ISystemScheduler
{
    public const string Prefix = "net.cuban8io.borgstudio.";

    public const string CatchUpLabel = Prefix + "catch-up";

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(agentsDirectory);
        var domain = $"gui/{(await SystemScheduler.RunAsync(runner, "id", ["-u"], cancellationToken)).StandardOutput.Trim()}";
        var wanted = new Dictionary<string, string>();
        foreach (var job in jobs.Where(job => job.IsScheduled))
            wanted[Prefix + SystemScheduler.Id(job.Id)] = JobPlist(job, executable);
        if (jobs.Any(job => job.IsScheduled && job.CatchUp))
            wanted[CatchUpLabel] = Plist(CatchUpLabel, [executable, "--catch-up"], runAtLoad: true, calendar: "");

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

    public static string JobPlist(BackupJob job, string executable)
    {
        var entries = new StringBuilder();
        foreach (var schedule in job.Schedules.Where(schedule => schedule.IsValid))
        {
            IEnumerable<DayOfWeek?> days = schedule.Kind == ScheduleKind.Weekdays ? schedule.Days.Distinct().Order().Cast<DayOfWeek?>() : [null];
            foreach (var day in days)
            {
                foreach (var time in schedule.TimesOfDay())
                {
                    entries.Append("      <dict>");
                    if (day is { } weekday)
                        entries.Append($"<key>Weekday</key><integer>{(int)weekday}</integer>");
                    entries.Append($"<key>Hour</key><integer>{time.Hour}</integer><key>Minute</key><integer>{time.Minute}</integer></dict>\n");
                }
            }
        }
        return Plist(Prefix + SystemScheduler.Id(job.Id), [executable, "--run-job", job.Id.ToString()], runAtLoad: false,
            calendar: $"    <key>StartCalendarInterval</key>\n    <array>\n{entries}    </array>\n");
    }

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
/// systemd on Linux: a user service and timer per job; <c>Persistent=true</c> makes up for times missed while the
/// computer was off.
/// </summary>
public sealed class SystemdScheduler(IProcessRunner runner, string unitDirectory) : ISystemScheduler
{
    public const string Prefix = "borgstudio-";

    public async Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(unitDirectory);
        var wanted = jobs.Where(job => job.IsScheduled).ToDictionary(job => Prefix + SystemScheduler.Id(job.Id));

        foreach (var timer in Directory.EnumerateFiles(unitDirectory, Prefix + "*.timer"))
        {
            var unit = Path.GetFileNameWithoutExtension(timer);
            if (wanted.ContainsKey(unit))
                continue;
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "disable", "--now", unit + ".timer"], cancellationToken, mayFail: true);
            File.Delete(timer);
            File.Delete(Path.Combine(unitDirectory, unit + ".service"));
        }
        foreach (var (unit, job) in wanted)
        {
            await File.WriteAllTextAsync(Path.Combine(unitDirectory, unit + ".service"), Service(job, executable), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(unitDirectory, unit + ".timer"), Timer(job), cancellationToken);
        }

        await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "daemon-reload"], cancellationToken);
        foreach (var unit in wanted.Keys)
        {
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "enable", unit + ".timer"], cancellationToken);
            // Picks up changed times of a timer that was running already.
            await SystemScheduler.RunAsync(runner, "systemctl", ["--user", "restart", unit + ".timer"], cancellationToken);
        }
    }

    public static string Service(BackupJob job, string executable) => $"""
        [Unit]
        Description=BorgStudio backup job "{job.Name.Replace("\"", "'", StringComparison.Ordinal)}"

        [Service]
        Type=oneshot
        ExecStart="{executable.Replace("\"", "\\\"", StringComparison.Ordinal)}" --run-job {job.Id}

        """.ReplaceLineEndings("\n");

    public static string Timer(BackupJob job)
    {
        var lines = new StringBuilder();
        foreach (var schedule in job.Schedules.Where(schedule => schedule.IsValid))
        {
            var days = schedule.Kind == ScheduleKind.Weekdays
                ? string.Join(',', schedule.Days.Distinct().Order().Select(day => day.ToString()[..3])) + " "
                : "";
            var time = schedule.Kind == ScheduleKind.EveryHours
                ? $"{schedule.Time.Hour:00}/{schedule.Hours}:{schedule.Time.Minute:00}:00"
                : $"{schedule.Time.Hour:00}:{schedule.Time.Minute:00}:00";
            lines.Append($"OnCalendar={days}*-*-* {time}\n");
        }
        return $"""
            [Unit]
            Description=Schedule of BorgStudio backup job "{job.Name.Replace("\"", "'", StringComparison.Ordinal)}"

            [Timer]
            {lines}Persistent={(job.CatchUp ? "true" : "false")}

            [Install]
            WantedBy=timers.target

            """.ReplaceLineEndings("\n");
    }
}
