using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #376: currently-attached removable drives' identity (Win32_DiskDrive - model,
/// interface, media type, size) plus the Device Manager "Policies" tab's Quick removal/Better
/// performance setting for each (its "Device Parameters" registry key) - shown in the same card as
/// #375's connection history, since both are about the same "external drives" concept, just
/// currently-attached vs. ever-attached.
/// </summary>
public static class RemovalPolicyService
{
    public static List<RemovableDriveFacts> ReadCurrentRemovableDrives()
    {
        var result = new List<RemovableDriveFacts>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, Size, MediaType, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject mo in searcher.Get())
            {
                // Win32_DiskDrive.MediaType reads "Removable Media" for USB mass-storage disks -
                // the same field SystemSpecsService.ReadDisks already surfaces for the System tab's
                // disk list, used here to filter down to just removable disks.
                string mediaType = (mo["MediaType"] as string ?? string.Empty).Trim();
                if (!mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase)) continue;

                int index = -1;
                try { index = Convert.ToInt32(mo["Index"] ?? -1); } catch { /* leave -1 */ }

                string pnpId = (mo["PNPDeviceID"] as string ?? string.Empty).Trim();

                string driveLetter = string.Empty;
                if (index >= 0)
                {
                    try { driveLetter = ClusterMappingService.ResolveVolumeForDisk(index)?.DriveLetter ?? string.Empty; }
                    catch { /* leave empty - the row still shows without a resolved letter */ }
                }

                long size = 0;
                try { size = Convert.ToInt64(mo["Size"] ?? 0L); } catch { /* leave 0 */ }

                result.Add(new RemovableDriveFacts
                {
                    DriveLetter = driveLetter,
                    DiskIndex = index,
                    Model = (mo["Model"] as string ?? "Unknown disk").Trim(),
                    InterfaceType = (mo["InterfaceType"] as string ?? string.Empty).Trim(),
                    MediaType = mediaType,
                    PnpDeviceId = pnpId,
                    SizeBytes = size,
                    RemovalPolicyRaw = pnpId.Length > 0 ? ReadRemovalPolicyRaw(pnpId) : null,
                });
            }
        }
        catch
        {
            // WMI unavailable - empty list, card hides, same degrade tier as everywhere else in
            // this app.
        }
        return result;
    }

    /// <summary>
    /// The Device Manager "Policies" tab's Quick removal/Better performance radio writes a DWORD
    /// "UserRemovalPolicy" value (1 = quick removal, 2 = better performance) somewhere under the
    /// device's "Device Parameters" registry key - but not at one single documented subkey; it's
    /// been observed directly under "Device Parameters" on some driver/Windows-version combinations
    /// and one level further under "Device Parameters\Classpnp" on others, so both plausible spots
    /// are tried in order and the first hit wins. Null (shown as "Default") when neither is present
    /// - the common, expected case for a device the user has never touched this setting for, not a
    /// failure.
    /// </summary>
    private static int? ReadRemovalPolicyRaw(string pnpDeviceId)
    {
        string basePath = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters";
        foreach (var candidate in new[] { basePath, $@"{basePath}\Classpnp" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(candidate);
                if (key?.GetValue("UserRemovalPolicy") is int policy) return policy;
            }
            catch { /* try the next candidate location */ }
        }
        return null;
    }
}
