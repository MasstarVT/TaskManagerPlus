using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 17, item 61: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\&lt;exe&gt;
/// read/write - makes Windows capture a dump (or launch a monitor process) when a process
/// disappears WITHOUT raising an error, the gap neither WER nor LocalDumps (item 42,
/// WerReportService) cover, since both only ever trigger from an actual reported fault. Same
/// registry-config-editor shape as WerReportService's LocalDumps methods (read/write/clear, needs
/// this app's own elevation - see CLAUDE.md - like every other registry write in this app).
/// </summary>
public static class SilentProcessExitService
{
    private const string RootKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";

    private static string KeyPathFor(string exeName) => $@"{RootKeyPath}\{exeName.Trim()}";

    /// <summary>Unlike LocalDumps, SilentProcessExit has no meaningful "global default" - it's
    /// keyed by executable name only, so a blank/whitespace target isn't a valid configuration to
    /// read or write (there's no bare SilentProcessExit\&lt;value&gt; shape to fall back to).</summary>
    public static SilentProcessExitConfig ReadConfig(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return new SilentProcessExitConfig { TargetExecutable = exeName, Exists = false };

        string path = KeyPathFor(exeName);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return new SilentProcessExitConfig { TargetExecutable = exeName, Exists = false };

            return new SilentProcessExitConfig
            {
                TargetExecutable = exeName,
                Exists = true,
                ReportingMode = key.GetValue("ReportingMode") is { } rm ? Convert.ToInt32(rm) : null,
                LocalDumpFolder = key.GetValue("LocalDumpFolder") as string,
                MonitorProcess = key.GetValue("MonitorProcess") as string,
            };
        }
        catch
        {
            return new SilentProcessExitConfig { TargetExecutable = exeName, Exists = false };
        }
    }

    public static bool WriteConfig(string exeName, int reportingMode, string? localDumpFolder, string? monitorProcess)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPathFor(exeName), writable: true);
            if (key is null) return false;

            key.SetValue("ReportingMode", reportingMode, RegistryValueKind.DWord);
            if (!string.IsNullOrWhiteSpace(localDumpFolder))
                key.SetValue("LocalDumpFolder", localDumpFolder, RegistryValueKind.ExpandString);
            else
                key.DeleteValue("LocalDumpFolder", throwOnMissingValue: false);

            if (!string.IsNullOrWhiteSpace(monitorProcess))
                key.SetValue("MonitorProcess", monitorProcess, RegistryValueKind.ExpandString);
            else
                key.DeleteValue("MonitorProcess", throwOnMissingValue: false);
        }
        catch
        {
            return false;
        }

        // Windows ignores the SilentProcessExit key unless the image's IFEO GlobalFlag also
        // carries FLG_MONITOR_SILENT_PROCESS_EXIT - what gflags' "Enable silent process exit
        // monitoring" checkbox sets.
        return UpdateIfeoMonitorFlag(exeName, set: true);
    }

    public static bool ClearConfig(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(KeyPathFor(exeName), throwOnMissingSubKey: false);
        }
        catch
        {
            return false;
        }
        return UpdateIfeoMonitorFlag(exeName, set: false);
    }

    private const string IfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const int FlgMonitorSilentProcessExit = 0x200;

    /// <summary>Sets or clears only the 0x200 bit of IFEO\&lt;exe&gt;\GlobalFlag, leaving every
    /// other gflags bit as it was. GlobalFlag is normally a DWORD, but gflags-era tools also wrote
    /// it as a "0x..." string - the existing kind is kept. Clearing never creates the IFEO key, and
    /// removes the value only when this bit was the last one set.</summary>
    private static bool UpdateIfeoMonitorFlag(string exeName, bool set)
    {
        string path = $@"{IfeoKeyPath}\{exeName.Trim()}";
        try
        {
            using var key = set
                ? Registry.LocalMachine.CreateSubKey(path, writable: true)
                : Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null) return !set;

            object? raw = key.GetValue("GlobalFlag");
            int current;
            if (raw is null) current = 0;
            else if (raw is int i) current = i;
            else if (raw is string s && TryParseFlag(s, out var parsed)) current = parsed;
            else return false; // unexpected type/text - don't overwrite other gflags bits with a guess

            int updated = set ? current | FlgMonitorSilentProcessExit : current & ~FlgMonitorSilentProcessExit;
            if (raw is not null && updated == current) return true;

            if (updated == 0)
                key.DeleteValue("GlobalFlag", throwOnMissingValue: false);
            else if (raw is string)
                key.SetValue("GlobalFlag", $"0x{updated:X8}", RegistryValueKind.String);
            else
                key.SetValue("GlobalFlag", updated, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseFlag(string text, out int value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        return int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
