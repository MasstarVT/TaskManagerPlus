using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#296: loads/saves the flight recorder's armed/disarmed preference to
/// flight-recorder-settings.json under AppPaths.SettingsDirectory - same load/save-with-defaults-
/// on-failure shape as LoggingSettingsService/every other settings file in this app.</summary>
public static class FlightRecorderSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("flight-recorder-settings.json");

    public static FlightRecorderSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<FlightRecorderSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return FlightRecorderSettings.Defaults;
    }

    public static void Save(FlightRecorderSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the setting just won't survive a restart.
        }
    }
}
