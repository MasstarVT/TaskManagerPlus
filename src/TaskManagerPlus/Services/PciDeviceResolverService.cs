namespace TaskManagerPlus.Services;

/// <summary>
/// #639: resolves a PCIe bus/device/function address (as WHEA-Logger reports it) to a friendly
/// device name. Windows stamps a "PCI bus X, device Y, function Z" location string on every
/// enumerated PCI device - the same "Location" Device Manager shows - but Win32_PnPEntity has no
/// LocationInfo property to read it from (the original WQL query was rejected as invalid, so every
/// WHEA event stayed unresolved), so this reuses PnpDeviceTreeService.BuildPciLocationLookup's
/// SetupAPI read of that property instead. Named devices make a flaky riser, an eGPU cable, or a
/// dying NVMe controller immediately obvious in the WHEA list instead of raw "Bus 3, Device 0,
/// Function 0."
/// </summary>
public static class PciDeviceResolverService
{
    /// <summary>Builds a bus/device/function -&gt; (Name, DeviceId) map for every currently
    /// present PCI device, in one enumeration pass - callers should build this once per refresh
    /// (not per WHEA event) and look up each event's parsed location against it. DeviceId is
    /// empty (the shared lookup carries names only). Returns an empty dictionary on failure, so
    /// lookups just come back "unresolved."</summary>
    public static Dictionary<(int Bus, int Device, int Function), (string Name, string DeviceId)> BuildLocationMap()
    {
        var map = new Dictionary<(int, int, int), (string, string)>();
        try
        {
            foreach (var (location, name) in PnpDeviceTreeService.BuildPciLocationLookup())
                map[location] = (name, string.Empty);
        }
        catch
        {
            // Enumeration unavailable - callers just see every PCIe WHEA event stay unresolved
            // (raw bus/device/function only), never a fabricated device name.
        }
        return map;
    }
}
