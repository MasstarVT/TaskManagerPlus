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
///
/// #1082: the write-back is debounced, not per-change - during any sustained download/build,
/// "today's low-water mark dropped" is true on nearly every 1s tick, and each one used to
/// re-serialize and rewrite the whole 180-day store (originally inside StorageViewModel's
/// Dispatcher.Invoke, i.e. on the UI thread). Changes now just mark the cache dirty; the actual
/// file write happens at most once per PersistIntervalMs, immediately on a day rollover (so a
/// completed day's mark isn't left in memory for long), and on <see cref="Flush"/> at app exit -
/// and RecordSample's only caller now runs on a background thread, so the debounced write is off
/// the dispatcher too.
/// </summary>
public static class FreeSpaceHistoryService
{
    private static string SettingsPath => AppPaths.GetPath("free-space-history.json");

    // A history this long (about 6 months of daily points) is plenty for a linear run-out
    // projection while keeping the JSON file small - older points are dropped, oldest first.
    private const int MaxDaysPerDrive = 180;

    // #1082: minimum spacing between persisted writes of the store (except day rollover/Flush).
    private const long PersistIntervalMs = 60_000;

    private static readonly object Lock = new();
    private static FreeSpaceHistoryStore? _cache;
    private static bool _dirty;
    private static long _lastPersistMs = long.MinValue;

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

    /// <summary>Writes the store now and, on success, clears the dirty flag - on failure the
    /// cache stays dirty so the next debounce window (or Flush) retries. Call under Lock.</summary>
    private static void PersistLocked(FreeSpaceHistoryStore store)
    {
        _lastPersistMs = Environment.TickCount64;
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            _dirty = false;
        }
        catch
        {
            // Best-effort - if we can't persist, this session's history just resets next launch.
        }
    }

    /// <summary>#1082: persists any unwritten changes - called once from MainViewModel.Dispose so
    /// the last debounce window's low-water marks survive a clean exit.</summary>
    public static void Flush()
    {
        lock (Lock)
        {
            if (_dirty && _cache is not null) PersistLocked(_cache);
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
            bool newDay = false;
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
                newDay = true;
            }

            // #1082: debounced write-back - a change only marks the cache dirty; the file is
            // rewritten at most once per PersistIntervalMs (a low-water mark that keeps dropping
            // during a download used to rewrite the whole 180-day store nearly every 1s tick), or
            // immediately when a new day's entry starts. Environment.TickCount64 is monotonic, so
            // a clock step can't stall or double the writes.
            if (changed) _dirty = true;
            if (_dirty && (newDay || _lastPersistMs == long.MinValue || Environment.TickCount64 - _lastPersistMs >= PersistIntervalMs))
                PersistLocked(store);

            return new List<FreeSpaceDailyPoint>(list);
        }
    }
}
