using System.Management;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// #639: resolves a PCIe bus/device/function address (as WHEA-Logger reports it) to a friendly
/// device name + PCI hardware ID, via Win32_PnPEntity's own LocationInfo string. Windows itself
/// stamps a "PCI bus X, device Y, function Z" (or "PCI Bus X, Device Y, Function Z") string into
/// LocationInfo for every enumerated PCI device - the same known-WMI-property approach
/// StorageSpacesService/SystemSpecsService already prefer over raw device-property interop for
/// this kind of lookup. Named devices make a flaky riser, an eGPU cable, or a dying NVMe
/// controller immediately obvious in the WHEA list instead of raw "Bus 3, Device 0, Function 0."
/// </summary>
public static class PciDeviceResolverService
{
    private static readonly Regex LocationRegex = new(
        @"PCI\s*bus\s*(\d+),\s*device\s*(\d+),\s*function\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Builds a bus/device/function -&gt; (Name, DeviceId) map for every currently
    /// enumerated PCI device, in one WMI pass - callers should build this once per refresh (not
    /// per WHEA event) and look up each event's parsed location against it. Returns an empty
    /// dictionary on any WMI failure, so lookups just come back "unresolved."</summary>
    public static Dictionary<(int Bus, int Device, int Function), (string Name, string DeviceId)> BuildLocationMap()
    {
        var map = new Dictionary<(int, int, int), (string, string)>();
        try
        {
            // One literal backslash in the WQL pattern (DeviceID values look like
            // "PCI\VEN_10DE&DEV_2482&..." - a single backslash separator, not two).
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, LocationInfo FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string location = mo["LocationInfo"] as string ?? string.Empty;
                var match = LocationRegex.Match(location);
                if (!match.Success) continue;

                int bus = int.Parse(match.Groups[1].Value);
                int device = int.Parse(match.Groups[2].Value);
                int function = int.Parse(match.Groups[3].Value);

                string name = (mo["Name"] as string ?? "PCI device").Trim();
                string deviceId = (mo["DeviceID"] as string ?? string.Empty).Trim();
                map[(bus, device, function)] = (name, deviceId);
            }
        }
        catch
        {
            // WMI unavailable - callers just see every PCIe WHEA event stay unresolved (raw
            // bus/device/function only), never a fabricated device name.
        }
        return map;
    }
}
