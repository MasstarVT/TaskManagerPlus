using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #624: best-effort PSU identification via root\cimv2\power Win32_PowerSupply (its rating is
/// rarely populated - most consumer/DIY boards don't implement the optional PSU-reporting
/// side-channel it depends on) and, when that's empty, Win32_SystemEnclosure's own free-text Description/Caption fields
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

    /// <summary>Win32_PowerSupply lives in root\cimv2\power (there is no such class in root\cimv2,
    /// which is where this used to look), and its rating is TotalOutputPower in milliwatts (CIM
    /// qualifier Units=MilliWatts; 0/null = unknown) - there is no MaxOutputWattage property.
    /// Windows exposes a generic "Microsoft Power Supply Device" instance there even when nothing
    /// reports a rating, so an instance only counts when it carries a plausible wattage;
    /// otherwise this falls through to the enclosure check.</summary>
    private static PsuInfo? TryReadWin32PowerSupply()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2\power",
                "SELECT Name, Caption, TotalOutputPower FROM Win32_PowerSupply");
            foreach (ManagementObject mo in searcher.Get())
            {
                double watts;
                try
                {
                    if (mo["TotalOutputPower"] is not { } raw) continue;
                    watts = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture) / 1000.0;
                }
                catch { continue; /* not every implementation populates this field */ }
                if (watts is < 50 or > 5000) continue; // 0 = unknown; anything else this far out isn't a real rating

                string name = (mo["Name"] as string ?? mo["Caption"] as string ?? "Power supply").Trim();
                return new PsuInfo { Name = name, RatedWattageW = watts, Source = "Win32_PowerSupply" };
            }
        }
        catch
        {
            // Namespace/class unavailable - fall through to the enclosure check.
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
