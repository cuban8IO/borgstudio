namespace BorgStudio.App.Services;

/// <summary>The app icon as a file next to the data, for system notifications (Windows and Linux take a path).</summary>
internal static class NotificationIcon
{
    /// <summary>Writes the icon to &lt;data&gt;/notification-icon.png if it is missing or changed; <c>null</c> if that fails.</summary>
    public static string? Ensure(string dataDirectory)
    {
        try
        {
            using var stream = typeof(NotificationIcon).Assembly.GetManifestResourceStream("BorgStudio.NotificationIcon.png");
            if (stream is null)
                return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var path = Path.Combine(dataDirectory, "notification-icon.png");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllBytes(path, bytes);
            }
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
