using System.Management;

namespace TaskManagerPlus.Services;

/// <summary>
/// #646/#647/#648: a lightweight, single-instance Win32_Battery read - cheap enough for the
/// per-tick timer (one small WMI SELECT, the same cost class HardwareMonitorService's own
/// per-tick Win32_PageFileUsage read already accepts), unlike the powercfg-based battery report
/// and SRUM scan elsewhere in this feature, which are real subprocess calls gated behind their
/// own on-demand buttons. Supplies the pieces the sensor tree (SensorMonitorService/
/// LibreHardwareMonitorLib) doesn't: a second, independent "is there a battery right now" signal
/// for gauge-dropout detection (#647), Windows' own EstimatedRunTime for the design-vs-actual
/// runtime comparison (#646), and the AC/charging status code used to recognize a vendor
/// charge-limit ceiling (#648).
/// </summary>
public static class BatteryStatusService
{
    /// <summary>WMI's documented sentinel for "Windows doesn't have an estimate" on
    /// EstimatedRunTime - never a real number of minutes.</summary>
    private const uint UnknownRunTimeMinutes = 71582788;

    public sealed record Win32BatterySnapshot(
        bool Present,
        double? EstimatedChargePercent,
        TimeSpan? EstimatedRunTime,
        string? BatteryStatusText)
    {
        public static readonly Win32BatterySnapshot NotPresent = new(false, null, null, null);
    }

    public static Win32BatterySnapshot Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, EstimatedRunTime, BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject mo in searcher.Get())
            {
                double? chargePercent = null;
                try { if (mo["EstimatedChargeRemaining"] is { } c) chargePercent = Convert.ToDouble(c); }
                catch { /* leave Unknown */ }

                TimeSpan? runTime = null;
                try
                {
                    if (mo["EstimatedRunTime"] is { } rt)
                    {
                        uint minutes = Convert.ToUInt32(rt);
                        if (minutes > 0 && minutes < UnknownRunTimeMinutes) runTime = TimeSpan.FromMinutes(minutes);
                    }
                }
                catch { /* leave Unknown */ }

                string? statusText = null;
                try { if (mo["BatteryStatus"] is { } s) statusText = MapBatteryStatus(Convert.ToInt32(s)); }
                catch { /* leave Unknown */ }

                return new Win32BatterySnapshot(true, chargePercent, runTime, statusText);
            }
        }
        catch
        {
            // Class unavailable/blocked, or genuinely no battery instance - either way, "not
            // present" for this tick is the honest answer; #647's dropout detector treats this
            // the same as the sensor tree reporting nothing.
        }
        return Win32BatterySnapshot.NotPresent;
    }

    /// <summary>Win32_Battery.BatteryStatus's documented value set (1-11). Code 2, "AC power, not
    /// charging", is the same state Windows reports once a vendor charge-limit ceiling is holding
    /// the battery below 100% - see EnergyThermalsViewModel's #648 remarks.</summary>
    private static string MapBatteryStatus(int code) => code switch
    {
        1 => "Discharging",
        2 => "On AC (not charging)",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging and high",
        8 => "Charging and low",
        9 => "Charging and critical",
        10 => "Undefined",
        11 => "Partially charged",
        _ => "Unknown",
    };
}
