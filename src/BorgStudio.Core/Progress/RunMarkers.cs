using System.Diagnostics;
using System.Text.Json;

namespace BorgStudio.Core.Progress;

public enum RunMarkerKind
{
    Backup,
    Check,
}

/// <summary>A run of another BorgStudio process (the system's task scheduler started it), as it left word of itself.</summary>
/// <param name="Waiting">Why it waits (a condition of its job, in words); <c>null</c> while it runs.</param>
public sealed record RunMarker(RunMarkerKind Kind, string Name, Guid? RepositoryId, Guid? JobId, DateTimeOffset Started, int ProcessId,
    string? Waiting = null);

/// <summary>
/// Lets the open app see what scheduled runs in other processes do: each run leaves a small file in <c>running/</c> while it
/// works (removed at its end; one of a process that is gone is ignored and cleaned up).
/// </summary>
public sealed class RunMarkers(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static RunMarkers ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "running"));

    public string Directory => directory;

    /// <summary>Leaves word of a run of this process; dispose the handle when it ends.</summary>
    public Handle Mark(RunMarkerKind kind, string name, Guid? repositoryId = null, Guid? jobId = null, string? waiting = null)
    {
        var marker = new RunMarker(kind, name, repositoryId, jobId, DateTimeOffset.Now, Environment.ProcessId, waiting);
        var file = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var handle = new Handle(file, marker);
        handle.Write();
        return handle;
    }

    /// <summary>The runs of other processes that are still alive.</summary>
    public IReadOnlyList<RunMarker> Others()
    {
        if (!System.IO.Directory.Exists(directory))
            return [];
        var markers = new List<RunMarker>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunMarker>(File.ReadAllText(file), JsonOptions) is not { } marker
                    || marker.ProcessId == Environment.ProcessId)
                    continue;
                if (IsAlive(marker))
                    markers.Add(marker);
                else
                    File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Being written or removed right now.
            }
        }
        return [.. markers.OrderBy(marker => marker.Started)];
    }

    /// <summary>The process still runs – and is the one that wrote the marker (process ids are reused).</summary>
    private static bool IsAlive(RunMarker marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            if (process.HasExited)
                return false;
            try
            {
                return process.StartTime <= marker.Started.LocalDateTime.AddSeconds(5);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true; // not allowed to look: trust it
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The marker of a run of this process.</summary>
    public sealed class Handle(string file, RunMarker marker) : IDisposable
    {
        private RunMarker _marker = marker;

        /// <summary>Waits now for <paramref name="reason"/> (<c>null</c>: runs).</summary>
        public void SetWaiting(string? reason)
        {
            _marker = _marker with { Waiting = reason };
            Write();
        }

        internal void Write()
        {
            try
            {
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(_marker, JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Only for the app's list.
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
