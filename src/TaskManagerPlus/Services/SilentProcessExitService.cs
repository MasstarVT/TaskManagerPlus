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

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearConfig(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(KeyPathFor(exeName), throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
