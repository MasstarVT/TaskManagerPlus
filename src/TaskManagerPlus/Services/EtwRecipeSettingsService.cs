using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#158: loads/saves EtwRecipeSettings to %AppData%\TaskManagerPlus\etw-recipes.json
/// (routed through AppPaths, so portable mode redirects it too) - same load/save shape as
/// PollIntervalSettingsService/EventFilterSettingsService. A missing/corrupt file, or one that
/// somehow deserializes to an empty recipe list, falls back to EtwRecipeSettings.Defaults' built-in
/// recipes (seeded from #150's own WPR scenario presets plus one netsh trace example) - the same
/// silent-fallback-to-defaults rule every settings file in this app follows.</summary>
public static class EtwRecipeSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("etw-recipes.json");

    public static EtwRecipeSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<EtwRecipeSettings>(json);
                if (settings is not null && settings.Recipes.Count > 0) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return EtwRecipeSettings.Defaults;
    }

    public static void Save(EtwRecipeSettings settings)
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
