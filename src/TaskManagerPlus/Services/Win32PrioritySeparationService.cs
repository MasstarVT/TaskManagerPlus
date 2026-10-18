using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #268: reads and decodes HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\
/// Win32PrioritySeparation - the classic "Programs" vs. "Background services" processor-scheduling
/// toggle (System Properties > Performance Options > Advanced) plus every other value it can hold.
/// The low six bits are three documented 2-bit fields (high to low): quantum length (1 = long,
/// 2 = short), quantum type (1 = variable, 2 = fixed), and foreground boost (0 = none, 1 = 2x,
/// 2 or 3 = 3x); 0 or 3 in the first two fields means "this edition's default" (short/variable on
/// client Windows, long/fixed on Server). Decoded from the bits rather than a lookup table - the
/// table this replaced mislabelled most entries (24, the "Background services" preset, read as
/// "short, variable" when it is long and fixed).
///
/// Reuses the existing PlatformLatencySettingRow shape so this appends onto
/// ResponsivenessViewModel.PlatformLatencySettings alongside the #220/#227/#232/#241 rows already
/// there.
/// </summary>
public static class Win32PrioritySeparationService
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ValueName = "Win32PrioritySeparation";

    // The two values Performance Options itself writes.
    private static readonly Dictionary<int, string> GuiPresets = new()
    {
        [0x26] = "\"Programs\" in Performance Options",
        [0x18] = "\"Background services\" in Performance Options",
    };

    public static PlatformLatencySettingRow Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            object? raw = key?.GetValue(ValueName);
            if (raw is null)
            {
                return new PlatformLatencySettingRow
                {
                    SettingName = "Win32PrioritySeparation",
                    ValueText = "Not set — Windows applies its built-in default",
                    Note = "Absent is normal for this value; Windows applies its built-in default when it's missing.",
                };
            }

            int value = Convert.ToInt32(raw);
            bool? isServer = ReadIsServerEdition();
            var (decoded, isShort, isVariable, boost) = Decode(value, isServer);

            string valueText = $"{value} (0x{value:X}) — {decoded}";
            if (GuiPresets.TryGetValue(value, out var preset)) valueText += $" ({preset})";

            bool matchesClientDefault = isShort == true && isVariable == true && boost >= 2;
            return new PlatformLatencySettingRow
            {
                SettingName = "Win32PrioritySeparation",
                ValueText = valueText,
                Note = matchesClientDefault
                    ? "Same scheduling behaviour as Windows' client default (short, variable quantum, 3x foreground boost)."
                    : "Differs from Windows' client default behaviour (short, variable quantum, 3x foreground boost) — often changed by a \"speed up gaming\" tweak guide, or by picking \"Background services\" in Performance Options" +
                      (isServer == true ? " (Server editions default to long, fixed quanta)." : "."),
            };
        }
        catch (Exception ex)
        {
            return new PlatformLatencySettingRow { SettingName = "Win32PrioritySeparation", ValueText = "Unknown", Note = $"Registry read failed: {ex.Message}" };
        }
    }

    /// <summary>Decodes the three 2-bit fields - see the class remarks. isShort/isVariable are
    /// null when a field says "edition default" and the edition couldn't be read.</summary>
    private static (string Text, bool? IsShort, bool? IsVariable, int Boost) Decode(int value, bool? isServer)
    {
        int length = (value >> 4) & 0x3;
        int type = (value >> 2) & 0x3;
        int boost = value & 0x3;

        bool? editionDefaultIsClientStyle = isServer is { } server ? !server : null;
        bool? isShort = length switch { 1 => false, 2 => true, _ => editionDefaultIsClientStyle };
        bool? isVariable = type switch { 1 => true, 2 => false, _ => editionDefaultIsClientStyle };
        bool lengthIsDefault = length is 0 or 3;
        bool typeIsDefault = type is 0 or 3;

        string lengthText = isShort switch { true => "Short", false => "Long", null => "Edition-default" }
            + (lengthIsDefault && isShort is not null ? " (edition default)" : string.Empty);
        string typeText = isVariable switch { true => "variable", false => "fixed", null => "edition-default" }
            + (typeIsDefault && isVariable is not null ? " (edition default)" : string.Empty);
        string boostText = boost switch { 0 => "no foreground boost", 1 => "2x foreground boost", _ => "3x foreground boost" };
        if (isVariable == false && boost > 0) boostText += " (has no effect with a fixed quantum)";

        string text = $"{lengthText}, {typeText} quantum — {boostText}";
        if ((value & ~0x3F) != 0) text += "; also sets bits outside the three documented fields";
        return (text, isShort, isVariable, boost);
    }

    /// <summary>ProductOptions\ProductType: "WinNT" is a client edition, "ServerNT"/"LanmanNT" a
    /// server one. Null when unreadable - the decode then says "edition default" without
    /// guessing which.</summary>
    private static bool? ReadIsServerEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            return (key?.GetValue("ProductType") as string) switch
            {
                { } t when t.Equals("WinNT", StringComparison.OrdinalIgnoreCase) => false,
                { } t when t.Equals("ServerNT", StringComparison.OrdinalIgnoreCase) || t.Equals("LanmanNT", StringComparison.OrdinalIgnoreCase) => true,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
