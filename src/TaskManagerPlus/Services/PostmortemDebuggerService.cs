using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 17, item 62: reads HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug (and its
/// Wow6432Node copy) plus every Image File Execution Options subkey's Debugger value - covers both
/// "is a broken/unexpected postmortem-debugger config the reason crash dialogs look wrong" and "is
/// something using IFEO to hijack process launches", a classic malware-persistence trick. Read-
/// only, matching PostmortemDebuggerInfo's own remarks on why this app doesn't offer a one-click
/// write action here the way LocalDumps/SilentProcessExit do.
/// </summary>
public static class PostmortemDebuggerService
{
    private const string AeDebugKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug";
    private const string AeDebugWow64KeyPath = @"SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\AeDebug";
    private const string ImageFileExecutionOptionsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public static PostmortemDebuggerInfo Read()
    {
        var (debugger, auto) = ReadAeDebug(AeDebugKeyPath);
        var (wow64Debugger, wow64Auto) = ReadAeDebug(AeDebugWow64KeyPath);
        var hijacks = ReadImageFileExecutionOptionsHijacks();

        bool nonDefault =
            !LooksLikeDefaultDebugger(debugger) ||
            !LooksLikeDefaultDebugger(wow64Debugger) ||
            hijacks.Count > 0;

        return new PostmortemDebuggerInfo
        {
            Debugger = debugger,
            Auto = auto,
            Wow64Debugger = wow64Debugger,
            Wow64Auto = wow64Auto,
            ImageFileExecutionOptionsHijacks = hijacks,
            HasNonDefaultEntries = nonDefault,
        };
    }

    private static (string? Debugger, string? Auto) ReadAeDebug(string keyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return (null, null);
            string? debugger = key.GetValue("Debugger") as string;
            string? auto = key.GetValue("Auto")?.ToString();
            return (string.IsNullOrWhiteSpace(debugger) ? null : debugger, string.IsNullOrWhiteSpace(auto) ? null : auto);
        }
        catch
        {
            // Key not present (a 32-bit build of Windows won't have the Wow6432Node copy at
            // all) or access denied - "not set", not an error.
            return (null, null);
        }
    }

    /// <summary>Windows' own default postmortem debugger is WerFault.exe (with a "-pr %ld -e %ld"
    /// or similar command line); a common, entirely legitimate alternative is Visual Studio's
    /// vsjitdebugger.exe. Anything else - or the value being unset entirely, which on most modern
    /// Windows installs means postmortem debugging was actively disabled - is flagged as worth a
    /// manual check, not confirmed malicious ("quick flag, not a verdict" per CLAUDE.md).</summary>
    private static bool LooksLikeDefaultDebugger(string? debugger)
    {
        if (string.IsNullOrEmpty(debugger)) return true; // "not set" isn't itself a red flag
        return debugger.Contains("WerFault.exe", StringComparison.OrdinalIgnoreCase) ||
               debugger.Contains("vsjitdebugger.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Windows ships no Image File Execution Options Debugger values by default - every
    /// subkey under this key that has one is inherently worth surfacing, whether it's forgotten
    /// developer tooling or an actual hijack.</summary>
    private static List<ImageFileExecutionOptionsHijack> ReadImageFileExecutionOptionsHijacks()
    {
        var result = new List<ImageFileExecutionOptionsHijack>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ImageFileExecutionOptionsKeyPath);
            if (key is null) return result;

            foreach (var exeName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(exeName);
                    if (sub?.GetValue("Debugger") is string debugger && !string.IsNullOrWhiteSpace(debugger))
                        result.Add(new ImageFileExecutionOptionsHijack { ExecutableName = exeName, DebuggerPath = debugger });
                }
                catch { /* one bad subkey shouldn't stop the rest of the scan */ }
            }
        }
        catch
        {
            // Key unavailable/access denied - degrade to "no IFEO hijacks found".
        }
        return result.OrderBy(h => h.ExecutableName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
