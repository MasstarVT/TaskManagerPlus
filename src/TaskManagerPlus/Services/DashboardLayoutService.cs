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
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("dashboard-layout.json");

    public static List<DashboardTileConfig> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var tiles = JsonSerializer.Deserialize<List<DashboardTileConfig>>(json);
                // A hand-edited file can carry null elements, "Id": null or duplicate Ids -
                // SummaryViewModel.BuildTiles keys a dictionary on Id inside its constructor.
                if (tiles is not null)
                {
                    var clean = tiles.Where(t => t is not null && !string.IsNullOrEmpty(t.Id))
                        .GroupBy(t => t.Id).Select(g => g.First()).ToList();
                    if (clean.Count != tiles.Count) AppPaths.MarkUnreadable(SettingsPath);
                    return clean;
                }
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to the built-in default layout, and
            // keep a copy of the user's file before the next Save replaces it.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return new List<DashboardTileConfig>();
    }

    public static void Save(List<DashboardTileConfig> tiles)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
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
