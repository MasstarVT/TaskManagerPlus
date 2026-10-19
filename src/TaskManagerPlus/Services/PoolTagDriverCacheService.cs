using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#417: loads/saves the cached tag-to-driver attribution to pool-tag-drivers.json -
/// same load/save-fails-silently-to-defaults shape as LeakWatchSettingsService. Cached because
/// PoolTagInspectionService.ScanForDriverAttribution walks hundreds of driver files on disk and
/// re-running it on every "Scan pool tags" click would make the button unusably slow.</summary>
public static class PoolTagDriverCacheService
{
    private static string CachePath => AppPaths.GetPath("pool-tag-drivers.json");

    /// <summary>Files written before this version could hold "Unknown" entries from a scan the
    /// deadline/file cap cut short; those were never checked against every driver file, so they're
    /// dropped on load and rescanned instead of staying unattributed forever.</summary>
    private const int CurrentSchemaVersion = 2;
    private const string UnknownDriver = "Unknown";

    /// <summary>On-disk shape: <see cref="PoolTagDriverCache"/> plus a schema marker (absent, so 0,
    /// in older files).</summary>
    private sealed class StoredCache
    {
        public int SchemaVersion { get; set; }
        public DateTime LastScanUtc { get; set; }
        public Dictionary<string, string> TagToDriver { get; set; } = new(StringComparer.Ordinal);
    }

    public static PoolTagDriverCache Load()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                var stored = JsonSerializer.Deserialize<StoredCache>(json);
                if (stored is not null)
                {
                    // A hand-edited "tag": null would count as already resolved yet show no driver -
                    // dropped so the next scan resolves it again.
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (tag, driver) in stored.TagToDriver ?? new())
                    {
                        if (driver is not null) map[tag] = driver;
                    }
                    if (stored.SchemaVersion < CurrentSchemaVersion)
                    {
                        foreach (var tag in map.Where(kv => kv.Value == UnknownDriver).Select(kv => kv.Key).ToList())
                            map.Remove(tag);
                    }
                    return new PoolTagDriverCache { LastScanUtc = stored.LastScanUtc, TagToDriver = map };
                }
            }
        }
        catch
        {
            // Corrupt or unreadable cache file - fall back to an empty one, same as a first run.
        }
        return new PoolTagDriverCache();
    }

    public static void Save(PoolTagDriverCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(dir);
            var stored = new StoredCache
            {
                SchemaVersion = CurrentSchemaVersion,
                LastScanUtc = cache.LastScanUtc,
                TagToDriver = cache.TagToDriver,
            };
            var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the scan result still shows for this session.
        }
    }
}
