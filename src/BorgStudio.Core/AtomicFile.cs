namespace BorgStudio.Core;

/// <summary>Replaces a file in one step (write a temporary file, then move it over the old one).</summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>. The move is tried a few times: on Windows a virus
    /// scanner or another BorgStudio process reading the file can hold it for a moment.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, contents);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
