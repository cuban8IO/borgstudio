using System.Text.Json;

namespace BorgStudio.Core.Settings;

/// <summary>App-wide settings (never secrets).</summary>
public sealed record AppSettings
{
    public const int DefaultVaultAutoLockMinutes = 15;

    /// <summary>Lock the vault after this many minutes without using the app; 0 = never.</summary>
    public int VaultAutoLockMinutes { get; init; } = DefaultVaultAutoLockMinutes;

    /// <summary>UI language as a culture name ("en", "de"); null = the system's language if supported, else English.</summary>
    public string? Language { get; init; }

    /// <summary>Look for a new release at startup and once a day.</summary>
    public bool CheckForUpdates { get; init; } = true;
}

/// <summary>Keeps <see cref="AppSettings"/> in settings.json; a missing or unreadable file means defaults.</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SettingsStore CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "settings.json"));

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
