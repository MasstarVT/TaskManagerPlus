using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// Item 70: HKLM registry toggles for Windows' two built-in "make a hard-hung machine crash on
/// purpose so it leaves a dump" mechanisms:
///  - CrashOnCtrlScroll: Ctrl+ScrollLock x2 forces a bugcheck (0xE2, MANUALLY_INITIATED_CRASH).
///    Set under BOTH the kbdhid and i8042prt driver Parameters keys, since which driver is
///    actually in use depends on whether the keyboard is USB or PS/2 and this app has no reliable
///    way to tell which - Microsoft's own KB244139 instructs setting both for exactly this reason.
///  - NMICrashDump: makes Windows bugcheck (instead of just logging it) when it receives a
///    hardware NMI signal (e.g. a server's NMI button) - a separate mechanism, its own single
///    registry value under CrashControl.
/// Both are otherwise-undiscoverable registry flags with a real, deliberate consequence (they
/// bluescreen the machine on demand); this service only ever flips the DWORD - the UI is
/// responsible for the explicit warning/confirmation before calling either Set method.
/// </summary>
public static class ForcedCrashService
{
    private const string KbdhidParametersPath = @"SYSTEM\CurrentControlSet\Services\kbdhid\Parameters";
    private const string I8042PrtParametersPath = @"SYSTEM\CurrentControlSet\Services\i8042prt\Parameters";
    private const string CrashControlPath = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string CrashOnCtrlScrollValue = "CrashOnCtrlScroll";
    private const string NmiCrashDumpValue = "NMICrashDump";

    /// <summary>Current CrashOnCtrlScroll state under each driver's own Parameters key - kept as
    /// a pair rather than collapsed into one bool, since a mismatch between the two (set under one
    /// driver but not the other) is itself meaningful and worth surfacing rather than hiding.
    /// Null means "not set" (Windows' own default - off - applies), per CLAUDE.md's "degrade to
    /// Unknown, never fabricate".</summary>
    public static (bool? KbdhidEnabled, bool? I8042Enabled) ReadCrashOnCtrlScrollStatus() =>
        (ReadDwordAsBool(KbdhidParametersPath, CrashOnCtrlScrollValue),
         ReadDwordAsBool(I8042PrtParametersPath, CrashOnCtrlScrollValue));

    public static bool? ReadNmiCrashDumpEnabled() => ReadDwordAsBool(CrashControlPath, NmiCrashDumpValue);

    /// <summary>Writes CrashOnCtrlScroll under both driver keys - returns true only when both
    /// writes succeeded (a partial write is reported as a failure so the UI doesn't claim success
    /// for a configuration that may not actually work on this machine's keyboard).</summary>
    public static bool SetCrashOnCtrlScroll(bool enable)
    {
        bool okKbdhid = WriteDword(KbdhidParametersPath, CrashOnCtrlScrollValue, enable ? 1 : 0);
        bool okI8042 = WriteDword(I8042PrtParametersPath, CrashOnCtrlScrollValue, enable ? 1 : 0);
        return okKbdhid && okI8042;
    }

    public static bool SetNmiCrashDump(bool enable) => WriteDword(CrashControlPath, NmiCrashDumpValue, enable ? 1 : 0);

    private static bool? ReadDwordAsBool(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue(valueName) is { } v) return Convert.ToInt32(v) != 0;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteDword(string path, string valueName, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(valueName, value, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
