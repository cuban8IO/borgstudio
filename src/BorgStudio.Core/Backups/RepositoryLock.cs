namespace BorgStudio.Core.Backups;

/// <summary>
/// Keeps two BorgStudio runs (the open app, a scheduled run in another process) from working on the same repository at
/// once: an exclusive lock on a file per repository, released when the process ends – also when it crashes. borg has a
/// lock of its own; this one turns "the repository is locked" into a clear "another run is busy" before borg starts.
/// </summary>
public sealed class RepositoryLock : IDisposable
{
    private readonly FileStream _file;

    private RepositoryLock(FileStream file) => _file = file;

    /// <summary>The lock, or <c>null</c> while another run holds it.</summary>
    public static RepositoryLock? TryAcquire(string directory, Guid repositoryId)
    {
        Directory.CreateDirectory(directory);
        try
        {
            var file = new FileStream(Path.Combine(directory, $"{repositoryId:N}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, FileOptions.None);
            return new RepositoryLock(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
