using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// #209: reads the current foreground window's process name and title, via the documented
/// GetForegroundWindow/GetWindowThreadProcessId Win32 pair - there's no WMI/tool equivalent for
/// "what's focused right now", so this is one of the small set of raw-interop exceptions to the
/// "prefer a known tool" rule (same tier as CpuTopologyService/HandleInspectionService). Used to
/// stamp DPC/ISR spikes with "what was the user looking at" context (DpcLatencyService) - a
/// correlation, not proof the foreground app caused the spike ("quick flag, not a verdict").
/// </summary>
public static class ForegroundContextService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static (string ProcessName, string WindowTitle) GetForegroundContext()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (string.Empty, string.Empty);

            GetWindowThreadProcessId(hwnd, out int pid);
            string processName = string.Empty;
            try
            {
                using var p = Process.GetProcessById(pid);
                processName = p.ProcessName;
            }
            catch
            {
                // process exited between the two calls, or access denied - leave blank
            }

            int len = GetWindowTextLength(hwnd);
            string title = string.Empty;
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            return (processName, title);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>#267: just the foreground window's owning process ID, 0 when there's none/it
    /// couldn't be determined - a separate method (not folded into GetForegroundContext's tuple)
    /// so that method's existing two-element deconstruction at its one call site doesn't need to
    /// change.</summary>
    public static int GetForegroundProcessId()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out int pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }
}
