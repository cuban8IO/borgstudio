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

    /// <summary>The "no warranty" notice was shown (once, at the first start; after an update from an older version too).</summary>
    public bool WarrantyNoticeShown { get; init; }

    /// <summary>Who starts scheduled backups: the system's task scheduler, or BorgStudio itself in the background.</summary>
    public ScheduleRunner ScheduleRunner { get; init; } = ScheduleRunner.System;

    /// <summary>In the background: the schedules start nothing until switched on again.</summary>
    public bool SchedulesPaused { get; init; }

    /// <summary>The hint "BorgStudio keeps running in the notification area" was shown (once).</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Who gets told about backups, and how.</summary>
    public Notifications.NotificationSettings Notifications { get; init; } = new();
}

/// <summary>Who starts scheduled backups.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ScheduleRunner>))]
public enum ScheduleRunner
{
    /// <summary>Windows Task Scheduler, launchd, systemd: runs even when BorgStudio is closed.</summary>
    System,

    /// <summary>BorgStudio itself, running in the notification area and started at login.</summary>
    Background,
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
