using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #669: best-effort USB-C/Thunderbolt Power Delivery readout via UCSI (USB Type-C Connector
/// System Software Interface). Windows' UCSI class driver (present since Windows 10 1809 on
/// platforms whose firmware exposes a UCSI-compliant embedded controller interface) publishes its
/// diagnostic data under the root\wmi namespace, but Microsoft has never published a stable,
/// versioned class-name/property contract for it the way it has for e.g. Win32_USBHub - unlike
/// UsbPowerService's MSPower_DeviceEnable class (confirmed live on a real dev machine, per that
/// file's own remarks), this app has no way to confirm a specific UCSI class/property name ahead
/// of time, and guessing one that happens to be wrong would either silently find nothing (harmless
/// but not "real" per this app's degrade-never-fabricate stance) or, worse, look like a genuine
/// negative result on a system that actually does have UCSI hardware.
///
/// Instead, this queries the WMI schema itself (root\wmi's meta_class) for any class whose name
/// contains "Ucsi", and - only if one is actually found on this system - reports every property
/// that class's instances actually expose, under the exact names Windows publishes them, as a
/// generic key/value dump rather than a fabricated Voltage/Current/PowerRole mapping. A light,
/// clearly-labeled best-effort summary line is added only when a property's own name looks like a
/// voltage/current/power-role reading. On every system without a UCSI class published at all (the
/// common desktop case, and the common case even on many USB-C laptops whose EC doesn't implement
/// UCSI) this returns an empty list and the card hides entirely, exactly as #669 asks.
/// </summary>
public static class UsbPdService
{
    private static readonly string[] VoltageHints = { "voltage", "volt" };
    private static readonly string[] CurrentHints = { "current", "amperage", "milliamp" };
    private static readonly string[] RoleHints = { "role", "direction", "orientation" };

    public static async Task<List<UsbPdConnectorInfo>> ReadPdConnectorsAsync()
        => await Task.Run(() =>
        {
            var result = new List<UsbPdConnectorInfo>();
            List<string> ucsiClassNames;
            try
            {
                ucsiClassNames = new List<string>();
                using var classSearcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM meta_class");
                foreach (ManagementBaseObject mc in classSearcher.Get())
                {
                    using (mc)
                    {
                        string className = ((ManagementClass)mc).ClassPath.ClassName;
                        if (className.Contains("Ucsi", StringComparison.OrdinalIgnoreCase))
                            ucsiClassNames.Add(className);
                    }
                }
            }
            catch
            {
                return result; // root\wmi's meta_class itself unavailable - hide the card
            }

            foreach (var className in ucsiClassNames)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\wmi", $"SELECT * FROM {className}");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        using (mo)
                        {
                            var props = new List<UsbPdProperty>();
                            foreach (PropertyData prop in mo.Properties)
                            {
                                if (prop.Value is null) continue;
                                string valueText = prop.IsArray
                                    ? string.Join(", ", ((Array)prop.Value).Cast<object>().Select(v => v?.ToString() ?? string.Empty))
                                    : prop.Value.ToString() ?? string.Empty;
                                props.Add(new UsbPdProperty { Name = prop.Name, Value = valueText });
                            }
                            if (props.Count == 0) continue;

                            result.Add(new UsbPdConnectorInfo
                            {
                                ClassName = className,
                                Properties = props,
                                SummaryText = Summarize(props),
                            });
                        }
                    }
                }
                catch
                {
                    // This particular UCSI-named class exists but couldn't be queried (permissions,
                    // an instance-less abstract class, ...) - skip it, other matches still show.
                }
            }

            return result;
        });

    /// <summary>Best-effort, name-hint-only summary line - never invents a unit or a value this
    /// class's actual property didn't already state.</summary>
    internal static string Summarize(List<UsbPdProperty> props)
    {
        string? Find(string[] hints) => props.FirstOrDefault(p => hints.Any(h => p.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))?.Value;

        string? voltage = Find(VoltageHints);
        string? current = Find(CurrentHints);
        string? role = Find(RoleHints);

        var parts = new List<string>();
        if (voltage is not null) parts.Add($"Voltage: {voltage}");
        if (current is not null) parts.Add($"Current: {current}");
        if (role is not null) parts.Add($"Role: {role}");
        return string.Join(" · ", parts);
    }
}
