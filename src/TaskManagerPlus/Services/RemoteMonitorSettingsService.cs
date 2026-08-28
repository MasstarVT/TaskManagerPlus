using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads/saves the remote-monitoring toggle to %AppData%\TaskManagerPlus\remote-monitor.json
/// - same shape as ThemeService/AlertThresholdsService's persistence.</summary>
public static class RemoteMonitorSettingsService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("remote-monitor.json");

    public static RemoteMonitorSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<RemoteMonitorSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults (off).
        }
        return RemoteMonitorSettings.Defaults;
    }

    public static void Save(RemoteMonitorSettings settings)
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
