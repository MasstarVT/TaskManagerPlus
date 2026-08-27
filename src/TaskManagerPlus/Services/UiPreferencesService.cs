using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves UiPreferences to %AppData%\TaskManagerPlus\ui-preferences.json - same
/// shape as ThemeService/AlertThresholdsService.</summary>
public static class UiPreferencesService
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskManagerPlus",
        "ui-preferences.json");

    public static UiPreferences Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var prefs = JsonSerializer.Deserialize<UiPreferences>(json);
                if (prefs is not null) return prefs;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return UiPreferences.Defaults;
    }

    public static void Save(UiPreferences prefs)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
