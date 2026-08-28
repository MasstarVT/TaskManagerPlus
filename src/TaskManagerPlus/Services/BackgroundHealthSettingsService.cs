using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#959/#960: load/save for background-health.json - fails silently to
/// BackgroundHealthSettings' defaults on a missing/corrupt file, same as every other settings
/// file in this app (ThemeService/theme.json, BaselineSettingsService, ...).</summary>
public static class BackgroundHealthSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string SettingsPath => AppPaths.GetPath("background-health.json");

    public static BackgroundHealthSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<BackgroundHealthSettings>(json, JsonOpts);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupt/unreadable - fall back to defaults */ }
        return new BackgroundHealthSettings();
    }

    public static void Save(BackgroundHealthSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { /* best-effort - a failed save just won't survive a restart */ }
    }
}
