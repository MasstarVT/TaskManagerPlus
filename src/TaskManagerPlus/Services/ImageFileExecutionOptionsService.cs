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
/// legitimate way real debuggers and compatibility shims attach. SilentProcessExit\MonitorProcess
/// under the same subkey is a separate, less well-known hook: it launches MonitorProcess whenever
/// the named executable exits, silently, with no user-visible prompt.
///
/// Quick flag, not a verdict (see CLAUDE.md's cross-cutting conventions) - this only lists
/// subkeys that actually carry a Debugger, a nonzero GlobalFlag, or a SilentProcessExit monitor;
/// the many IFEO subkeys that hold only unrelated per-image settings (MaxRequestThreads,
/// DisableHeapLookaside, ...) produce no row at all.
/// </summary>
public static class ImageFileExecutionOptionsService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public static List<ImageFileExecutionOptionsEntry> Read()
    {
        var result = new List<ImageFileExecutionOptionsEntry>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(IfeoPath);
            if (root is null) return result;

            foreach (var exeName in root.GetSubKeyNames())
            {
                try
                {
                    using var exeKey = root.OpenSubKey(exeName);
                    if (exeKey is null) continue;

                    string? debugger = exeKey.GetValue("Debugger") as string;
                    int? globalFlag = exeKey.GetValue("GlobalFlag") as int?;

                    string? monitorProcess = null;
                    using (var silentKey = exeKey.OpenSubKey("SilentProcessExit"))
                        monitorProcess = silentKey?.GetValue("MonitorProcess") as string;

                    bool hasDebugger = !string.IsNullOrEmpty(debugger);
                    bool hasGlobalFlag = globalFlag is not (null or 0);
                    bool hasMonitor = !string.IsNullOrEmpty(monitorProcess);
                    if (!hasDebugger && !hasGlobalFlag && !hasMonitor) continue;

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
        catch
        {
            // Key inaccessible - degrade to no rows rather than fabricating a result.
        }
        return result.OrderBy(e => e.ExecutableName, StringComparer.OrdinalIgnoreCase).ToList();
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
