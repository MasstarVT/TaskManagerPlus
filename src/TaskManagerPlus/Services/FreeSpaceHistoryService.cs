using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #352: persists one daily free-space low-water mark per fixed volume to
/// free-space-history.json under AppPaths.SettingsDirectory - same "small JSON, fail silently to
/// defaults" shape every other settings file in this app uses (see AlertThresholdsService). Backed
/// by an in-memory cache (loaded lazily, written back only when a sample actually changes
/// something) rather than a fresh disk read/write on every StorageViewModel.OnPerformanceSampled
/// tick, since this is now called unthrottled on every shared-sampler tick per #352's brief.
/// </summary>
public static class FreeSpaceHistoryService
{
    private static string SettingsPath => AppPaths.GetPath("free-space-history.json");

    // A history this long (about 6 months of daily points) is plenty for a linear run-out
    // projection while keeping the JSON file small - older points are dropped, oldest first.
    private const int MaxDaysPerDrive = 180;

    private static readonly object Lock = new();
    private static FreeSpaceHistoryStore? _cache;

    private static FreeSpaceHistoryStore LoadCache()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var store = JsonSerializer.Deserialize<FreeSpaceHistoryStore>(json);
                if (store is not null)
                {
                    _cache = store;
                    return _cache;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file - fall back to an empty store below.
        }
        _cache = new FreeSpaceHistoryStore();
        return _cache;
    }

    private static void Persist(FreeSpaceHistoryStore store)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, this session's history just resets next launch.
        }
    }

    /// <summary>Records <paramref name="freeBytes"/> as today's low-water mark for
    /// <paramref name="driveLetter"/> (e.g. "C:") if it's lower than what's already recorded for
    /// today, or starts a new day's entry. Returns a snapshot of the drive's full recorded history
    /// (oldest first) either way, so callers don't need a separate read-back call.</summary>
    public static List<FreeSpaceDailyPoint> RecordSample(string driveLetter, long freeBytes, long totalBytes)
    {
        lock (Lock)
        {
            var store = LoadCache();
            if (!store.ByDrive.TryGetValue(driveLetter, out var list))
            {
                list = new List<FreeSpaceDailyPoint>();
                store.ByDrive[driveLetter] = list;
            }

            var today = DateTime.Today;
            bool changed = false;
            if (list.Count > 0 && list[^1].Date == today)
            {
                if (freeBytes < list[^1].FreeBytes)
                {
                    list[^1].FreeBytes = freeBytes;
                    list[^1].TotalBytes = totalBytes;
                    changed = true;
                }
            }
            else
            {
                list.Add(new FreeSpaceDailyPoint { Date = today, FreeBytes = freeBytes, TotalBytes = totalBytes });
                if (list.Count > MaxDaysPerDrive) list.RemoveAt(0);
                changed = true;
            }

            if (changed) Persist(store);
            return new List<FreeSpaceDailyPoint>(list);
        }
    }
}
