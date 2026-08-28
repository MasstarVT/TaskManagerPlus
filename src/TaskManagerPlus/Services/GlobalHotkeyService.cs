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

    // #297: a second hotkey ID registered through this same HwndSource message hook - Ctrl+Alt+F
    // ("Flight recorder") for a manual flight-recorder trigger, the smallest extension of the
    // existing plumbing per the item's own framing ("registering a second hotkey ID via the same
    // HwndSource message hook it already has"). Checked against HotkeyId above (Ctrl+Alt+T) before
    // picking F - nothing else in this app currently claims Ctrl+Alt+anything.
    private const int SecondaryHotkeyId = 0x3A18;
    private const uint VK_F = 0x46;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_T = 0x54;

    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>True only if the OS actually granted this app the Ctrl+Alt+T binding - false
    /// (silently - see the class remarks) when another running app already owns it.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>#297: true only if Ctrl+Alt+F was actually granted - independent of
    /// <see cref="IsRegistered"/> above (one combination can be taken while the other is free).
    /// The Responsiveness tab's "Trigger now" button works either way - this only gates whether
    /// the *hotkey* shortcut is also live, per the item's own "a button is an acceptable
    /// simplification if the hotkey doesn't pan out" allowance.</summary>
    public bool IsSecondaryRegistered { get; private set; }

    public event Action? Pressed;

    /// <summary>#297: fired on Ctrl+Alt+F - wired to ResponsivenessViewModel's shared
    /// "handle a trigger" method, the same one the in-app "Trigger now" button calls.</summary>
    public event Action? SecondaryPressed;

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

        try
        {
            IsSecondaryRegistered = RegisterHotKey(_hwnd, SecondaryHotkeyId, MOD_CONTROL | MOD_ALT, VK_F);
        }
        catch
        {
            IsSecondaryRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == SecondaryHotkeyId)
        {
            SecondaryPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, HotkeyId);
                UnregisterHotKey(_hwnd, SecondaryHotkeyId);
            }
            _source?.RemoveHook(WndProc);
        }
        catch { /* best-effort */ }
    }
}
