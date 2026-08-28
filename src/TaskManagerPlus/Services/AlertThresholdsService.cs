using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves the user's alert thresholds to %AppData%\TaskManagerPlus\alerts.json -
/// same shape as ThemeService.</summary>
public static class AlertThresholdsService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("alerts.json");

    public static AlertThresholds Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var thresholds = JsonSerializer.Deserialize<AlertThresholds>(json);
                if (thresholds is not null) return thresholds;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return AlertThresholds.Defaults;
    }

    public static void Save(AlertThresholds thresholds)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(thresholds, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
