using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #246: probes the shell's own windows specifically - Shell_TrayWnd (the taskbar) and Progman (the
/// desktop) by class name via FindWindow, plus any open Explorer frame windows (found via the same
/// top-level-window enumeration HungWindowService.EnumerateRaw already does for #235, filtered down
/// to explorer.exe's own PID) - so "the taskbar/right-click menu is laggy" becomes a measured
/// number instead of a feeling. Reuses HungWindowService.ProbeWindowMs (#236's exact probe logic)
/// rather than re-deriving the SendMessageTimeout call, on its own slow cadence
/// (ResponsivenessViewModel drives the timer, matching #236's own cadence). Cross-links to
/// ShellExtensionService's existing registered-context-menu-handler count as a plain-text pointer -
/// a common cause of shell lag this doesn't otherwise measure directly.
/// </summary>
public static class ShellResponsivenessService
{
    public static List<ShellResponsivenessRow> Probe()
    {
        var rows = new List<ShellResponsivenessRow>();

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
            rows.Add(new ShellResponsivenessRow { WindowName = "Taskbar (Shell_TrayWnd)", ResponseMs = HungWindowService.ProbeWindowMs(tray) });

        IntPtr desktop = FindWindow("Progman", null);
        if (desktop != IntPtr.Zero)
            rows.Add(new ShellResponsivenessRow { WindowName = "Desktop (Progman)", ResponseMs = HungWindowService.ProbeWindowMs(desktop) });

        try
        {
            var explorerFrames = HungWindowService.EnumerateRaw()
                .Where(w => w.Visible && !string.IsNullOrEmpty(w.Title))
                .Where(w => IsExplorerPid(w.Pid))
                .ToList();

            foreach (var frame in explorerFrames)
            {
                rows.Add(new ShellResponsivenessRow
                {
                    WindowName = $"Explorer window: {frame.Title}",
                    ResponseMs = HungWindowService.ProbeWindowMs(frame.Hwnd),
                });
            }
        }
        catch
        {
            // Best-effort - the taskbar/desktop rows above still stand even if the Explorer-frame
            // pass fails for some reason.
        }

        return rows;
    }

    /// <summary>Plain count/text pointer to the existing shell-extension list (#20) - a common cause
    /// of shell lag this probe doesn't otherwise measure, per the task's own "no need for deep new
    /// cross-tab plumbing" framing.</summary>
    public static string ShellExtensionNote()
    {
        try
        {
            // List() covers every handler category, Windows' own included. Only a named
            // non-Microsoft publisher counts as third-party: Windows' own handler DLLs are
            // catalog-signed, which carries no readable publisher ("Unknown (catalog-signed)").
            var extensions = ShellExtensionService.List();
            int thirdParty = 0, unknown = 0;
            foreach (var e in extensions)
            {
                string publisher = e.Publisher ?? string.Empty;
                if (publisher.Length == 0 || publisher.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) unknown++;
                else if (!publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) thirdParty++;
            }
            return extensions.Count == 0
                ? "No registered shell extension handlers found."
                : $"{extensions.Count} shell extension handler(s) registered: {thirdParty} from a named non-Microsoft publisher, {unknown} with no readable publisher (Windows' own catalog-signed handlers are among these). Third-party handlers are a common cause of shell lag.";
        }
        catch (Exception ex)
        {
            return $"Couldn't list shell extensions: {ex.Message}";
        }
    }

    private static bool IsExplorerPid(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
}
