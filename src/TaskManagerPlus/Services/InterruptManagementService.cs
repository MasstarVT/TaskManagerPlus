using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #218/#219: per-device MSI/MSI-X status and interrupt-affinity policy, both read from the same
/// `Device Parameters\Interrupt Management` registry subtree under each device's Enum key
/// (`HKLM\SYSTEM\CurrentControlSet\Enum\&lt;PNPDeviceID&gt;\Device Parameters\Interrupt Management`) -
/// combined into one service/one device enumeration since #218 and #219 both need it, per
/// CLAUDE.md's granularity guidance for closely-related items. Read-only: this never writes to the
/// registry.
///
/// Device inventory comes from Win32_PnPEntity (the same class UsbPowerService/SystemSpecsService
/// already query elsewhere), whose PNPDeviceID doubles as the Enum subkey path. Most devices have no
/// `Interrupt Management` subtree at all (virtual/software devices, devices with no interrupt
/// resource) - those are silently skipped rather than reported as "line-based", since there's no
/// interrupt to report on in the first place.
///
/// On-demand only (a full PnP enumeration + one registry probe per device is not a per-tick-timer
/// cost per CLAUDE.md's on-demand rule) - loaded once at Responsiveness tab start-up plus a manual
/// refresh button.
/// </summary>
public static class InterruptManagementService
{
    // IRQ_DEVICE_POLICY values (ntddk.h) - the only stable, documented meaning for DevicePolicy.
    private static readonly Dictionary<int, string> DevicePolicyNames = new()
    {
        [0] = "Machine default",
        [1] = "All close processors",
        [2] = "One close processor",
        [3] = "All processors in machine",
        [4] = "Specified processors",
        [5] = "Spread across all processors",
        [6] = "Specified processors, when possible",
    };

    // IRQ_PRIORITY values.
    private static readonly Dictionary<int, string> DevicePriorityNames = new()
    {
        [0] = "Undefined",
        [1] = "Low",
        [2] = "Normal",
        [3] = "High",
    };

    // #218: heuristic "high-traffic" device-class/name match - "quick flag, not a verdict", the
    // same tier of honesty as KnownOffenderDriverLookup's hint table.
    private static readonly string[] HighTrafficClassNames = { "Net", "Display", "SCSIAdapter", "HDC" };
    private static readonly string[] HighTrafficNameHints = { "nvme", "ethernet", "wi-fi", "wireless", "wlan", "gpu", "graphics", "radeon", "geforce", "host controller", "xhci", "usb 3", "usb3" };

    public static Task<List<DeviceInterruptRow>> LoadAsync() => Task.Run(Load);

    private static List<DeviceInterruptRow> Load()
    {
        var rows = new List<DeviceInterruptRow>();
        List<(string Id, string Name, string Class)> devices;
        try
        {
            devices = new List<(string, string, string)>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, PNPClass FROM Win32_PnPEntity");
            foreach (ManagementObject mo in searcher.Get())
            {
                string id = mo["PNPDeviceID"] as string ?? string.Empty;
                string name = (mo["Name"] as string ?? string.Empty).Trim();
                string cls = (mo["PNPClass"] as string ?? string.Empty).Trim();
                if (id.Length == 0 || name.Length == 0) continue;
                devices.Add((id, name, cls));
            }
        }
        catch
        {
            return rows; // WMI unavailable - empty list, card shows the "no data" message
        }

        foreach (var (id, name, cls) in devices)
        {
            var row = TryReadDevice(id, name, cls);
            if (row is not null) rows.Add(row);
        }

        return rows
            .OrderByDescending(r => r.IsLineBasedHighTraffic)
            .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Returns null when the device has no Interrupt Management subtree at all (the
    /// overwhelmingly common case - most PnP entries aren't interrupt-generating hardware), so the
    /// resulting grid only lists devices this data is actually meaningful for.</summary>
    private static DeviceInterruptRow? TryReadDevice(string pnpDeviceId, string name, string pnpClass)
    {
        try
        {
            string basePath = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters\Interrupt Management";
            using var imKey = Registry.LocalMachine.OpenSubKey(basePath);
            if (imKey is null) return null;

            bool? msiSupported = null;
            int? messageLimit = null;
            using (var msiKey = imKey.OpenSubKey("MessageSignaledInterruptProperties"))
            {
                if (msiKey is not null)
                {
                    if (msiKey.GetValue("MSISupported") is int msiVal) msiSupported = msiVal != 0;
                    if (msiKey.GetValue("MessageNumberLimit") is int limitVal) messageLimit = limitVal;
                }
            }

            int? policy = null;
            int? priority = null;
            string? affinityCores = null;
            using (var affKey = imKey.OpenSubKey("Affinity Policy"))
            {
                if (affKey is not null)
                {
                    if (affKey.GetValue("DevicePolicy") is int policyVal) policy = policyVal;
                    if (affKey.GetValue("DevicePriority") is int prioVal) priority = prioVal;
                    if (affKey.GetValue("AssignmentSetOverride") is byte[] mask && mask.Length > 0)
                        affinityCores = DecodeCoreMask(mask);
                }
            }

            // No MSI info and no affinity policy at all - registry subtree exists but is empty of
            // anything actionable, same as "no subtree" for reporting purposes.
            if (msiSupported is null && messageLimit is null && policy is null && priority is null && affinityCores is null)
                return null;

            bool highTraffic = IsHighTraffic(name, pnpClass);

            return new DeviceInterruptRow
            {
                DeviceName = name,
                DeviceClass = pnpClass,
                MsiSupported = msiSupported,
                MessageNumberLimit = messageLimit,
                IsHighTrafficClass = highTraffic,
                DevicePolicy = policy,
                DevicePolicyText = policy is { } p && DevicePolicyNames.TryGetValue(p, out var pn) ? pn : "Machine default",
                AssignmentSetOverride = affinityCores,
                DevicePriority = priority,
                DevicePriorityText = priority is { } pr && DevicePriorityNames.TryGetValue(pr, out var prn) ? prn : "Unknown",
            };
        }
        catch
        {
            // Denied/malformed value - skip this one device rather than fail the whole scan.
            return null;
        }
    }

    private static bool IsHighTraffic(string name, string pnpClass)
    {
        if (HighTrafficClassNames.Contains(pnpClass, StringComparer.OrdinalIgnoreCase)) return true;
        return HighTrafficNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>AssignmentSetOverride is a bitmask (one bit per logical processor, LSB = core 0) -
    /// rendered as a comma-separated core list rather than the raw bytes.</summary>
    private static string DecodeCoreMask(byte[] mask)
    {
        var cores = new List<int>();
        for (int byteIndex = 0; byteIndex < mask.Length; byteIndex++)
        {
            byte b = mask[byteIndex];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0) cores.Add(byteIndex * 8 + bit);
            }
        }
        return cores.Count == 0 ? string.Empty : string.Join(",", cores);
    }
}
