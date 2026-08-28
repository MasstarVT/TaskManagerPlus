using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// #245: parses HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\SubSystems\Windows's
/// SharedSection value - a string like "1024,20480,768" where the 2nd/3rd numbers are the
/// interactive/noninteractive desktop heap sizes in KB (the 1st is the shared/system-wide heap, not
/// per-desktop). Desktop-heap exhaustion presents as "windows stop drawing / nothing opens" rather
/// than high CPU, and unlike the session-wide USER/GDI handle totals (summed by
/// ResponsivenessViewModel from the process list ProcessesViewModel already polls -
/// ProcessRow.GdiHandleCount/UserHandleCount), Windows exposes no live "how much of the desktop heap
/// is actually used" counter - only the configured size, which is what this reads.
/// </summary>
public static class DesktopHeapService
{
    private const string SubKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\SubSystems";
    private const string ValueName = "Windows";
    private static readonly Regex SharedSectionRegex = new(@"SharedSection=(\d+),(\d+),(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (int? InteractiveKb, int? NoninteractiveKb, string StatusText) ReadHeapSizes()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SubKeyPath);
            string? raw = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(raw))
                return (null, null, "Couldn't read the SubSystems\\Windows registry value.");

            var m = SharedSectionRegex.Match(raw);
            if (!m.Success)
                return (null, null, "SharedSection value present but didn't match the expected 3-number format.");

            int interactive = int.Parse(m.Groups[2].Value);
            int noninteractive = int.Parse(m.Groups[3].Value);
            return (interactive, noninteractive,
                $"Interactive desktop heap: {interactive:N0} KB · Noninteractive: {noninteractive:N0} KB (Windows default is 3072/768 KB on most modern builds).");
        }
        catch (Exception ex)
        {
            return (null, null, $"Read failed: {ex.Message}");
        }
    }
}
