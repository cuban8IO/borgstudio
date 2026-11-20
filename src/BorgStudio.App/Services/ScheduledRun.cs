using BorgStudio.App.ViewModels;
using BorgStudio.Core;
using BorgStudio.Core.Backups;

namespace BorgStudio.App.Services;

/// <summary>
/// <c>BorgStudio --run-job &lt;job id&gt;</c>: runs one backup job without window, as the system's task scheduler starts
/// it. The outcome is recorded with the job (the app shows it) and a line goes to logs/scheduled.log.
/// Exit code: 0 backed up (also with warnings), 1 failed or stopped, 2 skipped, 3 unknown job or wrong call, 4 crashed.
/// <c>--check-repository &lt;repository id&gt;</c> checks a repository the same way (0 no damage, 1 damage or not run).
/// With <c>--if-due</c> (launchd's monthly times) a run only goes ahead on the right day.
/// </summary>
public static class ScheduledRun
{
    public const string Argument = ScheduledArguments.RunJob;

    /// <summary>Runs everything scheduled with "catch up" whose last scheduled time passed since it last ran.</summary>
    public const string CatchUpArgument = ScheduledArguments.CatchUp;

    public static bool IsRunCall(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is Argument or CatchUpArgument or SystemScheduler.DailyCheckArgument or ScheduledArguments.CheckRepository;

    public static int Run(IReadOnlyList<string> args)
    {
        if (args[0] == CatchUpArgument)
            return CatchUp();
        if (args[0] == SystemScheduler.DailyCheckArgument)
            return RunDailyCheck(force: true);
        var ifDue = args.Contains(ScheduledArguments.IfDue);
        if (args[0] == ScheduledArguments.CheckRepository)
            return args.Count >= 2 && Guid.TryParse(args[1], out var repositoryId) ? RunCheck(repositoryId, ifDue) : Usage();

        if (args.Count < 2 || !Guid.TryParse(args[1], out var jobId))
            return Usage();

        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            if (ifDue && services.Jobs.Load().FirstOrDefault(candidate => candidate.Id == jobId) is { } scheduled
                && !ScheduleCalendar.IsScheduledNow(scheduled.Schedules, DateTime.Now))
                return 0;
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

    private static int Usage()
    {
        Log($"Usage: {Argument} <job id> | {ScheduledArguments.CheckRepository} <repository id>");
        return 3;
    }

    /// <summary><c>--check-repository</c>: a normal check of the repository, recorded and reported like one by hand.</summary>
    private static int RunCheck(Guid repositoryId, bool ifDue)
    {
        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            if (services.RepositoryStore.Load().Repositories.FirstOrDefault(candidate => candidate.Id == repositoryId) is not { } repository)
            {
                Log($"There is no repository {repositoryId}.");
                return 3;
            }
            if (ifDue && !ScheduleCalendar.IsScheduledNow(repository.Check.Schedules, DateTime.Now))
                return 0;
            var record = new RepositoryChecks(services).RunScheduledAsync(repository, MainViewModel.CreateScheduledAccess(services))
                .GetAwaiter().GetResult();
            Log($"{repository.Name} (check): {record.Outcome}{(record.Error is null ? "" : $" – {record.Error}")}");
            return record.Outcome == Core.Checks.CheckOutcome.Ok ? 0 : 1;
        }
        catch (Exception exception)
        {
            Log($"{repositoryId} (check): {exception}");
            return 4;
        }
    }

    private static int CatchUp()
    {
        try
        {
            var services = App.CreateServices(new MainViewModel.NoDialogs());
            var now = DateTime.Now;
            var missed = services.Jobs.Load().Where(job => job.IsScheduled && ScheduleCalendar.IsMissed(job, now)).ToList();
            var checks = new RepositoryChecks(services).Store;
            var missedChecks = services.RepositoryStore.Load().Repositories
                .Where(repository => repository.Check.IsScheduled && ScheduleCalendar.IsMissed(repository.Check.Schedules,
                    repository.Check.CatchUp, checks.Get(repository.Id).FirstOrDefault()?.Started.LocalDateTime, now))
                .ToList();
            Log($"Catching up: {missed.Count} job(s), {missedChecks.Count} check(s)");
            var worst = 0;
            foreach (var job in missed)
                worst = Math.Max(worst, Run([Argument, job.Id.ToString()]));
            foreach (var repository in missedChecks)
                worst = Math.Max(worst, RunCheck(repository.Id, ifDue: false));
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
