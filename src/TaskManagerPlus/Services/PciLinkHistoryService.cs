using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #682: persists one PCIe link-speed/width reading per (device, boot) so PciLinkService can flag
/// a device whose link has *changed* since a previous boot, not just one that's below its own
/// reported maximum right now. Same load/append/cap-and-save JSON shape as GpuHangHistoryService -
/// fails silently to "no history" on a missing or corrupt file.
///
/// There's no simple public API for "give me a stable boot id", so this approximates one from
/// Environment.TickCount64 (uptime) subtracted from DateTime.UtcNow, rounded to the surrounding
/// couple of minutes - good enough to distinguish "this boot" from "a previous boot" without
/// needing exact precision (WMI's LastBootUpTime would be more precise but is one more query this
/// on-demand read doesn't need).
/// </summary>
public static class PciLinkHistoryService
{
    private const int MaxBootsPerDevice = 25;

    private static string SettingsPath => AppPaths.GetPath("pci-link-history.json");

    private sealed class Record
    {
        public string InstanceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public DateTime BootTimeUtc { get; set; }
        public int? Gen { get; set; }
        public int? Width { get; set; }
    }

    private static List<Record> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<Record>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<Record>();
    }

    private static void Save(List<Record> records)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, next boot just won't have this boot's reading to
            // compare against.
        }
    }

    private static string GenText(int? gen) => gen switch
    {
        1 => "Gen1", 2 => "Gen2", 3 => "Gen3", 4 => "Gen4", 5 => "Gen5", 6 => "Gen6",
        null => "Unknown",
        _ => $"Gen{gen}",
    };

    /// <summary>Compares each device's current reading against the most recent *prior* boot's
    /// reading for that same device instance, records this boot's reading (once per device per
    /// boot - repeated calls within the same session don't pile up duplicate rows), and returns a
    /// new list with ChangedSincePreviousBoot/PreviousBootLinkText filled in.</summary>
    public static List<PciLinkInfo> RecordAndCompare(List<PciLinkInfo> current)
    {
        var records = Load();
        var approxBootTimeUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        bool dirty = false;
        var result = new List<PciLinkInfo>(current.Count);

        foreach (var info in current)
        {
            var deviceRecords = records.Where(r => r.InstanceId == info.InstanceId)
                .OrderByDescending(r => r.BootTimeUtc).ToList();
            var sameBoot = deviceRecords.FirstOrDefault(r => Math.Abs((r.BootTimeUtc - approxBootTimeUtc).TotalMinutes) < 2);
            var previousBoot = deviceRecords.FirstOrDefault(r => Math.Abs((r.BootTimeUtc - approxBootTimeUtc).TotalMinutes) >= 2);

            bool? changedFlag = null;
            string? prevText = null;
            if (previousBoot is not null)
            {
                prevText = previousBoot.Gen is null && previousBoot.Width is null
                    ? null
                    : $"{GenText(previousBoot.Gen)} x{previousBoot.Width?.ToString() ?? "?"}";
                changedFlag = previousBoot.Gen != info.CurrentLinkGen || previousBoot.Width != info.CurrentLinkWidth;
            }

            result.Add(new PciLinkInfo
            {
                InstanceId = info.InstanceId,
                Name = info.Name,
                Kind = info.Kind,
                CurrentLinkGen = info.CurrentLinkGen,
                CurrentLinkWidth = info.CurrentLinkWidth,
                MaxLinkGen = info.MaxLinkGen,
                MaxLinkWidth = info.MaxLinkWidth,
                IsThunderboltAttached = info.IsThunderboltAttached,
                EnclosureName = info.EnclosureName,
                ChangedSincePreviousBoot = changedFlag,
                PreviousBootLinkText = prevText,
            });

            if (sameBoot is null)
            {
                records.Add(new Record
                {
                    InstanceId = info.InstanceId,
                    DeviceName = info.Name,
                    BootTimeUtc = approxBootTimeUtc,
                    Gen = info.CurrentLinkGen,
                    Width = info.CurrentLinkWidth,
                });
                dirty = true;
            }
        }

        if (dirty)
        {
            // Keep only the most recent MaxBootsPerDevice boots per device instance.
            var pruned = records
                .GroupBy(r => r.InstanceId)
                .SelectMany(g => g.OrderByDescending(r => r.BootTimeUtc).Take(MaxBootsPerDevice))
                .ToList();
            Save(pruned);
        }

        return result;
    }
}
