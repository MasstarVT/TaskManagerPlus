using System.Management;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Storage Spaces / RAID member health rollup (#85), via MSFT_VirtualDisk in the same
/// root\Microsoft\Windows\Storage namespace SystemSpecsService already queries for SSD wear and
/// page file location. Storage Spaces is an opt-in Windows feature most desktops/laptops never
/// configure at all, so an empty result here is the expected, common case, not a failure - the
/// Storage tab collapses the whole card when this returns nothing, the same "hidden when not
/// applicable" pattern the Battery/outdated-driver sections already use elsewhere in this app.
///
/// Round 20, #386/#387/#388 extend the same one-time read with per-pool member physical disks
/// (MSFT_StoragePoolToPhysicalDisk), in-flight repair/rebuild jobs
/// (MSFT_StorageJobToAffectedStorageObject), and a thin-provisioning over-commit warning
/// (MSFT_StoragePool.AllocatedSize/Size/ThinProvisioningAlertThresholds vs. the sum of every thin
/// virtual disk's logical Size in the pool) - all read once alongside the existing per-virtual-disk
/// facts, following the same WMI-associator-chain and enum-decoding style as the rest of this file.
/// </summary>
public static class StorageSpacesService
{
    public static List<StorageSpaceInfo> List()
    {
        var result = new List<StorageSpaceInfo>();
        try
        {
            // Materialized into a plain list (not just enumerated once) since #388's thin-
            // provisioning total needs to see every virtual disk sharing a pool before any single
            // row's text can be built - a second pass over the same in-memory list, not a second
            // WMI query.
            var vdisks = new List<ManagementObject>();
            using (var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, ObjectId, HealthStatus, OperationalStatus, ResiliencySettingName, Size, AllocatedSize, ProvisioningType FROM MSFT_VirtualDisk"))
            {
                foreach (ManagementObject vdisk in searcher.Get()) vdisks.Add(vdisk);
            }

            // #388: sum of every thin virtual disk's logical Size, grouped by owning pool -
            // ProvisioningType 1 = Thin per MSFT_VirtualDisk's documented enum (Storage Management
            // API; Fixed = 2).
            var thinTotalByPool = new Dictionary<string, long>();
            var poolObjectIdByVdisk = new Dictionary<string, string>();
            foreach (var vdisk in vdisks)
            {
                string vdiskObjectId = (vdisk["ObjectId"] as string) ?? string.Empty;
                string? poolObjectId = ReadOwningPoolObjectId(vdisk);
                if (poolObjectId is null || poolObjectId.Length == 0) continue;
                poolObjectIdByVdisk[vdiskObjectId] = poolObjectId;

                int provisioningType = 0;
                try { provisioningType = Convert.ToInt32(vdisk["ProvisioningType"] ?? 0); } catch { /* leave 0 */ }
                if (provisioningType == 1) // Thin
                {
                    long thinSize = 0;
                    try { thinSize = Convert.ToInt64(vdisk["Size"] ?? 0L); } catch { /* leave 0 */ }
                    thinTotalByPool.TryGetValue(poolObjectId, out long running);
                    thinTotalByPool[poolObjectId] = running + thinSize;
                }
            }

            // Per-pool facts are cached so a pool with several virtual disks only pays for one
            // MSFT_StoragePoolToPhysicalDisk / MSFT_StoragePool round trip, not one per row.
            var memberCache = new Dictionary<string, List<PoolMemberDiskInfo>>();
            var poolSizeCache = new Dictionary<string, (long Size, long AllocatedSize, ushort[] Thresholds)>();

            foreach (var vdisk in vdisks)
            {
                string vdiskName = (vdisk["FriendlyName"] as string ?? "Virtual disk").Trim();
                string vdiskObjectId = (vdisk["ObjectId"] as string) ?? string.Empty;

                int health = 0;
                try { health = Convert.ToInt32(vdisk["HealthStatus"] ?? 0); } catch { /* leave 0 (Healthy) */ }

                long size = 0, allocatedSize = 0;
                try { size = Convert.ToInt64(vdisk["Size"] ?? 0L); } catch { /* leave 0 */ }
                try { allocatedSize = Convert.ToInt64(vdisk["AllocatedSize"] ?? 0L); } catch { /* leave 0 */ }

                int provisioningType = 0;
                try { provisioningType = Convert.ToInt32(vdisk["ProvisioningType"] ?? 0); } catch { /* leave 0 */ }

                var opStatusCodes = (vdisk["OperationalStatus"] as ushort[]) ?? Array.Empty<ushort>();
                bool canRepair = opStatusCodes.Contains((ushort)3) || opStatusCodes.Contains((ushort)11); // Degraded / In Service

                poolObjectIdByVdisk.TryGetValue(vdiskObjectId, out var poolObjectId);
                poolObjectId ??= string.Empty;
                string poolName = ReadOwningPoolName(vdisk) ?? "Storage pool";

                // #386
                var members = new List<PoolMemberDiskInfo>();
                if (poolObjectId.Length > 0)
                {
                    if (!memberCache.TryGetValue(poolObjectId, out var cached))
                    {
                        cached = ReadPoolMembers(poolObjectId);
                        memberCache[poolObjectId] = cached;
                    }
                    members = cached;
                }

                // #387
                var jobs = ReadActiveJobs(vdiskObjectId);

                // #388
                string thinWarning = string.Empty;
                if (poolObjectId.Length > 0 && thinTotalByPool.TryGetValue(poolObjectId, out long thinTotal))
                {
                    if (!poolSizeCache.TryGetValue(poolObjectId, out var poolFacts))
                    {
                        poolFacts = ReadPoolSizeFacts(poolObjectId);
                        poolSizeCache[poolObjectId] = poolFacts;
                    }
                    thinWarning = BuildThinProvisioningWarning(poolFacts.Size, poolFacts.AllocatedSize, thinTotal, poolFacts.Thresholds);
                }

                result.Add(new StorageSpaceInfo
                {
                    PoolName = poolName,
                    VirtualDiskName = vdiskName,
                    HealthStatus = HealthStatusName(health),
                    OperationalStatus = OperationalStatusArrayText(opStatusCodes),
                    ResiliencySettingName = (vdisk["ResiliencySettingName"] as string ?? string.Empty).Trim(),
                    SizeBytes = size,
                    IsHealthWarning = health != 0,
                    VirtualDiskObjectId = vdiskObjectId,
                    MemberDisks = members,
                    ActiveJobs = jobs,
                    CanRepair = canRepair,
                    AllocatedSizeBytes = allocatedSize,
                    ProvisioningTypeText = provisioningType switch { 1 => "Thin", 2 => "Fixed", _ => "Unknown" },
                    ThinProvisioningWarningText = thinWarning,
                });
            }
        }
        catch
        {
            // Namespace/class unavailable, or (the common case) no Storage Spaces pools exist at
            // all on this system - either way, an empty list, not an error.
        }
        return result;
    }

    /// <summary>#387: initiates MSFT_VirtualDisk.Repair for one virtual disk, restoring redundancy
    /// to different/new physical disks in the pool. RunAsJob=true so a repair that takes a while
    /// returns a "started" result rather than blocking this call for however long the repair
    /// takes - the Storage tab re-reads ActiveJobs on the next refresh to show progress.</summary>
    public static (bool Started, uint ReturnCode, string Message) RepairVirtualDisk(string virtualDiskObjectId)
    {
        try
        {
            using var vdisk = new ManagementObject(@"root\Microsoft\Windows\Storage", $"MSFT_VirtualDisk.ObjectId='{EscapeWmiPath(virtualDiskObjectId)}'", null);
            vdisk.Get();

            var inParams = vdisk.GetMethodParameters("Repair");
            inParams["RunAsJob"] = true;
            var outParams = vdisk.InvokeMethod("Repair", inParams, null);
            uint code = outParams?["ReturnValue"] is null ? uint.MaxValue : Convert.ToUInt32(outParams["ReturnValue"]);
            return (code is 0 or 4096, code, RepairReturnCodeText(code));
        }
        catch (Exception ex)
        {
            return (false, uint.MaxValue, $"Repair failed: {ex.Message}");
        }
    }

    // MSFT_VirtualDisk.Repair documented return codes (Storage Management API).
    private static string RepairReturnCodeText(uint code) => code switch
    {
        0 => "Repair completed successfully.",
        1 => "Not supported on this virtual disk.",
        2 => "Unspecified error.",
        3 => "The repair timed out.",
        4 => "The repair failed.",
        5 => "Invalid parameter.",
        6 => "The virtual disk is in use.",
        4096 => "Repair started as a background job.",
        40000 => "Not enough free space in the pool to complete the repair.",
        40001 => "Access denied.",
        40002 => "Not enough resources to complete the operation.",
        46000 => "Cannot connect to the storage provider.",
        46001 => "The storage provider cannot connect to the storage subsystem.",
        50001 => "Not enough redundancy remains in the pool to repair this virtual disk.",
        50002 => "Another computer currently controls this virtual disk's configuration.",
        _ => $"Unrecognized return code {code}.",
    };

    /// <summary>#386: member physical disks for one pool, via MSFT_StoragePoolToPhysicalDisk.
    /// Empty (not an error) when the pool object can't be re-resolved by ObjectId, or has no
    /// members reported.</summary>
    private static List<PoolMemberDiskInfo> ReadPoolMembers(string poolObjectId)
    {
        var members = new List<PoolMemberDiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_StoragePool.ObjectId='{EscapeWmiPath(poolObjectId)}'}} WHERE AssocClass=MSFT_StoragePoolToPhysicalDisk");
            foreach (ManagementObject phys in searcher.Get())
            {
                string name = (phys["FriendlyName"] as string ?? "Physical disk").Trim();

                int usage = 0;
                try { usage = Convert.ToInt32(phys["Usage"] ?? 0); } catch { /* leave 0 (Unknown) */ }

                int health = 0;
                try { health = Convert.ToInt32(phys["HealthStatus"] ?? 0); } catch { /* leave 0 (Healthy) */ }

                var opCodes = (phys["OperationalStatus"] as ushort[]) ?? Array.Empty<ushort>();

                long size = 0, allocated = 0;
                try { size = Convert.ToInt64(phys["Size"] ?? 0L); } catch { /* leave 0 */ }
                try { allocated = Convert.ToInt64(phys["AllocatedSize"] ?? 0L); } catch { /* leave 0 */ }

                members.Add(new PoolMemberDiskInfo
                {
                    FriendlyName = name,
                    UsageText = PhysicalDiskUsageName(usage),
                    OperationalStatusText = string.Join(", ", opCodes.Select(PhysicalDiskOperationalStatusName)),
                    IsUnhealthy = health != 0,
                    SizeBytes = size,
                    AllocatedSizeBytes = allocated,
                });
            }
        }
        catch { /* pool object no longer resolvable, or no members reported - empty list */ }
        return members;
    }

    /// <summary>#387: in-flight jobs affecting one virtual disk, via
    /// MSFT_StorageJobToAffectedStorageObject - the documented associator between a storage job and
    /// whatever storage object(s) it's operating on. Empty on the common case of no job currently
    /// running (repairs/rebuilds only take noticeable wall-clock time while actually degraded).</summary>
    private static List<StorageJobInfo> ReadActiveJobs(string vdiskObjectId)
    {
        var jobs = new List<StorageJobInfo>();
        if (vdiskObjectId.Length == 0) return jobs;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_VirtualDisk.ObjectId='{EscapeWmiPath(vdiskObjectId)}'}} WHERE AssocClass=MSFT_StorageJobToAffectedStorageObject");
            foreach (ManagementObject job in searcher.Get())
            {
                string name = (job["Name"] as string ?? "Storage job").Trim();

                double percent = 0;
                try { percent = Convert.ToDouble(job["PercentComplete"] ?? 0); } catch { /* leave 0 */ }

                int state = 0;
                try { state = Convert.ToInt32(job["JobState"] ?? 0); } catch { /* leave 0 (Unknown) */ }

                string elapsedText = string.Empty;
                try
                {
                    if (job["ElapsedTime"] is string dmtf && dmtf.Length > 0)
                    {
                        var elapsed = ManagementDateTimeConverter.ToTimeSpan(dmtf);
                        elapsedText = elapsed.TotalDays >= 1 ? $"{elapsed.Days}d {elapsed.Hours}h {elapsed.Minutes}m" : $"{elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s";
                    }
                }
                catch { /* leave empty - ElapsedTime not reported by this provider */ }

                string errorDescription = (job["ErrorDescription"] as string ?? string.Empty).Trim();

                jobs.Add(new StorageJobInfo
                {
                    Name = name,
                    PercentComplete = percent,
                    JobStateText = JobStateName(state),
                    ElapsedTimeText = elapsedText,
                    ErrorDescription = errorDescription,
                });
            }
        }
        catch { /* no jobs currently affecting this virtual disk - empty list */ }
        return jobs;
    }

    /// <summary>#388: pool Size/AllocatedSize/ThinProvisioningAlertThresholds, re-resolved by
    /// ObjectId - a separate query from ReadPoolMembers above since MSFT_StoragePoolToPhysicalDisk's
    /// associated objects are MSFT_PhysicalDisk instances, not the pool's own properties.</summary>
    private static (long Size, long AllocatedSize, ushort[] Thresholds) ReadPoolSizeFacts(string poolObjectId)
    {
        try
        {
            using var pool = new ManagementObject(@"root\Microsoft\Windows\Storage", $"MSFT_StoragePool.ObjectId='{EscapeWmiPath(poolObjectId)}'", null);
            pool.Get();
            long size = 0, allocated = 0;
            try { size = Convert.ToInt64(pool["Size"] ?? 0L); } catch { /* leave 0 */ }
            try { allocated = Convert.ToInt64(pool["AllocatedSize"] ?? 0L); } catch { /* leave 0 */ }
            var thresholds = (pool["ThinProvisioningAlertThresholds"] as ushort[]) ?? Array.Empty<ushort>();
            return (size, allocated, thresholds);
        }
        catch
        {
            return (0, 0, Array.Empty<ushort>());
        }
    }

    /// <summary>#388: compares the pool's own physical AllocatedSize/Size against the sum of every
    /// thin virtual disk's logical Size in that pool. An over-committed pool can promise more
    /// logical space across its thin disks than it physically has - if every thin disk were filled,
    /// the pool would run out of physical capacity and take its volumes read-only with no separate
    /// warning from Windows itself, which is exactly the case this flags. Empty string (no warning
    /// shown) whenever the pool's physical capacity still covers the thin commitment.</summary>
    private static string BuildThinProvisioningWarning(long poolSize, long poolAllocated, long thinCommittedTotal, ushort[] alertThresholds)
    {
        if (poolSize <= 0) return string.Empty;

        double committedPercent = thinCommittedTotal / (double)poolSize * 100;
        double physicalUsedPercent = poolAllocated / (double)poolSize * 100;

        string thresholdText = alertThresholds.Length > 0
            ? $" Pool alert threshold(s): {string.Join("%, ", alertThresholds)}%."
            : string.Empty;

        if (thinCommittedTotal > poolSize)
        {
            return $"Over-committed: thin virtual disk(s) in this pool logically promise {Formatting.FormatBytes(thinCommittedTotal)} ({committedPercent:0}% of pool capacity), more than the pool's {Formatting.FormatBytes(poolSize)}. If every thin disk fills up, the pool runs out of physical space and its volumes go read-only without a separate warning.{thresholdText}";
        }
        if (physicalUsedPercent >= 85)
        {
            return $"Pool is {physicalUsedPercent:0}% physically allocated ({Formatting.FormatBytes(poolAllocated)} of {Formatting.FormatBytes(poolSize)}) with thin provisioning in use - worth watching before it fills.{thresholdText}";
        }
        return string.Empty;
    }

    private static string? ReadOwningPoolName(ManagementObject vdisk)
    {
        try
        {
            using var pools = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_VirtualDisk.ObjectId='{EscapeWmiPath((string)vdisk["ObjectId"])}'}} WHERE AssocClass=MSFT_StoragePoolToVirtualDisk");
            foreach (ManagementObject pool in pools.Get())
                return (pool["FriendlyName"] as string ?? string.Empty).Trim();
        }
        catch { /* fall through */ }
        return null;
    }

    private static string? ReadOwningPoolObjectId(ManagementObject vdisk)
    {
        try
        {
            using var pools = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_VirtualDisk.ObjectId='{EscapeWmiPath((string)vdisk["ObjectId"])}'}} WHERE AssocClass=MSFT_StoragePoolToVirtualDisk");
            foreach (ManagementObject pool in pools.Get())
                return pool["ObjectId"] as string;
        }
        catch { /* fall through */ }
        return null;
    }

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

    // MSFT_VirtualDisk.HealthStatus / MSFT_StoragePool.HealthStatus documented enum (Storage
    // Management API) - both classes share the same 0/1/2/5 value map.
    private static string HealthStatusName(int code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    private static string OperationalStatusArrayText(ushort[] codes) =>
        codes.Length == 0 ? string.Empty : string.Join(", ", codes.Select(OperationalStatusName));

    // MSFT_VirtualDisk.OperationalStatus / MSFT_StoragePool.OperationalStatus documented enum.
    private static string OperationalStatusName(ushort code) => code switch
    {
        2 => "OK",
        3 => "Degraded",
        4 => "Stressed",
        5 => "Predictive failure",
        6 => "Error",
        7 => "Non-recoverable error",
        8 => "Starting",
        9 => "Stopping",
        10 => "Stopped",
        11 => "In service",
        12 => "No contact",
        13 => "Lost communication",
        14 => "Aborted",
        15 => "Dormant",
        19 => "Relocating",
        0xD002 => "Detached",
        0xD003 => "Incomplete (not enough redundancy remaining)",
        _ => $"Status {code}",
    };

    // #386: MSFT_PhysicalDisk.OperationalStatus shares the same core 1-19 value map as above, plus
    // undocumented vendor-specific codes Storage Spaces itself uses (53252 = 0xD004 "Failed Media"
    // being the one this item's brief calls out by name) - checked first, falling back to the
    // shared table for everything else.
    private static string PhysicalDiskOperationalStatusName(ushort code) => code switch
    {
        53252 => "Failed media",
        53253 => "Split",
        53254 => "Stale metadata",
        53255 => "IO error",
        53256 => "Unrecognized metadata",
        _ => OperationalStatusName(code),
    };

    // MSFT_PhysicalDisk.Usage documented enum (Storage Management API).
    private static string PhysicalDiskUsageName(int code) => code switch
    {
        0 => "Unknown",
        1 => "Auto-Select",
        2 => "Manual-Select",
        3 => "Hot Spare",
        4 => "Retired",
        5 => "Journal",
        _ => $"Usage {code}",
    };

    // MSFT_StorageJob.JobState documented enum (Storage Management API).
    private static string JobStateName(int code) => code switch
    {
        2 => "New",
        3 => "Starting",
        4 => "Running",
        5 => "Suspended",
        6 => "Shutting down",
        7 => "Completed",
        8 => "Terminated",
        9 => "Killed",
        10 => "Exception",
        11 => "Service",
        12 => "Query pending",
        _ => $"State {code}",
    };
}
