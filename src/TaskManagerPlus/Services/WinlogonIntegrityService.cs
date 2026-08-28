using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #744: Winlogon shell chain integrity check. HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\
/// Winlogon's Shell/Userinit/Taskman/AppSetup/GinaDLL values control what actually launches at
/// sign-in, underneath even the Run keys/Startup folders this tab already inventories - replacing
/// Shell or Userinit is a classic, low-level persistence technique. Compared against Windows'
/// documented defaults (Shell = explorer.exe, Userinit = the userinit.exe path with its trailing
/// comma - Windows appends further comma-separated commands after that path, so the trailing comma
/// is part of the correct value, not a typo). Taskman/AppSetup/GinaDLL have no meaningful "expected
/// string" - Windows simply doesn't set them on a clean install, so their being present at all is
/// itself the flag (GinaDLL in particular predates the Vista+ Credential Provider model entirely;
/// a modern system with it set is unusual enough to always flag).
///
/// Quick flag, not a verdict (see CLAUDE.md's cross-cutting conventions) - HKCU overrides in
/// particular have legitimate uses (a kiosk/shell-replacement machine sets a per-user Shell on
/// purpose), so this reports the comparison, not an accusation.
/// </summary>
public static class WinlogonIntegrityService
{
    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static readonly (string ValueName, string? ExpectedValue, string ExpectedDescription)[] CheckedValues =
    {
        ("Shell", "explorer.exe", "explorer.exe"),
        ("Userinit", @"C:\Windows\system32\userinit.exe,", @"C:\Windows\system32\userinit.exe, (trailing comma is correct - Windows appends to it)"),
        ("Taskman", null, "(not set - Windows uses its own default Task Manager launcher)"),
        ("AppSetup", null, "(not set)"),
        ("GinaDLL", null, "(not set - GINA was replaced by the Credential Provider model in Vista+; a value here on a modern system is unusual)"),
    };

    public static List<WinlogonCheckEntry> Read()
    {
        var result = new List<WinlogonCheckEntry>();
        ReadScope(result, Registry.LocalMachine, "HKLM (system-wide)", isHkcu: false);
        ReadScope(result, Registry.CurrentUser, "HKCU (current user override)", isHkcu: true);
        return result;
    }

    private static void ReadScope(List<WinlogonCheckEntry> result, RegistryKey hive, string scopeLabel, bool isHkcu)
    {
        try
        {
            using var key = hive.OpenSubKey(WinlogonPath);

            foreach (var (valueName, expected, expectedDescription) in CheckedValues)
            {
                string? actual = key?.GetValue(valueName) as string;

                // HKCU essentially never carries anything except an intentional Shell override -
                // skip the Taskman/AppSetup/GinaDLL rows there when absent, so the HKCU section
                // doesn't just repeat four "not set" rows that only mean something at HKLM scope.
                if (isHkcu && actual is null && valueName != "Shell") continue;

                bool isMismatch = expected is not null
                    ? !string.Equals(actual ?? string.Empty, expected, StringComparison.OrdinalIgnoreCase)
                    : actual is not null; // any value at all where none is expected is itself the flag

                result.Add(new WinlogonCheckEntry
                {
                    Scope = scopeLabel,
                    ValueName = valueName,
                    ExpectedText = expectedDescription,
                    ActualText = actual ?? "(not set)",
                    IsMismatch = isMismatch,
                });
            }
        }
        catch
        {
            // Key inaccessible - degrade to no rows for this scope rather than fabricating a result.
        }
    }
}
