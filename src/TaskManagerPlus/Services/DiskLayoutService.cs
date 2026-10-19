using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #385: unallocated tail space (over-provisioning) and 4K partition-alignment facts per
/// disk - MSFT_Disk/MSFT_Partition (root\Microsoft\Windows\Storage), the same Storage Management
/// API namespace DiskFragmentationService's associator chain and StorageViewModel's volume facts
/// already use elsewhere in this app. Framed factually throughout, per this item's brief:
/// deliberate over-provisioning is a valid, common choice on SSDs (nothing to flag), misalignment
/// genuinely costs read-modify-write overhead (worth noting, not alarming).
/// </summary>
public static class DiskLayoutService
{
    public static List<DiskLayoutInfo> ReadAll()
    {
        var result = new List<DiskLayoutInfo>();
        try
        {
            using var diskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT Number, ObjectId, Size, FriendlyName, LargestFreeExtent FROM MSFT_Disk");
            foreach (ManagementObject disk in diskSearcher.Get())
            {
                result.Add(ReadOneDisk(disk));
            }
        }
        catch
        {
            // Storage Management API namespace unavailable - empty list, card hides same as every
            // other WMI-only card in this app.
        }
        return result;
    }

    private static DiskLayoutInfo ReadOneDisk(ManagementObject disk)
    {
        int number = -1;
        try { number = Convert.ToInt32(disk["Number"] ?? -1); } catch { /* leave -1 */ }
        string model = (disk["FriendlyName"] as string ?? $"Disk {number}").Trim();

        try
        {
            long size = disk["Size"] is null ? 0 : Convert.ToInt64(disk["Size"]);
            long? largestFreeExtent = disk["LargestFreeExtent"] is null ? null : Convert.ToInt64(disk["LargestFreeExtent"]);
            string objectId = (string)disk["ObjectId"];

            var partitions = new List<PartitionAlignmentInfo>();
            long partitionedBytes = 0;
            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_Disk.ObjectId={QuoteWmiKey(objectId)}}} WHERE AssocClass=MSFT_DiskToPartition");
            foreach (ManagementObject part in partSearcher.Get())
            {
                int partNumber = part["PartitionNumber"] is null ? 0 : Convert.ToInt32(part["PartitionNumber"]);
                string driveLetterVal = part["DriveLetter"] is char c && c != '\0' ? c.ToString() : string.Empty;
                long offset = part["Offset"] is null ? 0 : Convert.ToInt64(part["Offset"]);
                long partSize = part["Size"] is null ? 0 : Convert.ToInt64(part["Size"]);
                partitionedBytes += partSize;

                partitions.Add(new PartitionAlignmentInfo
                {
                    PartitionNumber = partNumber,
                    DriveLetter = driveLetterVal,
                    StartingOffsetBytes = offset,
                    SizeBytes = partSize,
                    IsAligned = offset % 4096 == 0,
                });
            }

            bool isSsd = number >= 0 && ReadIsSsd(number);

            return new DiskLayoutInfo
            {
                DiskIndex = number,
                Model = model,
                IsSsd = isSsd,
                DiskSizeBytes = size,
                PartitionedBytes = partitionedBytes,
                LargestFreeExtentBytes = largestFreeExtent,
                Partitions = partitions.OrderBy(p => p.PartitionNumber).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new DiskLayoutInfo { DiskIndex = number, Model = model, Available = false, UnavailableReason = $"Could not read partition layout: {ex.Message}" };
        }
    }

    /// <summary>There is no MSFT_Disk -&gt; MSFT_PhysicalDisk association class, so this goes by
    /// number: MSFT_Disk.Number is the PhysicalDriveN index, which MSFT_PhysicalDisk exposes as
    /// DeviceId (same lookup DiskFragmentationService uses for a drive letter). False when it
    /// can't be read - the model labels that "HDD/Unknown", not "HDD".</summary>
    private static bool ReadIsSsd(int diskNumber)
    {
        try
        {
            using var physSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'");
            foreach (ManagementObject phys in physSearcher.Get())
            {
                if (phys["MediaType"] is null) continue;
                return Convert.ToInt32(phys["MediaType"]) == 4; // 4 = SSD, per MSFT_PhysicalDisk.MediaType
            }
        }
        catch { /* fall through */ }
        return false;
    }

    // MSFT ObjectIds embed double quotes, so the key must be double-quoted (as in the object's own
    // __RELPATH) - a single-quoted key fails with "Invalid query".
    private static string QuoteWmiKey(string objectId) => "\"" + objectId.Replace(@"\", @"\\").Replace("\"", "\\\"") + "\"";
}
