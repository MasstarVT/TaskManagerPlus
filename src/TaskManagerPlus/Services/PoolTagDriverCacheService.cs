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

    public static PoolTagDriverCache Load()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                var cache = JsonSerializer.Deserialize<PoolTagDriverCache>(json);
                if (cache is not null) return cache;
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
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the scan result still shows for this session.
        }
    }
}
