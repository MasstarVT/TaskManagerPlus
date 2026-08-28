namespace TaskManagerPlus.Services;

/// <summary>#551's read result. <see cref="DeviceMayBePoweredOff"/> is null whenever the value can't
/// be confidently interpreted (no PnPCapabilities value set at all - most drivers don't write one,
/// leaving Windows' own default in effect - or a value that isn't the specific bit pattern this app
/// actually recognizes) - <see cref="Explanation"/> always has something readable to show either
/// way, per CLAUDE.md's "degrade to Unknown, never fabricate" rule.</summary>
public sealed record AdapterPowerManagementInfo(
    string? RawPnpCapabilities, bool? DeviceMayBePoweredOff, string Explanation,
    bool? ArpOffloadEnabled, bool? WakeOnMagicPacketEnabled);

/// <summary>
/// Item #551: "Allow the computer to turn off this device to save power" - by a wide margin the
/// single most common cause of "no internet after resuming from sleep" this app can actually detect,
/// per the suggestion text. Device Manager's Power Management tab checkbox for this is backed by the
/// adapter's own <c>PnPCapabilities</c> DWORD, a plain (if functionally undocumented) value living
/// right alongside the `*`-prefixed keywords AdapterAdvancedPropertyService already reads - CLAUDE.md's
/// "AV/mitigation-status reads use undocumented bitmask/registry conventions" caveat applies here
/// too: <c>0x18</c> (24) is the specific value widely and consistently documented across NIC
/// vendor/support guidance as "both power-management checkboxes locked off by the driver" - anything
/// else this app doesn't recognize is shown as its raw hex value rather than guessed at.
///
/// <c>*PMARPOffload</c>/<c>*WakeOnMagicPacket</c> are ordinary standardized NDIS keywords (unlike
/// PnPCapabilities, these ones are properly documented), read the same boolean-ish way #550's
/// on/off flags already interpret a keyword's value.
/// </summary>
public static class AdapterPowerManagementService
{
    // The specific documented "hide + force off" PnPCapabilities value - see the class remarks.
    private const int LockedOffBits = 0x18;

    public static AdapterPowerManagementInfo Read(string? adapterId)
    {
        string? raw = AdapterAdvancedPropertyService.ReadRawValue(adapterId, "PnPCapabilities");
        bool? mayPowerOff;
        string explanation;

        if (string.IsNullOrEmpty(raw))
        {
            mayPowerOff = null;
            explanation = "Not set - the driver leaves Windows' own default in effect, which usually still allows powering the device off. Check Device Manager directly to be sure.";
        }
        else if (TryParseIntFlexible(raw, out int value))
        {
            if (value == 0)
            {
                mayPowerOff = true;
                explanation = "Windows is allowed to power this device off to save power (the default) - the #1 cause of \"no internet after resuming from sleep\".";
            }
            else if ((value & LockedOffBits) == LockedOffBits)
            {
                mayPowerOff = false;
                explanation = "Windows is NOT permitted to power this device off - both power-management checkboxes are locked off by the driver.";
            }
            else
            {
                mayPowerOff = null;
                explanation = $"Non-default value (0x{value:X}) - its exact meaning is driver-specific. Check Device Manager directly to be sure.";
            }
        }
        else
        {
            mayPowerOff = null;
            explanation = $"Unrecognized value (\"{raw}\").";
        }

        bool? arpOffload = ParseBoolKeyword(adapterId, "*PMARPOffload");
        bool? wakeOnMagicPacket = ParseBoolKeyword(adapterId, "*WakeOnMagicPacket");

        return new AdapterPowerManagementInfo(raw, mayPowerOff, explanation, arpOffload, wakeOnMagicPacket);
    }

    private static bool? ParseBoolKeyword(string? adapterId, string keyword)
    {
        string? raw = AdapterAdvancedPropertyService.ReadRawValue(adapterId, keyword);
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw == "1") return true;
        if (raw == "0") return false;
        if (raw.Contains("enable", StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Contains("disable", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool TryParseIntFlexible(string raw, out int value)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(raw, out value);
    }
}
