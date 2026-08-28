using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #325: persists one SMART-attribute snapshot per disk to smart-history.json under
/// AppPaths.SettingsDirectory - same fail-silent-to-defaults shape as every other settings file in
/// this app (AlertThresholdsService, ThemeService, ...). A snapshot is recorded at most once per app
/// start per disk (RecordIfNew tracks which disk keys have already been recorded this session, so
/// repeated "Read SMART details" clicks on the same disk in one sitting don't pile up duplicate
/// entries), diffed against whatever the previous run's most recent entry for that disk was - "one
/// new reallocated sector" reads as a change, not lost in an ever-growing absolute count.
/// </summary>
public static class SmartHistoryService
{
    private static string SettingsPath => AppPaths.GetPath("smart-history.json");
    private static readonly HashSet<string> RecordedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public static List<SmartHistoryEntry> LoadAll()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var entries = JsonSerializer.Deserialize<List<SmartHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh, same as every other settings load in this app.
        }
        return new List<SmartHistoryEntry>();
    }

    /// <summary>#326: chronological snapshots for one disk, for the trend chart.</summary>
    public static List<SmartHistoryEntry> ForDisk(string diskKey) =>
        LoadAll().Where(e => e.DiskKey.Equals(diskKey, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(e => e.Timestamp).ToList();

    /// <summary>Records <paramref name="entry"/> once per app start per DiskKey, returning the diff
    /// against the previous entry for the same disk (#325's "changed since last run" panel) - an
    /// empty list either on the very first snapshot ever taken for this disk, or when this disk's
    /// snapshot has already been recorded this session (repeat clicks return no new diff, since
    /// nothing has changed since the one already recorded moments ago).</summary>
    public static List<SmartHistoryChange> RecordIfNew(SmartHistoryEntry entry)
    {
        lock (Lock)
        {
            if (!RecordedThisSession.Add(entry.DiskKey)) return new List<SmartHistoryChange>();

            var all = LoadAll();
            var previous = all.Where(e => e.DiskKey.Equals(entry.DiskKey, StringComparison.OrdinalIgnoreCase))
                               .OrderByDescending(e => e.Timestamp).FirstOrDefault();

            all.Add(entry);
            Save(all);

            var changes = new List<SmartHistoryChange>();
            if (previous is not null)
            {
                AddIfChanged(changes, "Reallocated sectors (05)", previous.Reallocated, entry.Reallocated);
                AddIfChanged(changes, "Current pending sectors (C5)", previous.PendingSector, entry.PendingSector);
                AddIfChanged(changes, "Offline uncorrectable (C6)", previous.OfflineUncorrectable, entry.OfflineUncorrectable);
                AddIfChanged(changes, "Reported uncorrectable (BB)", previous.ReportedUncorrectable, entry.ReportedUncorrectable);
                AddIfChanged(changes, "UDMA CRC errors (C7)", previous.UdmaCrcErrors, entry.UdmaCrcErrors);
                if (previous.NvmePercentageUsed.HasValue && entry.NvmePercentageUsed.HasValue)
                    AddIfChanged(changes, "NVMe percentage used", previous.NvmePercentageUsed.Value, entry.NvmePercentageUsed.Value);
                if (previous.NvmeAvailableSparePercent.HasValue && entry.NvmeAvailableSparePercent.HasValue)
                    AddIfChanged(changes, "NVMe available spare", previous.NvmeAvailableSparePercent.Value, entry.NvmeAvailableSparePercent.Value);
            }
            return changes;
        }
    }

    private static void AddIfChanged(List<SmartHistoryChange> changes, string label, int previous, int current)
    {
        if (previous != current) changes.Add(new SmartHistoryChange(label, previous, current));
    }

    private static void Save(List<SmartHistoryEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            // Keep the file from growing unbounded - 200 entries per disk is roughly a year of
            // once-a-day app-start snapshots, comfortably more than #326's trend chart needs.
            var trimmed = entries
                .GroupBy(e => e.DiskKey, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(e => e.Timestamp).Take(200))
                .ToList();
            var json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
