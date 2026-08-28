using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TaskManagerPlus.ViewModels;
using Forms = System.Windows.Forms;

namespace TaskManagerPlus;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    // Round 12, #85: system tray icon - System.Windows.Forms.NotifyIcon, since WPF has no tray
    // API of its own (see the csproj's UseWindowsForms comment). Created lazily in OnSourceInitialized
    // rather than the constructor so ApplyNativeWindowChrome/Handle exist first; disposed on Closed.
    private Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closing += (_, _) => _viewModel.Summary.GenerateReportOnExitIfEnabled();
        Closed += (_, _) => { _viewModel.Dispose(); _trayIcon?.Dispose(); TaskManagerPlus.Services.TrayBalloonService.Icon = null; };
        SourceInitialized += (_, _) => { ApplyNativeWindowChrome(); InitializeTrayIcon(); InitializeGlobalHotkey(); };
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    /// <summary>Round 12, #84: selects a tab by header text (case-insensitive) - used by the
    /// `--tab &lt;name&gt;` launch flag from App.xaml.cs. Matches the same way Ctrl+1..9 already
    /// does (by header text, not a hardcoded index), so it keeps working if tabs are ever
    /// reordered. Silently does nothing on an unrecognized name rather than showing an error for
    /// what's ultimately a convenience shortcut, not a required argument.</summary>
    public void SelectTabByName(string name)
    {
        foreach (var item in MainTabControl.Items)
        {
            if (item is TabItem tab && string.Equals(tab.Header as string, name, StringComparison.OrdinalIgnoreCase))
            {
                MainTabControl.SelectedItem = tab;
                return;
            }
        }
    }

    /// <summary>Round 12, #85: builds the tray icon (extracted from this app's own exe, so no
    /// separate .ico asset is needed) with an Open/Exit context menu, and a live CPU/RAM tooltip
    /// updated off PerformanceViewModel's existing PropertyChanged notifications - no new
    /// polling, just reading the same 1s-ticking numbers every other tab already shows.</summary>
    private void InitializeTrayIcon()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Exit", null, (_, _) => Close());

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Task Manager Plus",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
            UpdateTrayTooltip();
            _viewModel.Performance.PropertyChanged += (_, _) => UpdateTrayTooltip();

            // #964: lets AlertDeliveryService (Services/, no window reference of its own) show a
            // genuine tray balloon for the "TrayBalloon" alert channel - see TrayBalloonService's
            // remarks.
            TaskManagerPlus.Services.TrayBalloonService.Icon = _trayIcon;
        }
        catch
        {
            // Best-effort - a machine with no default shell icon association (rare) just means
            // no tray icon; minimize-to-tray then falls back to a normal taskbar minimize since
            // there'd be nothing to restore from otherwise (see MainWindow_StateChanged).
            _trayIcon = null;
        }
    }

    /// <summary>Live mini CPU/RAM readout (#85) - a NotifyIcon tooltip is capped at 63 characters
    /// on classic Windows shells, so this stays deliberately short rather than trying to cram in
    /// every metric the full window already shows.</summary>
    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null) return;
        string text = $"Task Manager Plus\nCPU {_viewModel.Performance.CpuCurrentPercent:0}%  RAM {_viewModel.Performance.RamPercent:0}%";
        if (text.Length > 63) text = text[..63];
        try { _trayIcon.Text = text; } catch { /* best-effort */ }
    }

    /// <summary>Minimize-to-tray (#85): hides the window (instead of a normal taskbar minimize)
    /// whenever the user minimizes it and MainViewModel.MinimizeToTray is on. Restoring happens
    /// via the tray icon's double-click or its "Open" menu entry (RestoreFromTray).</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray && _trayIcon is not null)
        {
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Round 12, #86: Ctrl+Alt+T "bring to front" global hotkey - see
    /// GlobalHotkeyService's remarks for why this isn't literally Ctrl+Shift+Esc. Opt-out via
    /// MainViewModel.GlobalHotkeyEnabled; registration failure (another app already owns the
    /// combination) is silent by design, not surfaced as an error.</summary>
    private void InitializeGlobalHotkey()
    {
        if (!_viewModel.GlobalHotkeyEnabled) return;

        _viewModel.Hotkey.Pressed += () => Dispatcher.Invoke(RestoreFromTray);
        _viewModel.Hotkey.Register(this);
    }

    /// <summary>Round 11, #80: Ctrl+1..Ctrl+9 jump straight to a tab, matching Task Manager's own
    /// Ctrl+Shift+Esc-style muscle memory for "get me to a specific view fast". The mapping (which
    /// tab each number opens) comes from MainViewModel.TabShortcutOrder - configurable via
    /// ui-preferences.json, defaulting to this app's first nine tabs in their normal strip order.
    /// Matches by tab Header text rather than a hardcoded index, so the mapping still works even if
    /// tabs are ever reordered in MainWindow.xaml.</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        int index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3, Key.D5 or Key.NumPad5 => 4, Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6, Key.D8 or Key.NumPad8 => 7, Key.D9 or Key.NumPad9 => 8,
            _ => -1,
        };
        if (index < 0) return;

        var order = _viewModel.TabShortcutOrder;
        if (index >= order.Count) return;
        var targetHeader = order[index];

        foreach (var item in MainTabControl.Items)
        {
            if (item is TabItem tab && string.Equals(tab.Header as string, targetHeader, StringComparison.OrdinalIgnoreCase))
            {
                MainTabControl.SelectedItem = tab;
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// Asks the OS to draw a dark title bar and rounded corners so the window
    /// chrome matches the app's own dark theme instead of the default white
    /// caption bar. Both are Windows 11-era DWM attributes; failures are
    /// swallowed so this is a no-op (not a crash) on older Windows builds.
    /// </summary>
    private void ApplyNativeWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
        }
        catch { /* pre-1809 Windows - ignore */ }

        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { /* pre-Windows 11 - ignore */ }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
