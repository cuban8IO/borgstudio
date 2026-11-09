using System.Diagnostics;
using System.Text.Json;

namespace BorgStudio.Core.Updates;

/// <summary>
/// Installs an update. Runs in the NEW version's process, started from the download folder with
/// <see cref="Argument"/> while the old app closes: waits for it to end, copies the new files over the old ones
/// (keeping a copy of every replaced file to put back on failure), notes the outcome and starts the app again.
/// Only files of the package are written: nothing else in the app's folder is touched or deleted.
/// </summary>
public static class UpdateApplier
{
    public const string Argument = "--apply-update";

    public const string OutcomeFile = "result.json";

    private const string LogFile = "update.log";

    public static bool IsApplyCall(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Argument;

    public static IEnumerable<string> Arguments(string source, InstallLocation target, int processId, string workDirectory, string version) =>
    [
        Argument,
        "--source", source,
        "--target", target.Directory,
        "--exe", target.RelativeExecutable,
        "--pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--work", workDirectory,
        "--version", version,
    ];

    /// <summary>Entry point of the installer process; always starts the app at the end (updated or not).</summary>
    /// <param name="start">Starts the app (tests replace it).</param>
    public static int Run(IReadOnlyList<string> args, Action<ProcessStartInfo>? start = null)
    {
        var options = new Dictionary<string, string>();
        for (var i = 1; i + 1 < args.Count; i += 2)
            options[args[i]] = args[i + 1];
        if (!options.TryGetValue("--source", out var source) || !options.TryGetValue("--target", out var target)
            || !options.TryGetValue("--exe", out var executable) || !options.TryGetValue("--work", out var work)
            || !options.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid))
            return 2;
        var version = options.GetValueOrDefault("--version", "?");
        var log = Path.Combine(work, LogFile);

        try
        {
            Directory.CreateDirectory(work);
            File.WriteAllText(log, $"{DateTime.Now:O} updating {target} to {version}{Environment.NewLine}");
            WaitForExit(pid, TimeSpan.FromMinutes(2));
            Replace(source, target, executable, Path.Combine(work, "backup"));
            WriteOutcome(work, new UpdateOutcome(version, true, null));
            Log(log, "done");
        }
        catch (Exception exception)
        {
            WriteOutcome(work, new UpdateOutcome(version, false, exception.Message));
            Log(log, exception.ToString());
        }

        try
        {
            var app = new ProcessStartInfo(Path.Combine(target, executable)) { UseShellExecute = false, WorkingDirectory = target };
            (start ?? (info => Process.Start(info)?.Dispose()))(app);
        }
        catch (Exception exception)
        {
            Log(log, exception.ToString());
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Copies every file of <paramref name="source"/> into <paramref name="target"/>. Files it replaces are kept in
    /// <paramref name="backup"/> first; if anything fails they are put back and files new to the folder are removed.
    /// </summary>
    public static void Replace(string source, string target, string executable, string backup, int attempts = 20)
    {
        // Never write into a folder that is not this app (a wrong path must not damage anything).
        if (!File.Exists(Path.Combine(target, executable)))
            throw new InvalidOperationException($"{target} does not contain {executable}.");
        if (!File.Exists(Path.Combine(source, executable)))
            throw new InvalidOperationException($"{source} does not contain {executable}.");

        if (Directory.Exists(backup))
            Directory.Delete(backup, recursive: true);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file)).ToList();

        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var destination = Path.Combine(target, file);
                if (File.Exists(destination))
                {
                    var kept = Path.Combine(backup, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    Retry(() => File.Copy(destination, kept, overwrite: true), attempts);
                    replaced.Add(file);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    added.Add(file);
                }
                Retry(() => File.Copy(Path.Combine(source, file), destination, overwrite: true), attempts);
            }
        }
        catch
        {
            foreach (var file in replaced)
                Ignore(() => File.Copy(Path.Combine(backup, file), Path.Combine(target, file), overwrite: true));
            foreach (var file in added)
                Ignore(() => File.Delete(Path.Combine(target, file)));
            throw;
        }

        Ignore(() => Directory.Delete(backup, recursive: true));
    }

    private static void WaitForExit(int processId, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return; // Already gone.
        }
        using (process)
        {
            if (!process.WaitForExit(timeout))
                throw new TimeoutException("BorgStudio did not close.");
        }
    }

    /// <summary>Files can stay busy for a moment after the old app ended (virus scanners, the OS).</summary>
    private static void Retry(Action action, int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Ignore(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteOutcome(string work, UpdateOutcome outcome) =>
        Ignore(() => File.WriteAllText(Path.Combine(work, OutcomeFile), JsonSerializer.Serialize(outcome)));

    private static void Log(string log, string text) =>
        Ignore(() => File.AppendAllText(log, $"{DateTime.Now:O} {text}{Environment.NewLine}"));
}
