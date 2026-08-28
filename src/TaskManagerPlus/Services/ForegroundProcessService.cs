using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #677: "is a foreground app running" for the pre-TDR hang detector - GetForegroundWindow/
/// GetWindowThreadProcessId have no WMI/tool equivalent (they're a live desktop-session query, not
/// system state), so this is the raw-P/Invoke "no other option" case CLAUDE.md carves out for
/// CpuTopologyService/NetworkConnectionsService/etc. Read-only, can't fail dangerously - worst case
/// is returning 0 (no known foreground process), never a hang or thrown exception.
/// </summary>
public static class ForegroundProcessService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>The PID owning the current foreground window, or 0 if none could be determined
    /// (no window has focus, or the call itself failed).</summary>
    public static int GetForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }
}
