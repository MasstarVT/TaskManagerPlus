using System.Management;

namespace TaskManagerPlus.Services;

/// <summary>
/// #216: best-effort join from a bare driver filename (e.g. "e2f68.sys", as resolved from a DPC/ISR
/// routine address by DpcModuleMapService/DriverIdentityService) to the friendly name of the
/// device(s) it actually drives, so a Responsiveness tab row can say "Intel(R) Ethernet Controller
/// I225-V" instead of just the driver file.
///
/// Uses Win32_PnPSignedDriver rather than parsing `pnputil /enum-devices /connected` text output:
/// this class's own DriverName property is documented as "path to driver file name" (distinct from
/// DeviceName, the human-readable device description), giving a structured file-name join key
/// pnputil's device-enumeration output doesn't expose directly - the same "known WMI class over
/// shelled-out text parsing" tradeoff this app already prefers when a suitable class exists (see
/// SystemSpecsService.ReadUsbDevices, UsbPowerService). Still explicitly best-effort: a driver file
/// can back zero, one, or several device instances, and DriverName isn't populated for every class
/// of device on every Windows build - a miss just leaves DeviceName blank ("Unknown" in the UI),
/// never a guessed device.
/// </summary>
public static class DeviceInterruptAttributionService
{
    public static Task<Dictionary<string, string>> LoadDriverToDeviceMapAsync() =>
        Task.Run(LoadDriverToDeviceMap);

    private static Dictionary<string, string> LoadDriverToDeviceMap()
    {
        var byDriverFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, DeviceClass, DriverName FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceName = (mo["DeviceName"] as string ?? string.Empty).Trim();
                string driverPath = (mo["DriverName"] as string ?? string.Empty).Trim();
                if (deviceName.Length == 0 || driverPath.Length == 0) continue;

                // DriverName is a full path (e.g. "C:\WINDOWS\system32\DRIVERS\e2f68.sys") on most
                // builds/classes; take just the file name so it matches the bare filenames the
                // DPC/ISR grid already resolves driver rows to.
                int slash = Math.Max(driverPath.LastIndexOf('\\'), driverPath.LastIndexOf('/'));
                string fileName = slash >= 0 ? driverPath[(slash + 1)..] : driverPath;
                if (!fileName.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) continue;

                if (!byDriverFile.TryGetValue(fileName, out var names))
                    byDriverFile[fileName] = names = new List<string>();
                if (!names.Contains(deviceName, StringComparer.OrdinalIgnoreCase))
                    names.Add(deviceName);
            }
        }
        catch
        {
            // WMI unavailable/access denied - return whatever was gathered (likely empty); every
            // Responsiveness row just shows a blank Device column, per this app's "never fabricate"
            // rule.
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (driverFile, names) in byDriverFile)
            result[driverFile] = string.Join("; ", names);
        return result;
    }
}
