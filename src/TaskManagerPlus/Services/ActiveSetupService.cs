using System.Globalization;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #743: Active Setup component inventory. HKLM\SOFTWARE\Microsoft\Active Setup\Installed
/// Components\&lt;GUID&gt; (and its Wow6432Node twin, for 32-bit-registered components) holds a
/// StubPath command that Windows runs once per user - specifically, the first time that user signs
/// in after the component's HKLM Version string changes. Windows tracks "have I already run this
/// for this user" by mirroring the same Version value into
/// HKCU\Software\Microsoft\Active Setup\Installed Components\&lt;same GUID&gt;\Version once the
/// stub has run, so comparing the two copies is exactly what Windows itself does before deciding
/// whether to run a component's stub again - not a guess at Windows' internal logic.
///
/// Registry-only reads (a handful of subkeys, typically well under a hundred), cheap enough to run
/// on every Refresh rather than needing its own "Load" button, unlike Scheduled Tasks/browser
/// extensions on this same tab.
/// </summary>
public static class ActiveSetupService
{
    private const string HklmPath = @"SOFTWARE\Microsoft\Active Setup\Installed Components";
    private const string Wow6432Path = @"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components";
    private const string HkcuPath = @"Software\Microsoft\Active Setup\Installed Components";

    public static List<ActiveSetupComponent> List()
    {
        var result = new List<ActiveSetupComponent>();
        ReadFrom(result, HklmPath, isWow6432: false);
        ReadFrom(result, Wow6432Path, isWow6432: true);
        return result.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadFrom(List<ActiveSetupComponent> result, string basePath, bool isWow6432)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
            if (baseKey is null) return; // absent entirely on many 64-bit-only installs for the Wow6432Node view - not an error

            foreach (var name in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var compKey = baseKey.OpenSubKey(name);
                    if (compKey is null) continue;

                    var stubPath = compKey.GetValue("StubPath") as string ?? string.Empty;
                    if (stubPath.Length == 0) continue; // nothing to run - not a real autorun entry

                    var displayName = compKey.GetValue(null) as string; // the key's default value often holds a friendly name
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = name;

                    var hklmVersion = compKey.GetValue("Version") as string ?? string.Empty;
                    // IsInstalled is a DWORD on most components but REG_SZ "0"/"1" on some.
                    bool isDisabled = compKey.GetValue("IsInstalled") switch
                    {
                        int flag => flag == 0,
                        string str => str.Trim() == "0",
                        _ => false,
                    };
                    var hkcuVersion = ReadHkcuVersion(name);

                    string state = isDisabled
                        ? "Disabled (IsInstalled=0)"
                        : hklmVersion.Length == 0
                            ? "Unknown (no Version value)"
                            : IsVersionLower(hkcuVersion, hklmVersion)
                                ? "Pending - will run at this user's next sign-in"
                                : "Complete";

                    result.Add(new ActiveSetupComponent
                    {
                        ComponentKeyName = name,
                        DisplayName = displayName!,
                        StubPath = stubPath,
                        HklmVersion = hklmVersion.Length == 0 ? "Unknown" : hklmVersion,
                        HkcuVersion = hkcuVersion.Length == 0 ? "(never run for this user)" : hkcuVersion,
                        State = state,
                        IsWow6432 = isWow6432,
                    });
                }
                catch { /* per-component - skip and continue */ }
            }
        }
        catch
        {
            // Key inaccessible - degrade to no rows from this hive/view rather than fabricating one.
        }
    }

    private static string ReadHkcuVersion(string componentName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{HkcuPath}\{componentName}");
            return key?.GetValue("Version") as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>Windows re-runs a stub only while the HKCU version is missing or LOWER than
    /// HKLM's - an equal or newer HKCU copy (or a different separator, "1.2" vs "1,2") counts as
    /// done. Versions are comma-separated numbers; an unparseable pair falls back to "differs".
    /// Same comparison as AutorunsService's Active Setup check.</summary>
    private static bool IsVersionLower(string hkcuVersion, string hklmVersion)
    {
        if (string.IsNullOrWhiteSpace(hkcuVersion)) return true;
        static int[]? Parse(string v)
        {
            var parts = v.Split(new[] { ',', '.' }, StringSplitOptions.TrimEntries);
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i])) return null;
            return nums;
        }
        var cu = Parse(hkcuVersion);
        var lm = Parse(hklmVersion);
        if (cu is null || lm is null) return !hkcuVersion.Equals(hklmVersion, StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Max(cu.Length, lm.Length); i++)
        {
            int a = i < cu.Length ? cu[i] : 0, b = i < lm.Length ? lm[i] : 0;
            if (a != b) return a < b;
        }
        return false;
    }
}
