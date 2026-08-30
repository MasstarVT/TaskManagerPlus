using System.Diagnostics;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #745: Image File Execution Options hijack audit. HKLM\SOFTWARE\Microsoft\Windows NT\
/// CurrentVersion\Image File Execution Options\&lt;exeName&gt; is meant for per-executable
/// compatibility settings, but a Debugger value there redirects every launch of that executable to
/// run the debugger command instead (with the original exe passed as an argument) - the classic
/// "sticky keys backdoor" technique (replacing sethc.exe's Debugger with cmd.exe) as well as a
/// legitimate way real debuggers and compatibility shims attach. The MonitorProcess hook is a
/// separate, less well-known one: it launches MonitorProcess whenever the named executable exits,
/// silently, with no user-visible prompt. Windows stores it under its own
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\&lt;exeName&gt; tree - only
/// the enabling GlobalFlag bit (0x200, FLG_MONITOR_SILENT_PROCESS_EXIT) lives under IFEO itself.
///
/// Quick flag, not a verdict (see CLAUDE.md's cross-cutting conventions) - this only lists
/// subkeys that actually carry a Debugger, a nonzero GlobalFlag, or a SilentProcessExit monitor;
/// the many IFEO subkeys that hold only unrelated per-image settings (MaxRequestThreads,
/// DisableHeapLookaside, ...) produce no row at all.
/// </summary>
public static class ImageFileExecutionOptionsService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string SilentProcessExitPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";

    public static List<ImageFileExecutionOptionsEntry> Read()
    {
        var result = new List<ImageFileExecutionOptionsEntry>();
        var silentMonitors = ReadSilentProcessExitMonitors();
        var listedExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(IfeoPath);
            if (root is not null)
            {
                foreach (var exeName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var exeKey = root.OpenSubKey(exeName);
                        if (exeKey is null) continue;

                        string? debugger = exeKey.GetValue("Debugger") as string;
                        int? globalFlag = exeKey.GetValue("GlobalFlag") as int?;
                        silentMonitors.TryGetValue(exeName, out string? monitorProcess);

                        bool hasDebugger = !string.IsNullOrEmpty(debugger);
                        bool hasGlobalFlag = globalFlag is not (null or 0);
                        bool hasMonitor = !string.IsNullOrEmpty(monitorProcess);
                        if (!hasDebugger && !hasGlobalFlag && !hasMonitor) continue;

                        listedExeNames.Add(exeName);
                        result.Add(new ImageFileExecutionOptionsEntry
                        {
                            ExecutableName = exeName,
                            Debugger = debugger,
                            GlobalFlagHex = hasGlobalFlag ? $"0x{globalFlag:X}" : null,
                            MonitorProcess = monitorProcess,
                            IsCurrentlyRunning = IsProcessRunning(exeName),
                        });
                    }
                    catch { /* per-entry - skip and continue */ }
                }
            }
        }
        catch
        {
            // Key inaccessible - degrade to no rows rather than fabricating a result.
        }

        // A SilentProcessExit monitor configured with no IFEO subkey for the exe at all (or one
        // this scan couldn't read) still deserves a row - it's the T1546.012 payload itself.
        foreach (var (exeName, monitor) in silentMonitors)
        {
            if (listedExeNames.Contains(exeName)) continue;
            result.Add(new ImageFileExecutionOptionsEntry
            {
                ExecutableName = exeName,
                Debugger = null,
                GlobalFlagHex = null,
                MonitorProcess = monitor,
                IsCurrentlyRunning = IsProcessRunning(exeName),
            });
        }

        return result.OrderBy(e => e.ExecutableName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads the whole SilentProcessExit tree once: exe name -> its MonitorProcess
    /// command. This is the location Windows actually honors for the exit hook; a
    /// "SilentProcessExit" subkey under IFEO\&lt;exe&gt; itself is inert.</summary>
    private static Dictionary<string, string> ReadSilentProcessExitMonitors()
    {
        var monitors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(SilentProcessExitPath);
            if (root is null) return monitors;

            foreach (var exeName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(exeName);
                    if (key?.GetValue("MonitorProcess") is string monitor && monitor.Length > 0)
                        monitors[exeName] = monitor;
                }
                catch { /* per-entry - skip and continue */ }
            }
        }
        catch
        {
            // Key inaccessible - degrade to none rather than fabricating a result.
        }
        return monitors;
    }

    /// <summary>#745: whether a process matching this exe name is currently running - gates the
    /// Startup tab's "View in Processes" cross-link, reusing the #708/#723 SelectTabByName pattern
    /// (see StartupView.xaml.cs).</summary>
    private static bool IsProcessRunning(string exeName)
    {
        string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(exeName);
        if (nameNoExt.Length == 0) return false;

        Process[] procs;
        try { procs = Process.GetProcessesByName(nameNoExt); }
        catch { return false; }

        bool any = procs.Length > 0;
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { /* best-effort */ }
        }
        return any;
    }
}
