using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #692: best-effort OEM thermal-profile/fan-mode probe across the three vendor WMI surfaces this
/// app can reach without a vendor SDK - Lenovo's <c>root\WMI</c> <c>LENOVO_GAMEZONE_DATA</c> class
/// (its <c>GetSmartFanMode</c> method - a read-only query, never the corresponding Set method), HP's
/// <c>root\HP\InstrumentedBIOS</c> <c>HP_BIOSEnumeration</c> "Thermal" setting, and Dell/Alienware's
/// <c>root\dcim\sysman</c> <c>DCIM_BIOSEnumeration</c> "ThermalManagement" attribute (the Dell
/// Client Command Suite WMI provider's own BIOS-setting surface). Every probe here is read-only (a
/// SELECT query, or a "Get"-prefixed method call with no side effects) - this app never invokes a
/// Set method on an OEM WMI surface it can't fully validate, per the same "never fabricate, degrade
/// instead" convention every other best-effort probe in this app follows.
///
/// Tried in this fixed order; the first vendor whose namespace/class/property actually resolves
/// wins. Degrades to <see cref="OemThermalProfileInfo.Unknown"/> ("Unknown — no OEM thermal
/// namespace on this system") when none of the three are present, which is the expected, common
/// outcome on a self-built desktop or a laptop from a vendor this app doesn't recognize - not a bug,
/// and never a guessed mode.
/// </summary>
public static class OemThermalProfileService
{
    public static OemThermalProfileInfo Probe()
        => TryLenovo() ?? TryHp() ?? TryDell() ?? OemThermalProfileInfo.Unknown;

    /// <summary>Lenovo Legion/gaming-series "Smart Fan Mode" - documented (informally, by Lenovo's
    /// own Vantage/Legion-family utilities) as 1 = Quiet, 2 = Balanced, 3 = Performance, 255 =
    /// Custom. GetSmartFanMode is a query method, not a WMI property, on this class.</summary>
    private static OemThermalProfileInfo? TryLenovo()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM LENOVO_GAMEZONE_DATA"));
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    using var result = mo.InvokeMethod("GetSmartFanMode", null) as ManagementBaseObject;
                    if (result?["Data"] is { } raw)
                    {
                        int mode = Convert.ToInt32(raw);
                        return new OemThermalProfileInfo
                        {
                            Available = true,
                            Vendor = "Lenovo",
                            ModeText = mode switch
                            {
                                1 => "Quiet",
                                2 => "Balanced",
                                3 => "Performance",
                                255 => "Custom",
                                _ => $"Unknown mode ({mode})",
                            },
                        };
                    }
                }
                catch
                {
                    // Class present but this generation doesn't support GetSmartFanMode - fall
                    // through to Unknown rather than guessing a mode.
                }
            }
        }
        catch
        {
            // root\WMI or LENOVO_GAMEZONE_DATA not present - not a Lenovo Legion/gaming-series
            // machine, the expected common case.
        }
        return null;
    }

    private static OemThermalProfileInfo? TryHp()
    {
        try
        {
            var scope = new ManagementScope(@"root\HP\InstrumentedBIOS");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name, Value FROM HP_BIOSEnumeration WHERE Name='Thermal'"));
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["Value"] is string value && !string.IsNullOrWhiteSpace(value))
                    return new OemThermalProfileInfo { Available = true, Vendor = "HP", ModeText = value.Trim() };
            }
        }
        catch
        {
            // root\HP\InstrumentedBIOS not present, or this model doesn't expose a "Thermal" BIOS
            // setting through it - not every HP model does.
        }
        return null;
    }

    private static OemThermalProfileInfo? TryDell()
    {
        try
        {
            var scope = new ManagementScope(@"root\dcim\sysman");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT AttributeName, CurrentValue FROM DCIM_BIOSEnumeration WHERE AttributeName='ThermalManagement'"));
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["CurrentValue"] is string value && !string.IsNullOrWhiteSpace(value))
                    return new OemThermalProfileInfo { Available = true, Vendor = "Dell/Alienware", ModeText = value.Trim() };
            }
        }
        catch
        {
            // root\dcim\sysman not present (the Dell Client Command Suite WMI provider isn't
            // installed on every Dell system), or no ThermalManagement attribute on this model.
        }
        return null;
    }
}
