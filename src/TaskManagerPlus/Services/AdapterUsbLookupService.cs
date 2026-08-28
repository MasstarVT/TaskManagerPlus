using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Supports #550's "Selective Suspend on USB adapters" flag. NetworkInterface (and the registry
/// advanced-property surface AdapterAdvancedPropertyService reads) has no PNPDeviceID, and the
/// existing #92 UsbPowerService.ReadUsbSelectiveSuspend keys its own results by PNPDeviceID, not an
/// adapter GUID - so this reads the one WMI class that carries both a network adapter's connection
/// GUID (matching NetworkInterface.Id) and its PNPDeviceID
/// (<see href="https://learn.microsoft.com/windows/win32/cimwin32prov/win32-networkadapter">Win32_NetworkAdapter</see>),
/// bridging the two without modifying UsbPowerService itself. Same "no exact join key, best-effort
/// prefix match" tradeoff UsbPowerService's own join to MSPower_DeviceEnable already accepts -
/// duplicated here (rather than shared) per this app's existing precedent for small, single-use
/// private helpers (see WifiEventLogService/WifiProfileService's own duplicated netsh-shelling
/// helpers).
/// </summary>
public static class AdapterUsbLookupService
{
    /// <summary>NetworkInterface.Id (braces stripped) -> PNPDeviceID, for every adapter WMI reports
    /// both fields for. A miss just means that adapter isn't found here - callers treat it the same
    /// as "not a USB adapter" rather than an error.</summary>
    public static Dictionary<string, string> ReadPnpDeviceIdsByGuid()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT GUID, PNPDeviceID FROM Win32_NetworkAdapter");
            foreach (ManagementObject mo in searcher.Get())
            {
                string guid = (mo["GUID"] as string ?? string.Empty).Trim('{', '}');
                string pnpId = (mo["PNPDeviceID"] as string ?? string.Empty).Trim();
                if (guid.Length > 0 && pnpId.Length > 0) result[guid] = pnpId;
            }
        }
        catch
        {
            // WMI unavailable - degrade to "no USB cross-reference for any adapter" rather than throw.
        }
        return result;
    }

    /// <summary>True/false when a USB device matching <paramref name="adapterPnpDeviceId"/> was
    /// found in <paramref name="usbDevices"/> and itself has a known Selective Suspend state; null
    /// when no match is found, or the match's own state is unknown (UsbPowerService's own MSPower_
    /// DeviceEnable read came up empty for it) - never guessed either way.</summary>
    public static bool? FindSelectiveSuspend(List<UsbDevicePowerInfo> usbDevices, string? adapterPnpDeviceId)
    {
        if (string.IsNullOrEmpty(adapterPnpDeviceId)) return null;
        string normalizedAdapter = Normalize(adapterPnpDeviceId);

        foreach (var device in usbDevices)
        {
            string normalizedDevice = Normalize(device.DeviceId);
            if (normalizedDevice.StartsWith(normalizedAdapter, StringComparison.OrdinalIgnoreCase) ||
                normalizedAdapter.StartsWith(normalizedDevice, StringComparison.OrdinalIgnoreCase))
                return device.SelectiveSuspendEnabled;
        }
        return null;
    }

    private static string Normalize(string id) => id.ToLowerInvariant().Replace('\\', '_').Replace(' ', '_');
}
