using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #268: reads and decodes HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\
/// Win32PrioritySeparation - the classic "Programs" vs. "Background services" processor-scheduling
/// toggle (System Properties > Performance Options > Advanced) plus every other value it can hold.
/// The exact bitfield semantics for every possible value aren't cleanly documented by Microsoft
/// (community references disagree on some of the less-common combinations), so per this app's
/// "never fabricate" rule this decodes leniently against a small lookup table of the values that
/// are well-documented/commonly cited (the two GUI-selectable presets plus the out-of-box default),
/// falling back to a plain "custom/undocumented value" label for anything else rather than deriving
/// novel bit math this app can't verify.
///
/// Reuses the existing PlatformLatencySettingRow shape so this appends onto
/// ResponsivenessViewModel.PlatformLatencySettings alongside the #220/#227/#232/#241 rows already
/// there.
/// </summary>
public static class Win32PrioritySeparationService
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ValueName = "Win32PrioritySeparation";

    // The well-documented/commonly-cited standard values - not an exhaustive bitfield decode (see
    // class remarks). 2 = the "Adjust for best performance of: Programs" GUI preset, 24 = the
    // "...Background services" GUI preset, 38 = the value a fresh desktop Windows install actually
    // ships with.
    private static readonly Dictionary<int, string> KnownValues = new()
    {
        [0] = "Short, fixed quantum — no foreground boost",
        [1] = "Short, fixed quantum — 2x foreground boost",
        [2] = "Short, fixed quantum — 3x foreground boost (\"Programs\" preset)",
        [4] = "Long, fixed quantum — no foreground boost",
        [5] = "Long, fixed quantum — 2x foreground boost",
        [6] = "Long, fixed quantum — 3x foreground boost",
        [8] = "Short, variable quantum — no foreground boost",
        [9] = "Short, variable quantum — 2x foreground boost",
        [10] = "Short, variable quantum — 3x foreground boost",
        [18] = "Long, variable quantum — 3x foreground boost",
        [24] = "Short, variable quantum — no foreground boost (\"Background services\" preset)",
        [26] = "Short, variable quantum — 3x foreground boost",
        [38] = "Short, variable quantum — 3x foreground boost (Windows' out-of-box default)",
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
                    ValueText = "Not set — Windows default (38: short variable quantum, 3x foreground boost)",
                    Note = "Absent is normal for this value; Windows applies its built-in default when it's missing.",
                };
            }

            int value = Convert.ToInt32(raw);
            bool known = KnownValues.TryGetValue(value, out var decoded);
            string valueText = known
                ? $"{value} — {decoded}"
                : $"{value} — custom/undocumented value (not one of the standard documented combinations)";

            return new PlatformLatencySettingRow
            {
                SettingName = "Win32PrioritySeparation",
                ValueText = valueText,
                Note = value == 38
                    ? "Matches Windows' out-of-box default."
                    : "Differs from Windows' out-of-box default (38) — often changed by a \"speed up gaming\" tweak guide, or by picking \"Background services\" in Performance Options.",
            };
        }
        catch (Exception ex)
        {
            return new PlatformLatencySettingRow { SettingName = "Win32PrioritySeparation", ValueText = "Unknown", Note = $"Registry read failed: {ex.Message}" };
        }
    }
}
