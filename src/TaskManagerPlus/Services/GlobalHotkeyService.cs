using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #86: registers one system-wide hotkey (RegisterHotKey/UnregisterHotKey, user32.dll,
/// via a HwndSource message hook) that brings the main window to the front from anywhere - the
/// same "one keystroke, whatever app has focus" convenience Ctrl+Shift+Esc gives the real Task
/// Manager. Deliberately does NOT bind the literal Ctrl+Shift+Esc combination: that keystroke is
/// intercepted by the shell itself (to launch the real Task Manager) before it ever reaches this
/// app's message loop, so claiming it here would be a no-op at best and would fight the real Task
/// Manager's own binding at worst. Ctrl+Alt+T ("Task Manager Plus") is the default instead - same
/// spirit, a combination that's actually deliverable to a background WPF window.
///
/// Same native-interop risk tier as CpuTopologyService/NetworkConnectionsService's own P/Invoke -
/// registration can fail (another app already owns the combination), which degrades to
/// <see cref="IsRegistered"/> staying false rather than a crash; every call is wrapped
/// accordingly, matching CLAUDE.md's "graceful degradation" convention for this whole app.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x3A17; // arbitrary 16-bit id, scoped to this process/window only

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_T = 0x54;

    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>True only if the OS actually granted this app the Ctrl+Alt+T binding - false
    /// (silently - see the class remarks) when another running app already owns it.</summary>
    public bool IsRegistered { get; private set; }

    public event Action? Pressed;

    /// <summary>Call once the window's handle exists (e.g. from SourceInitialized).</summary>
    public void Register(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        try
        {
            IsRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, VK_T);
        }
        catch
        {
            IsRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
            _source?.RemoveHook(WndProc);
        }
        catch { /* best-effort */ }
    }
}
