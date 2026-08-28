using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #224: every device Win32_PnPEntity reports a nonzero ConfigManagerErrorCode for - exactly what
/// Device Manager itself flags with a yellow bang, read via the same class SystemSpecsService's
/// ReadUsbDevices/UsbPowerService already query elsewhere in this app, just without the USB-only
/// filter. Error codes are decoded through a small table of the most common, well-documented
/// CM_PROB_* values; anything outside that table shows as "Unknown error code N" rather than a
/// guessed meaning - the same "degrade to Unknown, never fabricate" rule this app applies to every
/// other undocumented-enum read (AV/mitigation bitmasks, bugcheck codes, ...).
/// </summary>
public static class ProblemDeviceService
{
    // CM_PROB_* codes (cfgmgr32.h) - the handful most commonly seen in the wild, not an exhaustive
    // list of every defined value.
    private static readonly Dictionary<int, string> ErrorCodeText = new()
    {
        [1] = "Device is not configured correctly",
        [3] = "Driver may be corrupted, or the system may be low on memory",
        [9] = "Firmware didn't give the device enough resource information",
        [10] = "Device cannot start",
        [12] = "Insufficient free resources (I/O, IRQ, memory, or DMA)",
        [14] = "Device cannot work properly until the system is restarted",
        [16] = "Windows cannot identify all of this device's resources",
        [18] = "Reinstall the drivers for this device",
        [19] = "Windows cannot start this hardware device because its configuration information (in the registry) is incomplete or damaged",
        [21] = "Windows is removing this device",
        [22] = "Device is disabled",
        [24] = "Device is not present, not working properly, or does not have all its drivers installed",
        [28] = "Drivers for this device are not installed",
        [29] = "Disabled by firmware (BIOS/UEFI) - possibly not enough resources to allocate",
        [31] = "Driver failed to load, or a resource conflict prevented this device from working properly",
        [32] = "Driver for this device was disabled - an alternate driver may be providing this functionality",
        [33] = "Windows cannot determine which resources are required for this device",
        [34] = "Windows cannot determine the settings for this device - manual configuration may be required",
        [35] = "Firmware/BIOS does not include enough information to properly configure and use this device",
        [36] = "Device is requesting a PCI interrupt but is configured for an ISA interrupt (or vice versa)",
        [37] = "Driver returned a failure while loading",
        [38] = "A previous driver instance for this device is still loaded in memory",
        [39] = "Driver is missing or corrupted",
        [41] = "Driver loaded but couldn't find the device's hardware",
        [42] = "Duplicate device was detected",
        [43] = "Device reported a problem to its driver (hardware failure)",
        [44] = "Application or service has shut down this hardware device",
        [45] = "Device is not present because it was disconnected/removed",
        [46] = "Device is not available because the system is shutting down",
        [47] = "Device is not safe to remove yet",
        [48] = "Driver is blocked from loading (known-incompatible/blocked driver)",
        [52] = "Windows cannot verify this device's digital signature",
    };

    public static Task<List<ProblemDeviceRow>> LoadAsync() => Task.Run(Load);

    private static List<ProblemDeviceRow> Load()
    {
        var rows = new List<ProblemDeviceRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity");
            foreach (ManagementObject mo in searcher.Get())
            {
                int errorCode;
                try { errorCode = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0); }
                catch { continue; }
                if (errorCode == 0) continue;

                string name = (mo["Name"] as string ?? "Unknown device").Trim();
                rows.Add(new ProblemDeviceRow
                {
                    Name = name,
                    ConfigManagerErrorCode = errorCode,
                    ErrorText = ErrorCodeText.TryGetValue(errorCode, out var text) ? text : $"Unknown error code {errorCode}",
                });
            }
        }
        catch
        {
            // WMI unavailable/access denied - return whatever was gathered before the failure.
        }
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
