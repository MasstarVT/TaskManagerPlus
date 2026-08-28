using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #466: groups Win32_PnPSignedDriver entries by their device's first hardware ID (the same
/// "strongest available identical-hardware signal" DriverInventoryService.ComputeMatchQuality
/// already reads per device, via HKLM\SYSTEM\CurrentControlSet\Enum\&lt;deviceId&gt;\HardwareID) and
/// flags groups of two-or-more devices sharing that ID but bound to two-or-more distinct
/// DriverVersion values - e.g. two identical NICs, one still on an old driver after the other was
/// updated. Only inconsistent groups are returned; a healthy system with every identical device on
/// the same version isn't a finding worth showing.
/// </summary>
public static class DriverVersionConsistencyService
{
    public static Task<List<DriverVersionConsistencyGroup>> ScanAsync() => Task.Run(Scan);

    private static List<DriverVersionConsistencyGroup> Scan()
    {
        var byHardwareId = new Dictionary<string, List<DriverVersionConsistencyDevice>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string? deviceId = mo["DeviceID"] as string;
                    if (string.IsNullOrEmpty(deviceId)) continue;

                    string? hardwareId = ReadFirstHardwareId(deviceId);
                    if (string.IsNullOrEmpty(hardwareId)) continue;

                    string deviceName = (mo["DeviceName"] as string ?? deviceId).Trim();
                    string driverVersion = (mo["DriverVersion"] as string ?? string.Empty).Trim();

                    DateTime? driverDate = null;
                    if (mo["DriverDate"] is string wmiDate)
                    {
                        try { driverDate = ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { /* leave null */ }
                    }

                    if (!byHardwareId.TryGetValue(hardwareId, out var list))
                        byHardwareId[hardwareId] = list = new List<DriverVersionConsistencyDevice>();
                    list.Add(new DriverVersionConsistencyDevice { DeviceName = deviceName, DriverVersion = driverVersion, DriverDate = driverDate });
                }
            }
        }
        catch
        {
            // WMI unavailable/hiccup - return whatever partial grouping was gathered before it failed.
        }

        var result = new List<DriverVersionConsistencyGroup>();
        foreach (var (hardwareId, devices) in byHardwareId)
        {
            if (devices.Count < 2) continue; // "two identical devices" needs at least two instances
            bool inconsistent = devices.Select(d => d.DriverVersion).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (!inconsistent) continue;

            result.Add(new DriverVersionConsistencyGroup { HardwareId = hardwareId, Devices = devices });
        }
        return result.OrderByDescending(g => g.Devices.Count).ThenBy(g => g.HardwareId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ReadFirstHardwareId(string deviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            var ids = key?.GetValue("HardwareID") as string[];
            return ids is { Length: > 0 } ? ids[0] : null;
        }
        catch
        {
            return null;
        }
    }
}
