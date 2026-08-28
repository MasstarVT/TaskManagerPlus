using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// #478: best-effort read of "Allow the computer to turn off this device to save power" (the
/// checkbox on a device's Power Management property tab in Device Manager) via the PNPCapabilities
/// REG_DWORD Windows stores directly under the device's own
/// HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId} key. This is a widely-referenced convention -
/// the same registry value several vendor NIC/network-adapter support articles and enterprise
/// deployment scripts use to script this exact checkbox - rather than a value documented in the
/// public cfgmgr32 CM_DEVCAP_* header, so it carries the same "quick flag, not a verdict" caveat
/// CLAUDE.md already applies to this app's other undocumented-bitmask reads (e.g. the AV/
/// mitigation-status ones). Absence of the value (most devices never had this checkbox touched)
/// reads as Unknown, not as "allowed" - never fabricated as a definite yes.
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
            if (key?.GetValue("PNPCapabilities") is not int caps) return null;
            return (caps & PowerOffDisallowedBit) == 0;
        }
        catch
        {
            return null;
        }
    }
}
