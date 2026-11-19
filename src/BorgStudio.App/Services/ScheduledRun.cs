using BorgStudio.App.ViewModels;
using BorgStudio.Core;
using BorgStudio.Core.Backups;

namespace BorgStudio.App.Services;

/// <summary>
/// <c>BorgStudio --run-job &lt;job id&gt;</c>: runs one backup job without window, as the system's task scheduler starts
/// it. The outcome is recorded with the job (the app shows it) and a line goes to logs/scheduled.log.
/// Exit code: 0 backed up (also with warnings), 1 failed or stopped, 2 skipped, 3 unknown job or wrong call, 4 crashed.
/// </summary>
public static class ScheduledRun
{
    public const string Argument = "--run-job";

    /// <summary>Runs every scheduled job with "catch up" whose last scheduled time passed since its last run.</summary>
    public const string CatchUpArgument = "--catch-up";

    public static bool IsRunCall(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is Argument or CatchUpArgument or SystemScheduler.DailyCheckArgument;

    public static int Run(IReadOnlyList<string> args)
    {
        if (args[0] == CatchUpArgument)
            return CatchUp();
        if (args[0] == SystemScheduler.DailyCheckArgument)
            return RunDailyCheck(force: true);

        if (args.Count < 2 || !Guid.TryParse(args[1], out var jobId))
        {
            Log($"Usage: {Argument} <job id>");
            return 3;
        }

        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            var runner = new ScheduledJobRunner(services, MainViewModel.CreateScheduledAccess(services));
            var result = runner.RunAsync(jobId).GetAwaiter().GetResult();
            var job = services.Jobs.Load().FirstOrDefault(candidate => candidate.Id == jobId);
            var summary = result.Summary;
            var reason = summary.SkipReason is { } skip ? BorgTexts.SkipReason(skip) : summary.Message;
            Log($"{job?.Name ?? jobId.ToString()}: {summary.Outcome}{(reason is null ? "" : $" – {reason}")}");
            return summary.Outcome switch
            {
                BackupOutcome.Succeeded or BackupOutcome.Warnings => 0,
                BackupOutcome.Skipped => 2,
                _ => 1,
            };
        }
        catch (ArgumentException exception)
        {
            Log(exception.Message);
            return 3;
        }
        catch (Exception exception)
        {
            Log($"{jobId}: {exception}");
            return 4;
        }
    }

    private static int CatchUp()
    {
        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            var missed = services.Jobs.Load().Where(job => job.IsScheduled && ScheduleCalendar.IsMissed(job, DateTime.Now)).ToList();
            Log($"Catching up: {missed.Count} job(s)");
            var worst = 0;
            foreach (var job in missed)
                worst = Math.Max(worst, Run([Argument, job.Id.ToString()]));
            // A daily check missed while the computer was off (launchd does not make up for it by itself).
            return Math.Max(worst, RunDailyCheck(force: false));
        }
        catch (Exception exception)
        {
            Log($"Catching up: {exception}");
            return 4;
        }
    }

    /// <summary>
    /// <c>--daily-check</c>: overdue jobs and the summary of the day (<paramref name="force"/>: now; otherwise only if missed).
    /// The vault is opened with the key for scheduled runs, for the channels' secrets.
    /// </summary>
    private static int RunDailyCheck(bool force)
    {
        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            var access = MainViewModel.CreateScheduledAccess(services);
            try
            {
                access.TryUnlock();
            }
            catch (Core.Security.SecretBoxException)
            {
                // The secrets stay unreadable; channels without secrets still work.
            }
            try
            {
                var check = new DailyCheck(services);
                if (force)
                    check.RunAsync(DateTime.Now, services.Vault).GetAwaiter().GetResult();
                else if (!check.RunIfDueAsync(DateTime.Now, services.Vault).GetAwaiter().GetResult())
                    return 0;
                Log("Daily check done");
                return 0;
            }
            finally
            {
                services.Vault.Lock();
            }
        }
        catch (Exception exception)
        {
            Log($"Daily check: {exception}");
            return 4;
        }
    }

    /// <summary>One line per run in logs/scheduled.log.</summary>
    internal static void Log(string text, string? dataDirectory = null) => AppLog.Write("scheduled.log", text, dataDirectory);
}
