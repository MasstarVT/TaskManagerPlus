using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves MemoryHighWaterSettings (#429) to memory-high-water.json - same
/// load/save-fails-silently-to-defaults shape as PollIntervalSettingsService/LeakWatchSettingsService.</summary>
public static class MemoryHighWaterService
{
    private static string SettingsPath => AppPaths.GetPath("memory-high-water.json");

    public static MemoryHighWaterSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<MemoryHighWaterSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return MemoryHighWaterSettings.Defaults;
    }

    public static void Save(MemoryHighWaterSettings settings)
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
