using System.Management;
using Microsoft.Win32;
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

        var devices = new List<(string Name, string PnpId, string ClassGuid)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, PNPDeviceID, ClassGuid FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? "Unknown USB device";
                string pnpId = mo["PNPDeviceID"] as string ?? string.Empty;
                string classGuid = mo["ClassGuid"] as string ?? string.Empty;
                if (pnpId.Length == 0) continue;
                devices.Add((name, pnpId, classGuid));
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

        foreach (var (name, pnpId, classGuid) in devices)
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

            results.Add(new UsbDevicePowerInfo
            {
                Name = name,
                DeviceId = pnpId,
                SelectiveSuspendEnabled = enabled,
                RiskClass = ClassifyRisk(classGuid, name),
            });
        }

        return results;
    }

    private static string Normalize(string id) => id.ToLowerInvariant().Replace('\\', '_').Replace(' ', '_');

    /// <summary>#667: joins one device's PNPDeviceID against
    /// UsbEventLogService.ReadReenumerationEvents' normalized-instance-keyed count dictionary,
    /// using the same best-effort normalized-prefix match this file's MSPower_DeviceEnable join
    /// above already uses (neither source publishes a clean, exact common key). Returns 0 (a
    /// confirmed zero, not "Unknown" - the scan ran and found nothing for this device) when no
    /// count entry's prefix matches.</summary>
    public static int FindReenumerationCount(string pnpDeviceId, Dictionary<string, int> countsByNormalizedInstance)
    {
        string normalizedPnpId = Normalize(pnpDeviceId);
        foreach (var (normalizedInstance, count) in countsByNormalizedInstance)
        {
            if (normalizedInstance.StartsWith(normalizedPnpId, StringComparison.OrdinalIgnoreCase) ||
                normalizedPnpId.StartsWith(normalizedInstance, StringComparison.OrdinalIgnoreCase))
                return count;
        }
        return 0;
    }

    // #668: Windows' own well-known, publicly documented "system-defined device setup class"
    // GUIDs - stable across Windows versions, unlike this file's undocumented text/WMI-instance
    // matching elsewhere. Matched first (more reliable than a name guess); DiskDrive/Media/HIDClass
    // cover the three groups #668 calls out (external HDDs, USB audio interfaces/DACs, HID input).
    private const string DiskDriveClassGuid = "{4d36e967-e325-11ce-bfc1-08002be10318}";
    private const string MediaClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private const string HidClassGuid = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
    private const string KeyboardClassGuid = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const string MouseClassGuid = "{4d36e96f-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] AudioNameHints = { "audio", "dac", "sound", "headset", "microphone", "speaker" };

    /// <summary>#668: "quick flag, not a verdict" - a device belonging to one of these classes
    /// isn't guaranteed to misbehave under selective suspend, just known to be a common source of
    /// reports when it does (crackling/dropout on USB audio, dropped input on HID, a drive that
    /// disconnects on external HDDs). Falls back to a name-hint match for audio devices whose
    /// ClassGuid wasn't reported (WMI leaves ClassGuid empty for a fair number of composite/
    /// class-driver-pending devices).</summary>
    internal static string ClassifyRisk(string classGuid, string name)
    {
        if (string.Equals(classGuid, DiskDriveClassGuid, StringComparison.OrdinalIgnoreCase))
            return "External storage";
        if (string.Equals(classGuid, MediaClassGuid, StringComparison.OrdinalIgnoreCase) ||
            AudioNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return "USB audio interface / DAC";
        if (string.Equals(classGuid, HidClassGuid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(classGuid, KeyboardClassGuid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(classGuid, MouseClassGuid, StringComparison.OrdinalIgnoreCase))
            return "HID input";
        return string.Empty;
    }

    /// <summary>#668's actual fix action: flips the same two device-power-policy registry values
    /// Device Manager's own "Allow the computer to turn off this device to save power" checkbox
    /// ultimately reads/writes for this device - <c>SelectiveSuspendEnabled</c> and
    /// <c>EnhancedPowerManagementEnabled</c>, both DWORDs under this device instance's own
    /// <c>Device Parameters</c> subkey. PNPDeviceID maps 1:1 onto the registry Enum tree's path
    /// (backslash-separated instance segments), so no extra device-instance lookup is needed
    /// beyond the PNPDeviceID this app already reads via WMI. Requires administrator privileges -
    /// this app runs elevated throughout (CLAUDE.md's elevation note), so a real permissions
    /// failure here would mean something else is wrong (e.g. a driver-locked key).</summary>
    public static (bool Success, string? Error) SetSelectiveSuspendEnabled(string pnpDeviceId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return (false, "No device ID.");
        try
        {
            string subKey = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters";
            using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true);
            if (key is null) return (false, "Couldn't open this device's registry key.");

            key.SetValue("SelectiveSuspendEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("EnhancedPowerManagementEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
