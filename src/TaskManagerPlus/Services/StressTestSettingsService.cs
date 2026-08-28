using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #699: persists the Stress test panel's pass/fail and safety-abort criteria to
/// %AppData%\TaskManagerPlus\stress-test.json - the canonical small-settings-file pattern (see
/// PsuSettingsService/ThemeService's own remarks), fails silently to StressTestSettings' defaults
/// on a missing/corrupt file.
/// </summary>
public static class StressTestSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("stress-test.json");

    public static StressTestSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<StressTestSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return new StressTestSettings();
    }

    public static void Save(StressTestSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
