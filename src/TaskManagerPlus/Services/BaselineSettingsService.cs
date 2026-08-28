using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#952: loads/saves the automatic weekly baseline capture toggle to
/// baseline-settings.json - same shape/fail-silently convention as AlertThresholdsService.</summary>
public static class BaselineSettingsService
{
    // Routed through AppPaths (#87) so portable mode redirects this next to the exe, same as
    // every other settings file in this app.
    private static string SettingsPath => AppPaths.GetPath("baseline-settings.json");

    public static BaselineSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<BaselineSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return BaselineSettings.Defaults;
    }

    public static void Save(BaselineSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
