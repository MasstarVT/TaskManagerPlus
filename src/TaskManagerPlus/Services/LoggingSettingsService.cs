using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves logging preferences to %AppData%\TaskManagerPlus\logging-settings.json
/// - same shape as ThemeService/AlertThresholdsService's persistence.</summary>
public static class LoggingSettingsService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("logging-settings.json");

    public static LoggingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<LoggingSettings>(json);
                if (settings is not null) return Sanitize(settings);
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return LoggingSettings.Defaults;
    }

    /// <summary>A hand-edited or damaged file can hold values the UI would never write: a
    /// non-positive interval throws from DispatcherTimer.Interval (or ticks continuously) while the
    /// main window is being built, and AutoCleanupDays &lt;= 0 deletes every rotated log part at
    /// launch. Out-of-range values fall back to the same choices the setters allow.</summary>
    private static LoggingSettings Sanitize(LoggingSettings settings)
    {
        if (settings.SampleIntervalSeconds is not (1 or 5 or 10)) settings.SampleIntervalSeconds = 1;
        if (settings.AutoCleanupDays < 1) settings.AutoCleanupDays = LoggingSettings.Defaults.AutoCleanupDays;
        // No UI sets this; the upper bound keeps the in-memory buffer (one row per sample) sane.
        settings.RollingBufferMinutes = Math.Clamp(settings.RollingBufferMinutes, 1, 1440);
        return settings;
    }

    public static void Save(LoggingSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the setting just won't survive a restart.
        }
    }
}
