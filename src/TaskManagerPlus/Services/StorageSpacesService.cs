using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Storage Spaces / RAID member health rollup (#85), via MSFT_VirtualDisk in the same
/// root\Microsoft\Windows\Storage namespace SystemSpecsService already queries for SSD wear and
/// page file location. Storage Spaces is an opt-in Windows feature most desktops/laptops never
/// configure at all, so an empty result here is the expected, common case, not a failure - the
/// Storage tab collapses the whole card when this returns nothing, the same "hidden when not
/// applicable" pattern the Battery/outdated-driver sections already use elsewhere in this app.
/// </summary>
public static class StorageSpacesService
{
    public static List<StorageSpaceInfo> List()
    {
        var result = new List<StorageSpaceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, HealthStatus, OperationalStatus, ResiliencySettingName, Size FROM MSFT_VirtualDisk");
            foreach (ManagementObject vdisk in searcher.Get())
            {
                string vdiskName = (vdisk["FriendlyName"] as string ?? "Virtual disk").Trim();

                int health = 0;
                try { health = Convert.ToInt32(vdisk["HealthStatus"] ?? 0); } catch { /* leave 0 (Healthy) */ }

                long size = 0;
                try { size = Convert.ToInt64(vdisk["Size"] ?? 0L); } catch { /* leave 0 */ }

                string poolName = ReadOwningPoolName(vdisk) ?? "Storage pool";

                result.Add(new StorageSpaceInfo
                {
                    PoolName = poolName,
                    VirtualDiskName = vdiskName,
                    HealthStatus = HealthStatusName(health),
                    OperationalStatus = OperationalStatusArrayText(vdisk["OperationalStatus"]),
                    ResiliencySettingName = (vdisk["ResiliencySettingName"] as string ?? string.Empty).Trim(),
                    SizeBytes = size,
                    IsHealthWarning = health != 0,
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

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

    // MSFT_VirtualDisk.HealthStatus documented enum (Storage Management API).
    private static string HealthStatusName(int code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    private static string OperationalStatusArrayText(object? raw)
    {
        if (raw is not ushort[] codes || codes.Length == 0) return string.Empty;
        return string.Join(", ", codes.Select(OperationalStatusName));
    }

    private static string OperationalStatusName(ushort code) => code switch
    {
        2 => "OK",
        3 => "Degraded",
        5 => "Predictive failure",
        6 => "Error",
        _ => $"Status {code}",
    };
}
