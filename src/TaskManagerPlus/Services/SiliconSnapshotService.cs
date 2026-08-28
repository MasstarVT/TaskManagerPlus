using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #635: persists on-demand "silicon behavior" snapshots (CpuViewModel's "Snapshot current
/// behavior" button) to %AppData%\TaskManagerPlus\silicon-snapshots.json - same load/append/cap
/// shape as ThrottleHistoryService/PowerHistoryLogService. Fails silently to an empty list on a
/// missing/corrupt file, same as every other settings file in this app.
/// </summary>
public static class SiliconSnapshotService
{
    private const int MaxEntries = 200;

    private static string SettingsPath => AppPaths.GetPath("silicon-snapshots.json");

    public static List<SiliconSnapshot> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<SiliconSnapshot>>(json);
                if (list is not null) return list.OrderBy(s => s.Timestamp).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<SiliconSnapshot>();
    }

    /// <summary>Appends one snapshot and re-saves, capping the total count at
    /// <see cref="MaxEntries"/> (oldest dropped first). Best-effort - if this can't persist, the
    /// session still has the snapshot in CpuViewModel's in-memory SiliconSnapshots list.</summary>
    public static void Append(SiliconSnapshot snapshot)
    {
        try
        {
            var list = Load();
            list.Add(snapshot);
            list = list.OrderBy(s => s.Timestamp).ToList();
            if (list.Count > MaxEntries) list = list.Skip(list.Count - MaxEntries).ToList();

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the session still has it in memory.
        }
    }
}
