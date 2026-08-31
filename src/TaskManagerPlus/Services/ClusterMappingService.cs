using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// #335: maps a disk-relative LBA (from a #332 surface scan) back to an owning file, via
/// `fsutil volume querycluster` - the documented tool for this, rather than parsing the MFT
/// directly (the same "known tool over raw struct interop" tradeoff every other fsutil-based
/// feature in this app already takes). Necessarily approximate: `fsutil volume querycluster` takes
/// a volume-relative cluster number, but a surface scan reads whole-disk-relative LBAs, and a disk
/// can have partitions (EFI System Partition, MSR, ...) before the data volume that shift every LBA
/// on it. This resolves the disk's first fixed NTFS volume and assumes the scanned disk is that
/// volume's only partition (true for most secondary/data-only disks, not guaranteed on a
/// multi-partition boot disk) - captioned as an approximation wherever it's shown, never presented
/// as exact.
/// </summary>
public static class ClusterMappingService
{
    /// <summary>First fixed drive letter (no colon) + cluster size (bytes) physically hosted on
    /// disk <paramref name="diskIndex"/>, via the classic Win32_DiskDriveToDiskPartition /
    /// Win32_LogicalDiskToPartition associator chain - null when this disk has no assigned drive
    /// letter at all (unallocated, or a raw/dynamic disk).</summary>
    public static (string DriveLetter, int BytesPerCluster)? ResolveVolumeForDisk(int diskIndex)
    {
        try
        {
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{diskIndex}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject partition in partitions.Get())
            {
                using var logicalDisks = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                foreach (ManagementObject logical in logicalDisks.Get())
                {
                    string letter = (logical["Name"] as string ?? string.Empty).Trim().TrimEnd(':');
                    if (letter.Length == 0) continue;

                    int blockSize = 4096; // NTFS's near-universal default - overridden below when Win32_Volume reports one
                    try
                    {
                        using var volSearcher = new ManagementObjectSearcher(
                            $"SELECT BlockSize FROM Win32_Volume WHERE DriveLetter = '{letter}:'");
                        foreach (ManagementObject vol in volSearcher.Get())
                        {
                            if (vol["BlockSize"] is not null) blockSize = Convert.ToInt32(vol["BlockSize"]);
                        }
                    }
                    catch { /* fall back to the NTFS default above */ }

                    return (letter, blockSize);
                }
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    /// <summary>#359: the reverse of ResolveVolumeForDisk above - the Win32_DiskDrive.Index of the
    /// physical disk hosting drive letter <paramref name="driveLetter"/> (e.g. "C" or "C:"), via
    /// the same classic Win32_LogicalDiskToPartition / Win32_DiskDriveToDiskPartition associator
    /// chain, just walked from the volume side instead of the disk side. Used to cross-reference a
    /// page file's volume against this app's own #328 per-disk health verdict list. Null on any
    /// failure or when the volume has no backing physical disk (e.g. a network drive letter, which
    /// shouldn't reach here since callers only pass fixed-volume page file locations anyway).
    /// </summary>
    public static int? ResolveDiskIndexForVolume(string driveLetter)
    {
        try
        {
            string letter = driveLetter.TrimEnd(':', '\\');
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementObject partition in partitions.Get())
            {
                using var disks = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject disk in disks.Get())
                {
                    if (disk["Index"] is not null) return Convert.ToInt32(disk["Index"]);
                }
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    private static readonly Regex ClusterFileRegex = new(@"File Name\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Resolves one disk-relative LBA to an owning file name (or a metadata-stream name
    /// like $Mft/$LogFile), or a short "why not" string - never throws, degrades to "Not resolved"
    /// on any failure, same tier as every other on-demand shell-out in this app.</summary>
    public static async Task<string> ResolveOwningFileAsync(string driveLetter, long diskRelativeLba, int bytesPerSector, int bytesPerCluster)
    {
        try
        {
            long clusterNumber = diskRelativeLba * bytesPerSector / Math.Max(bytesPerCluster, 1);
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("fsutil.exe", $"volume querycluster {driveLetter}: {clusterNumber}", 10000);
            if (exitCode is null) return "Timed out";
            var match = ClusterFileRegex.Match(output);
            if (match.Success) return match.Groups[1].Value.Trim();
            if (output.Contains("Free", StringComparison.OrdinalIgnoreCase)) return "Free (unallocated) cluster";
            return "Not resolved";
        }
        catch
        {
            return "Not resolved";
        }
    }
}
