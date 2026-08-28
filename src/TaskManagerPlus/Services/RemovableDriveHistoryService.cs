using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #375: "every storage device ever attached to this PC", from the two registry branches
/// PnP records device history under - HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR (USB mass-storage
/// devices - flash drives, external HDDs/SSDs) and \Enum\SWD\WPDBUSENUM (the synthetic Windows
/// Portable Devices bus - phones, cameras, media players enumerated over MTP/PTP). There is no WMI
/// class or documented API for "device connection history" as such; this is the standard PnP
/// device-instance registry shape every USB-history forensics tool (USBDeview, USB Historian, ...)
/// reads from, walked directly.
///
/// Friendly name + serial come straight from each instance key's own values/name and are reliable.
/// First/last-connected timestamps are read from the device's Properties subkey
/// ({83da6326-97a6-4088-9453-a1923f573b29}\0065 = DEVPKEY_Device_FirstInstallDate, \0066 =
/// DEVPKEY_Device_LastArrivalDate - property IDs 101/102 in hex, zero-padded) as an 8-byte FILETIME
/// under a "Data" value - a real but undocumented-by-Microsoft registry layout, so every read here
/// is wrapped defensively and degrades to a null timestamp (not a guess) whenever the shape doesn't
/// match on a given Windows build; FriendlyName/Serial still populate either way, per this item's
/// own explicit "partial gap is acceptable" guidance.
/// </summary>
public static class RemovableDriveHistoryService
{
    private const string DevicePropertiesGuid = "{83da6326-97a6-4088-9453-a1923f573b29}";
    private const string FirstInstallDatePropertyId = "0065"; // DEVPKEY_Device_FirstInstallDate (pid 101)
    private const string LastArrivalDatePropertyId = "0066";  // DEVPKEY_Device_LastArrivalDate (pid 102)

    public static List<RemovableDriveHistoryEntry> ReadHistory()
    {
        var result = new List<RemovableDriveHistoryEntry>();
        ReadEnumBranch(result, @"SYSTEM\CurrentControlSet\Enum\USBSTOR", "USB mass storage");
        ReadEnumBranch(result, @"SYSTEM\CurrentControlSet\Enum\SWD\WPDBUSENUM", "Portable device (WPD)");
        return result
            .OrderByDescending(e => e.LastConnected ?? DateTime.MinValue)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadEnumBranch(List<RemovableDriveHistoryEntry> into, string rootPath, string sourceLabel)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null) return; // branch doesn't exist on this machine - nothing of this kind was ever attached

            foreach (var deviceTypeName in root.GetSubKeyNames())
            {
                RegistryKey? deviceTypeKey = null;
                try { deviceTypeKey = root.OpenSubKey(deviceTypeName); }
                catch { /* access denied on this one device-type key - skip it */ }
                if (deviceTypeKey is null) continue;

                using (deviceTypeKey)
                {
                    foreach (var instanceName in deviceTypeKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var instanceKey = deviceTypeKey.OpenSubKey(instanceName);
                            if (instanceKey is null) continue;

                            string friendlyName = (instanceKey.GetValue("FriendlyName") as string)
                                ?? (instanceKey.GetValue("DeviceDesc") as string)
                                ?? deviceTypeName;
                            friendlyName = CleanDeviceDesc(friendlyName);

                            into.Add(new RemovableDriveHistoryEntry
                            {
                                FriendlyName = friendlyName,
                                Serial = instanceName,
                                Source = sourceLabel,
                                FirstConnected = ReadPropertyDate(instanceKey, FirstInstallDatePropertyId),
                                LastConnected = ReadPropertyDate(instanceKey, LastArrivalDatePropertyId),
                            });
                        }
                        catch
                        {
                            // One malformed/inaccessible device instance shouldn't stop the rest of
                            // the scan.
                        }
                    }
                }
            }
        }
        catch
        {
            // Root key denied or missing entirely - this branch just contributes nothing, same
            // degrade-to-empty tier as every other registry sweep in this app.
        }
    }

    /// <summary>DeviceDesc/FriendlyName can be an INF string-table reference like
    /// "@disk.inf,%disk_devdesc%;Disk drive" rather than literal text - the human-readable part is
    /// always after the last ';' when the value takes that shape.</summary>
    private static string CleanDeviceDesc(string raw)
    {
        int idx = raw.LastIndexOf(';');
        return idx >= 0 && idx < raw.Length - 1 ? raw[(idx + 1)..].Trim() : raw.Trim();
    }

    private static DateTime? ReadPropertyDate(RegistryKey instanceKey, string propertyIdHex)
    {
        try
        {
            using var propsKey = instanceKey.OpenSubKey($@"Properties\{DevicePropertiesGuid}\{propertyIdHex}");
            using var valueKey = propsKey?.OpenSubKey("00000000");
            if (valueKey?.GetValue("Data") is byte[] { Length: 8 } data)
            {
                long fileTime = BitConverter.ToInt64(data, 0);
                if (fileTime > 0) return DateTime.FromFileTime(fileTime);
            }
        }
        catch
        {
            // Undocumented registry shape didn't match on this Windows build/device - leave null
            // rather than guess. FriendlyName/Serial (read by the caller) are unaffected.
        }
        return null;
    }
}
