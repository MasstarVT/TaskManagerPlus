using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Four small, unrelated per-volume facts (round 9, #37/#40/#42/#44) bundled into one file because
/// each is a single, self-contained read with its own graceful-degradation story - the same
/// "bundled because they answer one question together" shape SystemSpecsService.ReadSecurityInfo
/// already uses for TPM/Secure Boot/VBS. Every method here is wrapped independently and never
/// throws past this class.
/// </summary>
public static class VolumeDiagnosticsService
{
    /// <summary>
    /// BitLocker conversion/protection status (#37), via Win32_EncryptableVolume in
    /// root\CIMV2\Security\MicrosoftVolumeEncryption. Both the namespace itself and its
    /// GetConversionStatus/GetProtectionStatus methods can legitimately be denied even to this
    /// app's elevated process on some Windows SKUs (BitLocker isn't available on every edition) or
    /// under a stricter local policy - the same "Unknown, not a false negative" tradeoff
    /// SystemSpecsService.ReadTpmStatus already takes for a similar WMI security namespace.
    /// </summary>
    public static string ReadBitLockerStatus(string driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}'");
            foreach (ManagementObject vol in searcher.Get())
            {
                uint conversionStatus = 0, protectionStatus = 0;
                try
                {
                    var convOut = vol.InvokeMethod("GetConversionStatus", vol.GetMethodParameters("GetConversionStatus"), null);
                    if (convOut?["ConversionStatus"] is not null) conversionStatus = Convert.ToUInt32(convOut["ConversionStatus"]);
                }
                catch { /* leave 0 (treated as "Fully Decrypted") */ }

                try
                {
                    var protOut = vol.InvokeMethod("GetProtectionStatus", vol.GetMethodParameters("GetProtectionStatus"), null);
                    if (protOut?["ProtectionStatus"] is not null) protectionStatus = Convert.ToUInt32(protOut["ProtectionStatus"]);
                }
                catch { /* leave 0 */ }

                string conversion = conversionStatus switch
                {
                    0 => "Off",
                    1 => "On",
                    2 => "Encrypting",
                    3 => "Decrypting",
                    4 => "Encryption paused",
                    5 => "Decryption paused",
                    _ => "Unknown",
                };
                if (conversionStatus == 1)
                    return protectionStatus == 1 ? "On (protected)" : "On (protection suspended)";
                return conversion;
            }
            // Query succeeded but found no instance for this drive letter - not a BitLocker-
            // capable volume, or the volume simply has no encryption status object (e.g. a
            // read-only optical drive) rather than a real "Unknown".
            return "Not applicable";
        }
        catch
        {
            // Namespace/method access denied (non-Enterprise/Pro edition, policy, ...) - "Unknown"
            // rather than a false "Off".
            return "Unknown";
        }
    }

    private const uint FileNotFound = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    /// <summary>Recycle Bin size on this volume (#40), via the native SHQueryRecycleBinW call -
    /// the same one Explorer's own Recycle Bin properties dialog reads, since there's no managed
    /// .NET API for it. Null on any failure (e.g. a removable/network volume with no Recycle Bin,
    /// or an empty bin some drivers report as a query failure rather than a zero).</summary>
    public static long? ReadRecycleBinBytes(string driveLetter)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            string root = driveLetter.TrimEnd('\\') + @"\";
            int hr = SHQueryRecycleBin(root, ref info);
            return hr == 0 ? info.i64Size : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TRIM (delete notify) status (#44), via `fsutil behavior query
    /// DisableDeleteNotify &lt;drive&gt;` - the documented, known-tool way to read this (the same
    /// "shell out to a known Windows tool rather than raw interop" tradeoff defrag.exe/schtasks.exe/
    /// sc.exe already take elsewhere in this app). fsutil prints "DisableDeleteNotify = 0" (TRIM
    /// enabled) or "= 1" (disabled) either as a single system-wide line or one line per volume type
    /// (NTFS/ReFS) when a drive letter is supplied - this looks for the first "= 0" or "= 1" it
    /// finds. Only meaningful for SSD volumes; callers should skip this for HDDs, mirroring how
    /// HDD fragmentation is hidden for SSDs.</summary>
    public static async Task<bool?> ReadTrimStatusAsync(string driveLetter)
    {
        try
        {
            var psi = new ProcessStartInfo("fsutil.exe", $"behavior query DisableDeleteNotify {driveLetter}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern TracerouteService.RunAsync uses. The previous version already checked
            // WaitForExit's result and killed the process on timeout, but only *after* the
            // unbounded synchronous ReadToEnd() calls above it had already returned - so fsutil
            // filling its stdout/stderr pipe buffer before exiting could still deadlock before the
            // timeout/kill logic was ever reached. Starting both reads and the bounded wait
            // concurrently fixes that ordering.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(5000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return null;
            }

            string output = (await outputTask) + (await errorTask);

            var match = Regex.Match(output, @"=\s*([01])");
            if (!match.Success) return null;
            return match.Groups[1].Value == "0"; // 0 = delete notify (TRIM) enabled
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shadow copy (VSS) storage used per volume (#42), via `vssadmin list shadowstorage` - the
    /// same known-tool tradeoff as the TRIM check above; VSS's own storage-allocation internals
    /// aren't exposed through any simpler managed API. Empty (not an error) on the very common
    /// case where no volume has any shadow copies at all.
    ///
    /// Round 21, #397: this method used to own the `list shadowstorage` shell-out/parse directly.
    /// It now delegates to VssService.ReadShadowStorageAsync (which extends the same parse to also
    /// carry Allocated/Maximum for the new Volume Shadow Copy card) rather than shelling out to the
    /// same command a second time - this method's signature and behavior for its existing caller
    /// (SystemSpecsService.ReadVolumesAsync) are unchanged.
    /// </summary>
    public static async Task<Dictionary<string, long>> ReadShadowCopyUsageByVolumeAsync()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = await VssService.ReadShadowStorageAsync();
            foreach (var entry in entries)
                if (!string.IsNullOrEmpty(entry.Volume))
                    result[entry.Volume.TrimEnd(':')] = entry.UsedBytes; // "For volume" (source) entry - the one that matters for "how much is used on this drive", keyed bare (no colon) same as before.
        }
        catch
        {
            // vssadmin unavailable, or (very common) VSS simply isn't configured on any volume.
        }
        return result;
    }
}
