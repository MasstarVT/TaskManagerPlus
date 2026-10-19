using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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

        // Kernel-PnP surprise-removal / driver-load-failure signals (#374) - these fire for every
        // device on the system, so only storage devices' records are kept.
        ReadKernelPnpStorageEvents(events, diskModels);

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
        // 55/130 come from the legacy "Ntfs" provider, 98/140 from "Microsoft-Windows-Ntfs" - see
        // NtfsCorruptionEventService's remarks.
        Provider = e.EventId is 98 or 140 ? "Microsoft-Windows-Ntfs" : "Ntfs",
        EventId = e.EventId,
        DeviceText = e.VolumeText,
        Category = e.EventId switch
        {
            55 => "Corruption detected",
            98 => "Volume needs repair",
            130 => "Repaired",
            140 => "Transaction log flush failed",
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

    private const string KernelPnpProvider = "Microsoft-Windows-Kernel-PnP";
    private const int MaxKernelPnpScanned = 1000;

    // Device instance IDs from these enumerators are storage devices outright; anything else
    // (PCI\ for an NVMe/AHCI controller, ...) is checked by its device class.
    private static readonly string[] StorageEnumeratorPrefixes = { @"SCSI\", @"IDE\", @"NVME\", @"STORAGE\", @"USBSTOR\", @"SD\", @"SBP2\", @"RAID\" };

    private static readonly HashSet<string> StorageClassGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e967-e325-11ce-bfc1-08002be10318}", // DiskDrive
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter (storport miniports: stornvme, storahci, RAID)
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC (IDE/ATA/AHCI controllers)
        "{71a27cdd-812a-11d0-bec7-08002be2092f}", // Volume
    };

    /// <summary>Kernel-PnP 219 (driver failed to load) and 225 (removal vetoed) are logged for
    /// every kind of device - on a typical PC almost all of them are WUDFRd/display/software
    /// devices. The device instance ID is a named field (219: "DriverName", which despite the name
    /// holds the instance ID; 225: "DeviceInstance"), so a record is kept only when that device is
    /// a storage one. Only kept records count toward MaxPerQuery, so non-storage noise can't
    /// crowd out real rows; MaxKernelPnpScanned bounds the walk.</summary>
    private static void ReadKernelPnpStorageEvents(List<StorageTimelineEvent> into, Dictionary<int, string> diskModels)
    {
        var selector = new EventLogPropertySelector(new[]
        {
            "Event/EventData/Data[@Name='DriverName']",
            "Event/EventData/Data[@Name='DeviceInstance']",
        });
        var classCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (int id in new[] { 219, 225 })
        {
            try
            {
                long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
                var query = new EventLogQuery("System", PathType.LogName,
                    $"*[System[Provider[@Name='{KernelPnpProvider}'] and EventID={id} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
                { ReverseDirection = true };

                using var reader = new EventLogReader(query);
                int kept = 0, scanned = 0;
                while (kept < MaxPerQuery && scanned < MaxKernelPnpScanned && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        scanned++;
                        string? instance = null;
                        try
                        {
                            if (record is EventLogRecord logRecord)
                            {
                                var values = logRecord.GetPropertyValues(selector);
                                instance = (id == 219 ? values[0] : values[1]) as string;
                            }
                        }
                        catch { /* field missing on this build - treated as unknown below */ }
                        instance = string.IsNullOrWhiteSpace(instance) ? null : instance.Trim();

                        // An unreadable instance is kept (with its message) rather than dropped unseen.
                        if (instance is not null && !IsStorageDevice(instance, classCache)) continue;
                        kept++;

                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; }

                        string device = ExtractDeviceHint(message, diskModels);
                        into.Add(new StorageTimelineEvent
                        {
                            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                            Provider = KernelPnpProvider,
                            EventId = id,
                            DeviceText = device == "Unknown device" && instance is not null ? instance : device,
                            Category = CategoryFor(KernelPnpProvider, id),
                            Message = Truncate(message, 280),
                        });
                    }
                }
            }
            catch
            {
                // Log/provider unavailable - contribute nothing for this ID, same as the other reads.
            }
        }
    }

    private static bool IsStorageDevice(string instanceId, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(instanceId, out bool known)) return known;
        bool isStorage = StorageEnumeratorPrefixes.Any(p => instanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!isStorage)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                isStorage = key?.GetValue("ClassGUID") is string classGuid && StorageClassGuids.Contains(classGuid.Trim());
            }
            catch
            {
                // Unreadable key (removed device, denied) - not provably storage.
            }
        }
        cache[instanceId] = isStorage;
        return isStorage;
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
        (KernelPnpProvider, 219) => "Driver load failure",
        (KernelPnpProvider, 225) => "Device migration/removal",
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
