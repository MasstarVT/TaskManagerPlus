using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #614: persists the highest RPM ever observed per fan identifier, across every session, to
/// %AppData%\TaskManagerPlus\fan-max-rpm.json. Writes only happen when a fan actually sets a new
/// high-water mark (naturally rare after the first few sessions), so there's no need for the
/// batching/flush-interval dance CoolingBaselineService/FanCurveHistoryService use for their
/// much-more-frequent per-tick data.
/// </summary>
public static class FanMaxRpmService
{
    private static string SettingsPath => AppPaths.GetPath("fan-max-rpm.json");

    public static List<FanMaxRpmEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<FanMaxRpmEntry>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<FanMaxRpmEntry>();
    }

    /// <summary>Overwrites (or adds) this fan's historical-max entry. Best-effort, same as every
    /// other settings write in this app.</summary>
    public static void RecordMax(string identifier, double rpm)
    {
        try
        {
            var list = Load();
            list.RemoveAll(e => e.Identifier == identifier);
            list.Add(new FanMaxRpmEntry { Identifier = identifier, HistoricalMaxRpm = rpm, RecordedAt = DateTime.Now });

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
