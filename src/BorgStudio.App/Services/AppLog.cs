using System.Globalization;
using BorgStudio.Core;

namespace BorgStudio.App.Services;

/// <summary>Plain log files in the data folder's logs/ (each kept below about 1 MB: the older half moves to .old).</summary>
internal static class AppLog
{
    private const long Limit = 1024 * 1024;

    private static readonly object Gate = new();

    public static void Write(string fileName, string text, string? dataDirectory = null)
    {
        try
        {
            var folder = Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "logs");
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, fileName);
                if (File.Exists(file) && new FileInfo(file).Length > Limit)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file,
                    $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {text}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop a backup.
        }
    }
}
