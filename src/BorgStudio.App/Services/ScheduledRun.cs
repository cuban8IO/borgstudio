using System.Globalization;
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

    private const long LogLimit = 1024 * 1024;

    public static bool IsRunCall(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Argument;

    public static int Run(IReadOnlyList<string> args)
    {
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

    /// <summary>One line per run in logs/scheduled.log (kept below about 1 MB: the older half moves to .old).</summary>
    private static void Log(string text)
    {
        try
        {
            var folder = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "scheduled.log");
            if (File.Exists(file) && new FileInfo(file).Length > LogLimit)
                File.Move(file, file + ".old", overwrite: true);
            File.AppendAllText(file, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {text}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop a backup.
        }
    }
}
