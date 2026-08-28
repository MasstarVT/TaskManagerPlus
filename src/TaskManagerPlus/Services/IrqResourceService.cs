using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #217: IRQ sharing map - enumerates Win32_PnPAllocatedResource (an associator class linking each
/// device to the hardware resources it was actually assigned) filtered down to entries whose
/// Antecedent is a Win32_IRQResource, then joins the Dependent device path back to a friendly name
/// via Win32_PnPEntity. Groups by IRQ number so a line with three or more sharers (level-triggered
/// PCI IRQs sharing two-deep is normal; three or more is the classic "worth a second look" case)
/// gets flagged. On-demand only (device/IRQ topology essentially never changes tick to tick, and
/// WMI associator queries aren't cheap enough for a per-tick timer per CLAUDE.md's on-demand rule) -
/// loaded once at Responsiveness tab start-up plus a manual refresh button.
/// </summary>
public static class IrqResourceService
{
    private static readonly Regex IrqNumberRegex = new(@"IRQNumber\s*=\s*""?(-?\d+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceIdRegex = new(@"Win32_PnPEntity\.DeviceID\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Task<List<IrqShareRow>> LoadAsync() => Task.Run(Load);

    private static List<IrqShareRow> Load()
    {
        var rows = new List<IrqShareRow>();
        try
        {
            var deviceNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var deviceSearcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity"))
            {
                foreach (ManagementObject mo in deviceSearcher.Get())
                {
                    string id = mo["PNPDeviceID"] as string ?? string.Empty;
                    string name = (mo["Name"] as string ?? string.Empty).Trim();
                    if (id.Length == 0 || name.Length == 0) continue;
                    deviceNamesById[id] = name;
                }
            }
            if (deviceNamesById.Count == 0) return rows;

            var namesByIrq = new Dictionary<int, List<string>>();
            using (var resSearcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_PnPAllocatedResource"))
            {
                foreach (ManagementObject mo in resSearcher.Get())
                {
                    string antecedent = mo["Antecedent"]?.ToString() ?? string.Empty;
                    if (!antecedent.Contains("Win32_IRQResource", StringComparison.OrdinalIgnoreCase)) continue;

                    var irqMatch = IrqNumberRegex.Match(antecedent);
                    if (!irqMatch.Success || !int.TryParse(irqMatch.Groups[1].Value, out int irq)) continue;

                    string dependent = mo["Dependent"]?.ToString() ?? string.Empty;
                    var devMatch = DeviceIdRegex.Match(dependent);
                    if (!devMatch.Success) continue;

                    // WMI's relative-path syntax escapes '\' as '\\' inside a quoted key value -
                    // undo that (not URL/percent-encoding, so Uri.UnescapeDataString would be the
                    // wrong tool here) to get back the real PNPDeviceID for the dictionary lookup.
                    string deviceId = devMatch.Groups[1].Value.Replace(@"\\", @"\");
                    if (!deviceNamesById.TryGetValue(deviceId, out var name)) continue;

                    if (!namesByIrq.TryGetValue(irq, out var names))
                        namesByIrq[irq] = names = new List<string>();
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            foreach (var (irq, names) in namesByIrq)
                rows.Add(new IrqShareRow { IrqNumber = irq, DeviceNames = names });
        }
        catch
        {
            // WMI unavailable/access denied - return whatever was gathered; the card shows an
            // empty grid with the surrounding explanatory text, never fabricated rows.
        }
        return rows.OrderByDescending(r => r.SharerCount).ThenBy(r => r.IrqNumber).ToList();
    }
}
