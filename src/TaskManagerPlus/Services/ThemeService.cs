using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves the user's chosen colors to %AppData%\TaskManagerPlus\theme.json.</summary>
public static class ThemeService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("theme.json");

    public static ThemeColors Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var colors = JsonSerializer.Deserialize<ThemeColors>(json);
                if (colors is not null) return colors;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return ThemeColors.Defaults;
    }

    public static void Save(ThemeColors colors)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(colors, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
