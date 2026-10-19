using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// #478: best-effort read of "Allow the computer to turn off this device to save power" (the
/// checkbox on a device's Power Management property tab in Device Manager) via the PnPCapabilities
/// REG_DWORD. Microsoft's own guidance for network adapters puts it in the device's driver (software)
/// key, HKLM\SYSTEM\CurrentControlSet\Control\Class\{class-guid}\NNNN, which the device's Enum key
/// names in its "Driver" value; some deployment scripts write it under the Enum\{deviceId} key
/// instead, so both are read, driver key first. The bit meaning is a widely-referenced convention
/// rather than a documented cfgmgr32 CM_DEVCAP_* constant, so it carries the same "quick flag, not a
/// verdict" caveat CLAUDE.md already applies to this app's other undocumented-bitmask reads (e.g.
/// the AV/mitigation-status ones). Absence of the value (most devices never had this checkbox
/// touched) reads as Unknown, not as "allowed" - never fabricated as a definite yes.
/// </summary>
public static class DevicePowerCapabilityService
{
    // The convention's "power-off disallowed" bit, observed consistently across vendor support
    // docs/scripts for this exact checkbox - not a documented cfgmgr32 CM_DEVCAP_* constant.
    private const int PowerOffDisallowedBit = 0x10;

    public static Dictionary<string, bool?> ReadAllowTurnOff(IEnumerable<string> deviceIds)
    {
        var results = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceId in deviceIds)
        {
            if (results.ContainsKey(deviceId)) continue;
            results[deviceId] = ReadOne(deviceId);
        }
        return results;
    }

    private static bool? ReadOne(string deviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            if (key is null) return null;

            if (key.GetValue("Driver") is string driver && driver.Length > 0)
            {
                using var driverKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
                if (driverKey?.GetValue("PnPCapabilities") is int driverCaps) return (driverCaps & PowerOffDisallowedBit) == 0;
            }

            if (key.GetValue("PnPCapabilities") is int caps) return (caps & PowerOffDisallowedBit) == 0;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
