using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #612: persists one least-squares RPM-vs-temperature fit per fan per calendar month to
/// %AppData%\TaskManagerPlus\fan-curve-history.json - lets EnergyThermalsViewModel draw last
/// month's fitted curve ghosted behind the current fan-curve scatter cloud. A curve shifted right
/// (more RPM needed for the same temperature) is dust or a clogged heatsink, not a fan/sensor
/// problem.
/// </summary>
public static class FanCurveHistoryService
{
    private const int MaxEntries = 500; // a handful of fans x many months, comfortably under this

    private static string SettingsPath => AppPaths.GetPath("fan-curve-history.json");

    public static List<FanCurveMonthlyFit> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<FanCurveMonthlyFit>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<FanCurveMonthlyFit>();
    }

    /// <summary>Replaces (or adds) this fan/month's fit and re-saves. Best-effort, same as every
    /// other settings write in this app.</summary>
    public static void UpsertFit(FanCurveMonthlyFit fit)
    {
        try
        {
            var list = Load();
            list.RemoveAll(f => f.FanIdentifier == fit.FanIdentifier && f.Year == fit.Year && f.Month == fit.Month);
            list.Add(fit);

            if (list.Count > MaxEntries)
                list = list.OrderBy(f => f.Year).ThenBy(f => f.Month).Skip(list.Count - MaxEntries).ToList();

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

    /// <summary>The most recent fit strictly before the given year/month for this fan - the
    /// "last month" (or most recent earlier month with data) overlay comparison point.</summary>
    public static FanCurveMonthlyFit? FindPriorFit(IEnumerable<FanCurveMonthlyFit> all, string fanIdentifier, int year, int month)
        => all.Where(f => f.FanIdentifier == fanIdentifier && (f.Year < year || (f.Year == year && f.Month < month)))
              .OrderByDescending(f => f.Year).ThenByDescending(f => f.Month)
              .FirstOrDefault();
}
