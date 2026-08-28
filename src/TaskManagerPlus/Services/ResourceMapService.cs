using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #476: builds the Devices &amp; Drivers tab's IRQ/I-O/memory/DMA resource map from
/// Win32_PnPAllocatedResource (the association between a device and each resource it holds) joined
/// against whichever of Win32_IRQResource/Win32_PortResource/Win32_DeviceMemoryAddress/
/// Win32_DMAChannel the association actually points at - plain WMI, matching this app's "prefer a
/// known API over raw interop" convention. Every one of these classes only ever reports *currently
/// allocated* resources for present devices - there's no equivalent for a non-present device
/// (nothing is allocated to it), which is expected and not a gap in this scan.
///
/// FlagConflicts is deliberately conservative - see DeviceResourceRow's remarks on why an
/// overlapping I/O or memory range can be a perfectly ordinary parent-bus-window/child-device
/// nesting rather than a genuine conflict, and is surfaced as an informational flag, not a verdict.
/// </summary>
public static class ResourceMapService
{
    public static Task<List<DeviceResourceRow>> ScanAsync() => Task.Run(Scan);

    private static List<DeviceResourceRow> Scan()
    {
        var rows = new List<DeviceResourceRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPAllocatedResource");
            foreach (ManagementBaseObject assocBase in searcher.Get())
            {
                using var assoc = (ManagementObject)assocBase;
                try
                {
                    if (assoc["Antecedent"] is not string antecedentPath || antecedentPath.Length == 0) continue;
                    if (assoc["Dependent"] is not string dependentPath || dependentPath.Length == 0) continue;

                    using var deviceObj = new ManagementObject(dependentPath);
                    string deviceId = TryGetString(deviceObj, "DeviceID") ?? string.Empty;
                    if (deviceId.Length == 0) continue;
                    string deviceName = TryGetString(deviceObj, "Name") ?? deviceId;

                    using var resourceObj = new ManagementObject(antecedentPath);
                    string className = resourceObj.ClassPath.ClassName;

                    var row = className switch
                    {
                        "Win32_IRQResource" => BuildIrq(resourceObj, deviceId, deviceName),
                        "Win32_PortResource" => BuildPort(resourceObj, deviceId, deviceName),
                        "Win32_DeviceMemoryAddress" => BuildMemory(resourceObj, deviceId, deviceName),
                        "Win32_DMAChannel" => BuildDma(resourceObj, deviceId, deviceName),
                        _ => null,
                    };
                    if (row is not null) rows.Add(row);
                }
                catch
                {
                    // One bad/inaccessible association shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Win32_PnPAllocatedResource unavailable - empty list, same degrade-to-nothing
            // convention every other WMI sweep in this app uses.
        }

        FlagConflicts(rows);
        return rows
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.RangeStart)
            .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DeviceResourceRow? BuildIrq(ManagementObject obj, string deviceId, string deviceName)
    {
        if (!TryGetUInt64(obj, "IRQNumber", out ulong irq)) return null;
        bool? shareable = obj["Shareable"] is bool b ? b : null;
        return new DeviceResourceRow
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Kind = DeviceResourceKind.Irq,
            RangeText = $"IRQ {irq}",
            RangeStart = irq,
            RangeEnd = irq,
            IrqNumber = (int)irq,
            Shareable = shareable,
        };
    }

    private static DeviceResourceRow? BuildPort(ManagementObject obj, string deviceId, string deviceName)
    {
        if (!TryGetUInt64(obj, "StartingAddress", out ulong start)) return null;
        if (!TryGetUInt64(obj, "EndingAddress", out ulong end) || end < start) end = start;
        return new DeviceResourceRow
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Kind = DeviceResourceKind.Io,
            RangeText = start == end ? $"0x{start:X4}" : $"0x{start:X4}-0x{end:X4}",
            RangeStart = start,
            RangeEnd = end,
        };
    }

    private static DeviceResourceRow? BuildMemory(ManagementObject obj, string deviceId, string deviceName)
    {
        if (!TryGetUInt64(obj, "StartingAddress", out ulong start)) return null;
        if (!TryGetUInt64(obj, "EndingAddress", out ulong end) || end < start) end = start;
        return new DeviceResourceRow
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Kind = DeviceResourceKind.Memory,
            RangeText = start == end ? $"0x{start:X8}" : $"0x{start:X8}-0x{end:X8}",
            RangeStart = start,
            RangeEnd = end,
        };
    }

    private static DeviceResourceRow? BuildDma(ManagementObject obj, string deviceId, string deviceName)
    {
        if (!TryGetUInt64(obj, "DMAChannel", out ulong channel)) return null;
        return new DeviceResourceRow
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Kind = DeviceResourceKind.Dma,
            RangeText = $"DMA {channel}",
            RangeStart = channel,
            RangeEnd = channel,
        };
    }

    private static string? TryGetString(ManagementBaseObject obj, string property)
    {
        try { return obj[property] as string; } catch { return null; }
    }

    /// <summary>WMI's own resource classes are inconsistent about whether an address is marshaled
    /// as a numeric type or a hex string (Win32_DeviceMemoryAddress in particular tends to come
    /// back as a plain hex string with no "0x" prefix) - this accepts either, trying decimal first
    /// (the common case for IRQResource/PortResource) then hex, and simply skips the row (via the
    /// caller's null return) rather than fabricating a range from data that didn't parse.</summary>
    private static bool TryGetUInt64(ManagementBaseObject obj, string property, out ulong value)
    {
        value = 0;
        try
        {
            object? raw = obj[property];
            switch (raw)
            {
                case null:
                    return false;
                case ulong ul:
                    value = ul;
                    return true;
                case uint ui:
                    value = ui;
                    return true;
                case int i when i >= 0:
                    value = (ulong)i;
                    return true;
                case long l when l >= 0:
                    value = (ulong)l;
                    return true;
                case string s:
                    string trimmed = s.Trim();
                    if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
                    if (trimmed.Length == 0) { value = 0; return true; }
                    if (ulong.TryParse(trimmed, out value)) return true;
                    return ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out value);
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>#476: flags an IRQ shared by 2+ distinct devices where at least one side is
    /// documented non-shareable, and flags overlapping I/O port or memory ranges across different
    /// devices. See DeviceResourceRow/class remarks for why the I/O and memory checks are
    /// deliberately worded as informational rather than a confirmed conflict.</summary>
    private static void FlagConflicts(List<DeviceResourceRow> rows)
    {
        foreach (var group in rows.Where(r => r.Kind == DeviceResourceKind.Irq).GroupBy(r => r.RangeStart))
        {
            var distinctDevices = group.Select(r => r.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctDevices.Count < 2 || !group.Any(r => r.Shareable == false)) continue;

            foreach (var r in group)
            {
                r.IsFlagged = true;
                r.FlagText = $"Non-shareable IRQ {r.RangeStart} shared with {distinctDevices.Count - 1} other device(s)";
            }
        }

        foreach (var kind in new[] { DeviceResourceKind.Io, DeviceResourceKind.Memory })
        {
            var list = rows.Where(r => r.Kind == kind).OrderBy(r => r.RangeStart).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[j].RangeStart > list[i].RangeEnd) break; // sorted by start - no further overlaps possible
                    if (string.Equals(list[i].DeviceId, list[j].DeviceId, StringComparison.OrdinalIgnoreCase)) continue;

                    list[i].IsFlagged = true;
                    list[i].FlagText = $"Range overlaps {list[j].DeviceName} - often benign for a bus/bridge controller, worth a look otherwise";
                    list[j].IsFlagged = true;
                    list[j].FlagText = $"Range overlaps {list[i].DeviceName} - often benign for a bus/bridge controller, worth a look otherwise";
                }
            }
        }

        foreach (var group in rows.Where(r => r.Kind == DeviceResourceKind.Dma).GroupBy(r => r.RangeStart))
        {
            var distinctDevices = group.Select(r => r.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctDevices.Count < 2) continue;

            foreach (var r in group)
            {
                r.IsFlagged = true;
                r.FlagText = $"DMA channel {r.RangeStart} shared with {distinctDevices.Count - 1} other device(s)";
            }
        }
    }
}
