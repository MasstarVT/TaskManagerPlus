using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #477: reads each device's MSI-vs-line-based interrupt mode and CPU affinity policy from
/// HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management -
/// MessageSignaledInterruptProperties\MSISupported (DWORD 0/1) and Affinity Policy\DevicePolicy
/// (DWORD, IRQ_DEVICE_POLICY enum) - plain registry reads, no interop needed. Display-only per the
/// suggestion text: this app offers no action to change either value.
///
/// Only devices whose "Interrupt Management" key exists *and* has at least one of the two values
/// actually set are returned - most devices never had either touched, and reporting "line-based"
/// for a device that simply has no interrupt-management registry presence at all would be exactly
/// the kind of fabricated value CLAUDE.md's "degrade to Unknown/hidden, never fabricate" rule
/// warns against.
/// </summary>
public static class InterruptModeService
{
    public static Task<List<InterruptModeInfo>> ScanAsync(IEnumerable<(string DeviceId, string DeviceName)> devices) =>
        Task.Run(() => Scan(devices.ToList()));

    private static List<InterruptModeInfo> Scan(List<(string DeviceId, string DeviceName)> devices)
    {
        var results = new List<InterruptModeInfo>();
        foreach (var (deviceId, deviceName) in devices)
        {
            try
            {
                using var interruptKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management");
                if (interruptKey is null) continue;

                bool? msiSupported = null;
                using (var msiKey = interruptKey.OpenSubKey("MessageSignaledInterruptProperties"))
                {
                    if (msiKey?.GetValue("MSISupported") is int msi) msiSupported = msi != 0;
                }

                string affinityPolicyText = "Machine default";
                bool hasAffinityPolicy = false;
                using (var affinityKey = interruptKey.OpenSubKey("Affinity Policy"))
                {
                    if (affinityKey?.GetValue("DevicePolicy") is int policy)
                    {
                        affinityPolicyText = DescribePolicy(policy);
                        hasAffinityPolicy = true;
                    }
                }

                if (msiSupported is null && !hasAffinityPolicy) continue; // nothing actually configured here

                results.Add(new InterruptModeInfo
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    MsiSupported = msiSupported,
                    AffinityPolicyText = affinityPolicyText,
                });
            }
            catch
            {
                // Access denied / malformed key for this one device - skip it, don't fabricate.
            }
        }
        return results.OrderBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>IRQ_DEVICE_POLICY values (wdm.h) - the same enum the "Affinity Policy" DevicePolicy
    /// DWORD stores.</summary>
    private static string DescribePolicy(int policy) => policy switch
    {
        0 => "Machine default",
        1 => "All close processors",
        2 => "One close processor",
        3 => "All processors in machine",
        4 => "Specified processors",
        5 => "Spread across all processors",
        _ => $"Policy {policy}",
    };
}
