using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #624: best-effort PSU identification via Win32_PowerSupply (rarely populated - most consumer/
/// DIY boards don't implement the optional SMBus PSU-reporting side-channel this class depends
/// on) and, when that's empty, Win32_SystemEnclosure's own free-text Description/Caption fields
/// (some OEM pre-builts stamp a wattage into the chassis description). Either way this is a
/// best-effort identification, not a guaranteed reading - EnergyThermalsViewModel falls back to
/// the user-entered wattage in psu.json (PsuSettingsService) whenever this returns a name with no
/// wattage, which is the common case on a self-built desktop.
/// </summary>
public static class PsuService
{
    public static PsuInfo? ReadPsuInventory()
    {
        var fromPowerSupply = TryReadWin32PowerSupply();
        if (fromPowerSupply is not null) return fromPowerSupply;

        return TryReadSystemEnclosure();
    }

    private static PsuInfo? TryReadWin32PowerSupply()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Caption, MaxOutputWattage FROM Win32_PowerSupply");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? mo["Caption"] as string ?? "Power supply").Trim();
                double? wattage = null;
                try
                {
                    if (mo["MaxOutputWattage"] is { } raw && Convert.ToDouble(raw) > 0)
                        wattage = Convert.ToDouble(raw);
                }
                catch { /* leave null - not every implementation populates this field */ }

                return new PsuInfo { Name = name, RatedWattageW = wattage, Source = "Win32_PowerSupply" };
            }
        }
        catch
        {
            // Class unavailable (the common case - it's an optional SMBus-backed class most
            // consumer boards never implement) - fall through to the enclosure check.
        }
        return null;
    }

    // A handful of OEM pre-built desktops stamp something like "300W" into the chassis
    // description/caption text - not a documented convention, just an observed pattern worth one
    // best-effort regex pass over free text that's already being read for other purposes
    // (SystemSpecsService's own chassis/form-factor readout already queries this same class).
    private static readonly System.Text.RegularExpressions.Regex WattageInTextRegex =
        new(@"(\d{2,4})\s*W\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static PsuInfo? TryReadSystemEnclosure()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Caption, Description FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in searcher.Get())
            {
                string manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim();
                string caption = (mo["Caption"] as string ?? mo["Description"] as string ?? string.Empty).Trim();
                if (manufacturer.Length == 0 && caption.Length == 0) continue;

                string name = string.IsNullOrEmpty(manufacturer) ? caption : $"{manufacturer} chassis";

                double? wattage = null;
                var match = WattageInTextRegex.Match(caption);
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w)) wattage = w;

                return new PsuInfo { Name = name, RatedWattageW = wattage, Source = "Win32_SystemEnclosure" };
            }
        }
        catch
        {
            // Class unavailable - degrade to "no WMI-reported PSU info", the caller falls back to
            // the user-entered wattage entirely.
        }
        return null;
    }
}
