using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #92: best-effort per-USB-device selective-suspend status. Windows exposes no simple,
/// documented WMI class for "is selective suspend enabled for this device" - Device Manager's own
/// Power Management tab checkbox reads a device power-policy value
/// (DEVPKEY_Device_RemovalPolicy/CM_Power_Data) that has no clean public WMI mapping, and a
/// correct read really needs SetupAPI device-property interop, a materially larger native-interop
/// undertaking than anything else in this app (CpuTopologyService/NetworkConnectionsService's own
/// P/Invoke both read one fixed, documented struct shape - this would mean walking device
/// property sets by GUID).
///
/// Instead, this takes one real best-effort shot via the legacy `root\WMI` `MSPower_DeviceEnable`
/// class (present on many, not all, Windows builds/drivers) and matches its `InstanceName` against
/// each USB `Win32_PnPEntity.PNPDeviceID` by normalized-prefix comparison - the same prefix-match
/// technique `SystemSpecsService.ReadFailurePredictStatus` already uses for the SMART
/// failure-prediction class, since neither class publishes a clean, exact join key. When the match
/// (or the WMI class itself) isn't available for a device, this honestly reports
/// <c>null</c> ("Unknown") rather than guessing - expect that to be the common case on a fair
/// number of systems, per the assignment's own explicit "reasonably degrade" guidance for this item.
/// </summary>
public static class UsbPowerService
{
    public static List<UsbDevicePowerInfo> ReadUsbSelectiveSuspend()
    {
        var results = new List<UsbDevicePowerInfo>();

        var devices = new List<(string Name, string PnpId)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? "Unknown USB device";
                string pnpId = mo["PNPDeviceID"] as string ?? string.Empty;
                if (pnpId.Length == 0) continue;
                devices.Add((name, pnpId));
            }
        }
        catch
        {
            return results; // no USB devices readable at all - empty list, card hides
        }

        // Best-effort join to MSPower_DeviceEnable, keyed by a lowercased/underscore-normalized
        // prefix match against PNPDeviceID - not every device (or Windows build) exposes this
        // class, so a lookup miss just leaves that device's SelectiveSuspendEnabled null.
        var enableByNormalizedInstance = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT InstanceName, Enable FROM MSPower_DeviceEnable");
            foreach (ManagementObject mo in searcher.Get())
            {
                string instanceName = mo["InstanceName"] as string ?? string.Empty;
                if (instanceName.Length == 0 || mo["Enable"] is not bool enable) continue;
                enableByNormalizedInstance[Normalize(instanceName)] = enable;
            }
        }
        catch
        {
            // Class unavailable on this Windows build/driver set - every device below just
            // degrades to "Unknown" instead of a wrong guess.
        }

        foreach (var (name, pnpId) in devices)
        {
            string normalizedPnpId = Normalize(pnpId);
            bool? enabled = null;
            foreach (var (normalizedInstance, value) in enableByNormalizedInstance)
            {
                if (normalizedInstance.StartsWith(normalizedPnpId, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPnpId.StartsWith(normalizedInstance, StringComparison.OrdinalIgnoreCase))
                {
                    enabled = value;
                    break;
                }
            }

            results.Add(new UsbDevicePowerInfo { Name = name, DeviceId = pnpId, SelectiveSuspendEnabled = enabled });
        }

        return results;
    }

    private static string Normalize(string id) => id.ToLowerInvariant().Replace('\\', '_').Replace(' ', '_');
}
