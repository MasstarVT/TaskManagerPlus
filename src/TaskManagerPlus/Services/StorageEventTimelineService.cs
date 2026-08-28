using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #370-#374: a unified, chronological storage event timeline across the providers that
/// matter for storage - `disk`, `Ntfs`, `volmgr`, `partmgr`, `storahci`, `stornvme`, `iaStorAC`,
/// `Microsoft-Windows-Kernel-PnP` (`volsnap` is covered indirectly - VSS's own shadow-copy storage
/// usage already has its own card via VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync;
/// volsnap doesn't log a distinct "storage went bad" event family the way the others here do, so
/// there's nothing further to add for it as an event source).
///
/// Rather than tripling the event-log-read boilerplate DiskDiagnosisEventService (#329/#336) and
/// NtfsCorruptionEventService (#344) already established, this reads only the providers/IDs those
/// two DON'T already cover (disk 11/15/51/157, volmgr, partmgr, storahci/stornvme/iaStorAC 129,
/// Kernel-PnP 219/225) and then folds their existing results straight into the same merged,
/// time-ordered list - one real consolidation rather than a third disconnected feed. The Storage
/// tab's own DiskDiagnosisEvents/NtfsCorruptionEvents cards keep working independently (their own
/// "Check now" buttons still populate them for anyone who only wants that narrower slice); this
/// service just re-reads the same cheap queries again for the unified view rather than depending on
/// those buttons having already been clicked.
///
/// Device-path resolution is intentionally two-tier: volume-level providers (Ntfs) resolve to a
/// drive letter via the shared DevicePathResolver (QueryDosDeviceW-based, extended from #344 per
/// #370's brief); physical-disk-level providers (the classic "Disk" source, storahci/stornvme/
/// iaStorAC, Kernel-PnP) reference a disk/port by number or raw NT device path instead, so those
/// fall back to a "Disk N (Model)" label (via Win32_DiskDrive.Index) or the raw "\Device\..." path
/// fragment when no disk index is embedded - never a fabricated volume letter.
/// </summary>
public static class StorageEventTimelineService
{
    private const int LookbackDays = 30;
    private const int MaxPerQuery = 50;
    private const int MaxTotal = 300;

    private const string DiskProvider = "Disk";

    private static readonly Regex HarddiskPhysicalRegex = new(@"\\Device\\Harddisk(\d+)\\DR\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GenericDevicePathRegex = new(@"\\Device\\[A-Za-z0-9_]+", RegexOptions.Compiled);

    public static List<StorageTimelineEvent> ReadUnifiedTimeline()
    {
        var diskModels = ReadDiskModelsByIndex();
        var events = new List<StorageTimelineEvent>();

        // "disk" provider events not already covered by DiskDiagnosisEventService (7/52/153).
        ReadByProviderEventIds(events, DiskProvider, new[] { 11, 15, 51, 157 }, diskModels);

        // storahci/stornvme/iaStorAC controller-reset (#373) - the storport miniport client
        // drivers all share the same "Reset to device, \Device\RaidPortN, was issued." message
        // shape under 129, just registered under each driver's own provider name.
        ReadByProviderEventIds(events, "storahci", new[] { 129 }, diskModels);
        ReadByProviderEventIds(events, "stornvme", new[] { 129 }, diskModels);
        ReadByProviderEventIds(events, "iaStorAC", new[] { 129 }, diskModels);

        // Kernel-PnP surprise-removal / driver-load-failure signals (#374).
        ReadByProviderEventIds(events, "Microsoft-Windows-Kernel-PnP", new[] { 219, 225 }, diskModels);

        // volmgr/partmgr - no fixed set of "the" interesting IDs the way the others above have, so
        // filtered to Warning/Error level only (Level 2/3) to avoid flooding the timeline with the
        // routine Information-level notices these two log constantly for ordinary mount/unmount
        // activity - the same Level-based noise filter EventLogService's own crash scan uses.
        ReadByProviderLevel(events, "volmgr", diskModels);
        ReadByProviderLevel(events, "partmgr", diskModels);

        // Fold in the two prior chunks' own reads rather than a third disconnected list - see this
        // class's remarks above.
        foreach (var e in DiskDiagnosisEventService.ReadBadBlockAndRetryEvents())
            events.Add(FromDiskDiagnosis(e, diskModels));
        foreach (var e in DiskDiagnosisEventService.ReadDiskDiagnosisEvents())
            events.Add(FromDiskDiagnosis(e, diskModels));
        foreach (var e in NtfsCorruptionEventService.ReadEvents())
            events.Add(FromNtfsCorruption(e));

        return events.OrderByDescending(e => e.TimeCreated).Take(MaxTotal).ToList();
    }

    private static StorageTimelineEvent FromDiskDiagnosis(DiskDiagnosisEvent e, Dictionary<int, string> diskModels) => new()
    {
        TimeCreated = e.TimeCreated,
        Provider = DiskProvider,
        EventId = e.EventId,
        DeviceText = ExtractDeviceHint(e.Message, diskModels),
        Category = e.EventId switch
        {
            52 => "Predicted failure",
            7 => "Bad block",
            153 => "I/O retried",
            _ => "Diagnosis",
        },
        Message = e.Message,
    };

    private static StorageTimelineEvent FromNtfsCorruption(NtfsCorruptionEvent e) => new()
    {
        TimeCreated = e.TimeCreated,
        Provider = "Ntfs",
        EventId = e.EventId,
        DeviceText = e.VolumeText,
        Category = e.EventId switch
        {
            55 => "Corruption detected",
            98 => "Transaction log unwritable",
            130 or 137 => "Volume resource exhaustion",
            140 or 142 => "Volume no longer writable",
            _ => "Ntfs event",
        },
        Message = e.Message,
    };

    private static void ReadByProviderEventIds(List<StorageTimelineEvent> into, string providerName, int[] eventIds, Dictionary<int, string> diskModels)
    {
        foreach (int id in eventIds)
        {
            try
            {
                long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
                var query = new EventLogQuery("System", PathType.LogName,
                    $"*[System[Provider[@Name='{providerName}'] and EventID={id} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
                { ReverseDirection = true };

                using var reader = new EventLogReader(query);
                int count = 0;
                while (count < MaxPerQuery && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        count++;
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                        into.Add(new StorageTimelineEvent
                        {
                            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                            Provider = providerName,
                            EventId = id,
                            DeviceText = ExtractDeviceHint(message, diskModels),
                            Category = CategoryFor(providerName, id),
                            Message = Truncate(message, 280),
                        });
                    }
                }
            }
            catch
            {
                // Provider/event unavailable on this system (e.g. no storahci-based controller, or
                // this ID has simply never fired) - contribute nothing for this ID.
            }
        }
    }

    private static void ReadByProviderLevel(List<StorageTimelineEvent> into, string providerName, Dictionary<int, string> diskModels)
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{providerName}'] and (Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxPerQuery && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    into.Add(new StorageTimelineEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Provider = providerName,
                        EventId = record.Id,
                        DeviceText = ExtractDeviceHint(message, diskModels),
                        Category = record.LevelDisplayName ?? "Event",
                        Message = Truncate(message, 280),
                    });
                }
            }
        }
        catch
        {
            // Provider unavailable on this system - contribute nothing.
        }
    }

    private static string CategoryFor(string providerName, int eventId) => (providerName, eventId) switch
    {
        (DiskProvider, 11) => "Controller error",
        (DiskProvider, 15) => "Device not ready",
        (DiskProvider, 51) => "Paging error",
        (DiskProvider, 157) => "Surprise removal",
        ("storahci", 129) or ("stornvme", 129) or ("iaStorAC", 129) => "Controller reset",
        ("Microsoft-Windows-Kernel-PnP", 219) => "Driver load failure",
        ("Microsoft-Windows-Kernel-PnP", 225) => "Device migration/removal",
        _ => "Event",
    };

    /// <summary>Best-effort device label from an event's formatted message: a physical-disk index
    /// (resolved to "Disk N (Model)" via Win32_DiskDrive when available), else the raw
    /// "\Device\..." path fragment if one appears in the message at all, else "Unknown device" -
    /// never a fabricated guess.</summary>
    private static string ExtractDeviceHint(string message, Dictionary<int, string> diskModels)
    {
        var physical = HarddiskPhysicalRegex.Match(message);
        if (physical.Success && int.TryParse(physical.Groups[1].Value, out int index))
        {
            return diskModels.TryGetValue(index, out var model) && model.Length > 0
                ? $"Disk {index} ({model})"
                : $"Disk {index}";
        }

        var generic = GenericDevicePathRegex.Match(message);
        return generic.Success ? generic.Value : "Unknown device";
    }

    private static Dictionary<int, string> ReadDiskModelsByIndex()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    int idx = Convert.ToInt32(mo["Index"]);
                    map[idx] = (mo["Model"] as string ?? string.Empty).Trim();
                }
                catch { /* skip this disk - one bad row shouldn't drop the rest */ }
            }
        }
        catch
        {
            // WMI unavailable - every physical-disk event below just shows "Disk N" without a
            // model suffix rather than failing the whole scan.
        }
        return map;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
