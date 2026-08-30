using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #738-740: system-disk partition enumeration (MSFT_Partition/MSFT_Disk in the
/// root\Microsoft\Windows\Storage WMI namespace - the same namespace StorageSpacesService/
/// SystemSpecsService already query), shared by the EFI System Partition health card (#738), the
/// WinRE status card (#739, via reagentc.exe), and the recovery-partition layout map (#740), so
/// the disk/partition query runs once per Startup tab refresh rather than once per card.
///
/// Free-space measurement (#738/#740) is the one part of this that mutates system state, briefly:
/// a partition with no drive letter has no free-space figure any WMI class exposes directly, so
/// this temporarily assigns one with mountvol.exe, measures via DriveInfo, and always removes the
/// mount point again in a finally block - even on an exception mid-measurement, the mount point is
/// never left behind. The ESP uses mountvol's own documented `/S` shortcut ("mount the EFI System
/// Partition"); a plain data/recovery partition has no such shortcut, so its NTFS/FAT volume GUID
/// path is resolved via MSFT_PartitionToVolume first and mounted by that path instead.
/// </summary>
public static class SystemPartitionService
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    // #740: Microsoft's own documented pattern behind Windows Update failure 0x80070643 when a
    // WinRE servicing update can't land - a recovery partition under this size with under this
    // much free space.
    private const long RecoveryTooSmallSizeBytes = 750L * 1024 * 1024;
    private const long RecoveryTooSmallFreeBytes = 250L * 1024 * 1024;

    #region #738: system-disk partition layout

    /// <summary>#738: enumerates every partition on the disk that carries the running Windows
    /// installation. Never throws - a WMI/namespace failure (older Windows builds, a policy
    /// restriction) comes back as <c>Available: false</c>, same "degrade together" shape
    /// BcdInspectorService.ReadAsync already uses.</summary>
    public static SystemPartitionLayout ReadLayout()
    {
        try
        {
            int systemDiskNumber = ReadSystemDiskNumber();
            if (systemDiskNumber < 0)
                return new SystemPartitionLayout { Available = false, Error = "Couldn't determine the system disk (no partition matches the boot drive letter)." };

            var partitions = ReadPartitions(systemDiskNumber).OrderBy(p => p.OffsetBytes).ToList();
            if (partitions.Count == 0)
                return new SystemPartitionLayout { Available = false, Error = "No partitions found on the system disk." };

            return new SystemPartitionLayout
            {
                Available = true,
                SystemDiskNumber = systemDiskNumber,
                DiskFriendlyName = ReadDiskFriendlyName(systemDiskNumber),
                Partitions = partitions,
            };
        }
        catch (Exception ex)
        {
            return new SystemPartitionLayout { Available = false, Error = ex.Message };
        }
    }

    private static int ReadSystemDiskNumber()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            char sysDrive = root.TrimEnd('\\')[0];

            using var searcher = new ManagementObjectSearcher(StorageNamespace,
                $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{sysDrive}'");
            foreach (ManagementObject mo in searcher.Get())
                return Convert.ToInt32(mo["DiskNumber"]);
        }
        catch
        {
            // Fall through - -1 tells ReadLayout there's nothing to enumerate.
        }
        return -1;
    }

    private static string ReadDiskFriendlyName(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNamespace,
                $"SELECT FriendlyName FROM MSFT_Disk WHERE Number={diskNumber}");
            foreach (ManagementObject mo in searcher.Get())
                return (mo["FriendlyName"] as string ?? string.Empty).Trim();
        }
        catch { /* best-effort - falls back below */ }
        return "System disk";
    }

    private static List<DiskPartitionInfo> ReadPartitions(int diskNumber)
    {
        var result = new List<DiskPartitionInfo>();
        using var searcher = new ManagementObjectSearcher(StorageNamespace,
            $"SELECT DiskNumber, PartitionNumber, DriveLetter, Size, Offset, GptType, Type, IsBoot, IsHidden, IsActive FROM MSFT_Partition WHERE DiskNumber={diskNumber}");
        foreach (ManagementObject mo in searcher.Get())
        {
            char? letter = mo["DriveLetter"] is char c && c != '\0' ? c : null;

            result.Add(new DiskPartitionInfo
            {
                DiskNumber = diskNumber,
                PartitionNumber = TryInt(mo["PartitionNumber"]),
                DriveLetter = letter,
                SizeBytes = TryLong(mo["Size"]),
                OffsetBytes = TryLong(mo["Offset"]),
                GptType = (mo["GptType"] as string ?? string.Empty).Trim(),
                TypeFriendlyName = (mo["Type"] as string ?? string.Empty).Trim(),
                IsBoot = mo["IsBoot"] is bool b && b,
                IsHidden = mo["IsHidden"] is bool h && h,
                IsActive = mo["IsActive"] is bool a && a,
            });
        }
        return result;
    }

    private static int TryInt(object? v) { try { return Convert.ToInt32(v ?? 0); } catch { return 0; } }
    private static long TryLong(object? v) { try { return Convert.ToInt64(v ?? 0L); } catch { return 0L; } }

    #endregion

    #region #738/#740: free-space measurement via temporary mount

    /// <summary>#738: ESP free space via mountvol's documented `/S` shortcut - mounts the EFI
    /// System Partition to a scratch drive letter, measures, and always unmounts (finally block,
    /// even on an exception mid-measurement).</summary>
    public static async Task<(long? FreeBytes, string? Error)> MeasureEspFreeSpaceAsync()
        => await MountMeasureUnmountAsync(letter => $"{letter}:\\ /S");

    /// <summary>#740: free space for a partition with no drive letter of its own (typically the
    /// recovery partition) - resolves its volume's GUID path via MSFT_PartitionToVolume (mountvol
    /// has no `/S`-style shortcut for a non-ESP partition), mounts by that path, measures, and
    /// always unmounts. If the partition already has a drive letter (unusual, but not impossible
    /// for a manually-modified recovery partition), reads it directly instead of mounting
    /// anything.</summary>
    public static async Task<(long? FreeBytes, string? Error)> MeasurePartitionFreeSpaceAsync(DiskPartitionInfo partition)
    {
        if (partition.DriveLetter is { } existing)
        {
            try
            {
                var drive = new DriveInfo($"{existing}:");
                return (drive.AvailableFreeSpace, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        string? volumePath = ReadVolumeGuidPath(partition);
        if (volumePath is null)
            return (null, "This partition has no readable file system to mount (it may be unformatted, or a type Windows doesn't expose a volume for).");

        return await MountMeasureUnmountAsync(letter => $"{letter}: {volumePath}");
    }

    private static string? ReadVolumeGuidPath(DiskPartitionInfo partition)
    {
        try
        {
            using var partSearcher = new ManagementObjectSearcher(StorageNamespace,
                $"SELECT ObjectId FROM MSFT_Partition WHERE DiskNumber={partition.DiskNumber} AND PartitionNumber={partition.PartitionNumber}");
            foreach (ManagementObject part in partSearcher.Get())
            {
                string objectId = (string)part["ObjectId"];
                using var volSearcher = new ManagementObjectSearcher(StorageNamespace,
                    $"ASSOCIATORS OF {{MSFT_Partition.ObjectId='{EscapeWmiPath(objectId)}'}} WHERE AssocClass=MSFT_PartitionToVolume");
                foreach (ManagementObject vol in volSearcher.Get())
                {
                    if (vol["Path"] is string path && !string.IsNullOrWhiteSpace(path)) return path;
                }
            }
        }
        catch { /* fall through - null tells the caller nothing was found */ }
        return null;
    }

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

    /// <summary>Picks an unused drive letter (Z downward, to avoid colliding with letters already
    /// in everyday use), mounts via <paramref name="buildMountArgs"/>, measures free space, and
    /// unmounts - the unmount always runs, even if mounting itself half-succeeded or measuring
    /// threw, so this app never leaves an orphaned mount point behind.</summary>
    private static async Task<(long? FreeBytes, string? Error)> MountMeasureUnmountAsync(Func<char, string> buildMountArgs)
    {
        char? letter = FindUnusedDriveLetter();
        if (letter is null) return (null, "No free drive letter available to mount this partition temporarily.");
        char l = letter.Value;
        bool mounted = false;

        try
        {
            var (mountOutput, mountExit) = await RunCapturedAsync("mountvol.exe", buildMountArgs(l));
            if (mountExit != 0)
                return (null, string.IsNullOrWhiteSpace(mountOutput) ? "mountvol couldn't mount this partition." : mountOutput.Trim());
            mounted = true;

            try
            {
                var drive = new DriveInfo($"{l}:");
                return (drive.AvailableFreeSpace, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            if (mounted)
            {
                try { await RunCapturedAsync("mountvol.exe", $"{l}: /D"); }
                catch { /* best-effort - an orphaned scratch mount point is unusual but recoverable by hand */ }
            }
        }
    }

    private static char? FindUnusedDriveLetter()
    {
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c)) return c;
        return null;
    }

    #endregion

    #region #739: WinRE status via reagentc

    private static readonly Regex ReagentcLineRegex = new(@"^\s*([^:]+):\s*(.*)$", RegexOptions.Compiled);

    /// <summary>#739: parses `reagentc /info` - Windows RE status/location/BCD identifier/
    /// recovery-image location, read adaptively by label text (reagentc's exact wording/line order
    /// isn't a versioned contract this app controls, same tradeoff as bcdedit's own text parse).</summary>
    public static async Task<WinReStatusInfo> ReadWinReStatusAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("reagentc.exe", "/info");
            if (exitCode != 0)
                return new WinReStatusInfo { Available = false, Error = string.IsNullOrWhiteSpace(output) ? "reagentc /info failed (needs Administrator)." : output.Trim() };

            bool? enabled = null;
            string? location = null, bcdId = null, imageLocation = null;

            foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                var match = ReagentcLineRegex.Match(raw);
                if (!match.Success) continue;
                string key = match.Groups[1].Value.Trim();
                string value = match.Groups[2].Value.Trim();
                if (value.Length == 0) continue;

                if (key.Contains("Windows RE status", StringComparison.OrdinalIgnoreCase))
                    enabled = value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                else if (key.Contains("Windows RE location", StringComparison.OrdinalIgnoreCase))
                    location = value;
                else if (key.Contains("BCD identifier", StringComparison.OrdinalIgnoreCase))
                    bcdId = value;
                else if (key.Contains("Recovery image location", StringComparison.OrdinalIgnoreCase))
                    imageLocation = value;
            }

            return new WinReStatusInfo
            {
                Available = true,
                Enabled = enabled,
                Location = location,
                BcdIdentifier = bcdId,
                RecoveryImageLocation = imageLocation,
            };
        }
        catch (Exception ex)
        {
            return new WinReStatusInfo { Available = false, Error = ex.Message };
        }
    }

    /// <summary>#739: `reagentc /enable` - only ever called after the caller has shown this exact
    /// command in a confirmation dialog.</summary>
    public static async Task<(bool Success, string? Error)> EnableWinReAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("reagentc.exe", "/enable");
        return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "reagentc /enable failed." : output.Trim());
    }

    #endregion

    #region #740: recovery-partition-too-small flag

    /// <summary>#740: flags the documented "recovery partition too small for the WinRE servicing
    /// update" pattern (sub-750 MB, under ~250 MB free - causes Windows Update failure
    /// 0x80070643). Guidance text only; this app never repartitions - see RecoveryPartitionFlag's
    /// remarks.</summary>
    public static RecoveryPartitionFlag EvaluateRecoveryPartition(DiskPartitionInfo? recovery, long? freeBytes, string? measureError)
    {
        if (recovery is null)
            return new RecoveryPartitionFlag { TooSmallForServicing = false, Message = "No recovery partition was found on the system disk." };

        bool sizeIsSmall = recovery.SizeBytes > 0 && recovery.SizeBytes < RecoveryTooSmallSizeBytes;
        bool tooSmall = sizeIsSmall && freeBytes is { } f && f < RecoveryTooSmallFreeBytes;

        string message = tooSmall
            ? $"This recovery partition is small ({recovery.SizeText}, {Formatting.FormatBytes(freeBytes!.Value)} free) - a known cause of Windows Update failure 0x80070643 when a WinRE servicing update can't fit. Microsoft's documented fix is to shrink the adjacent Windows partition and delete/recreate the recovery partition larger (see support.microsoft.com's WinRE partition resize guidance) - this app never repartitions or resizes disks itself."
            : sizeIsSmall && freeBytes is null
                ? $"This recovery partition is small ({recovery.SizeText}) - couldn't measure its free space to confirm the 0x80070643 pattern ({measureError ?? "not measured"})."
                : "This recovery partition's size looks fine for WinRE servicing updates.";

        return new RecoveryPartitionFlag { TooSmallForServicing = tooSmall, FreeBytes = freeBytes, MeasureError = measureError, Message = message };
    }

    #endregion

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
