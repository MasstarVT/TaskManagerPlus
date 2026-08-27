using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves the Summary tab's dashboard tile layout (#69) to
/// %AppData%\TaskManagerPlus\dashboard-layout.json - same shape as ThemeService. An empty/missing
/// file degrades to "no saved config for any tile", which SummaryViewModel.BuildTiles treats as
/// "visible, in this app's built-in default order" for every known tile - the same
/// missing-field-degrades-gracefully discipline ThemeService/LoggingSettingsService already use.</summary>
public static class DashboardLayoutService
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskManagerPlus",
        "dashboard-layout.json");

    public static List<DashboardTileConfig> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var tiles = JsonSerializer.Deserialize<List<DashboardTileConfig>>(json);
                if (tiles is not null) return tiles;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to the built-in default layout.
        }
        return new List<DashboardTileConfig>();
    }

    public static void Save(List<DashboardTileConfig> tiles)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(tiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
