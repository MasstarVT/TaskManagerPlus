using System.Collections.Generic;
using System.Linq;
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

    // #690: WM_DISPLAYCHANGE hook - same HwndSource.AddHook pattern GlobalHotkeyService already
    // establishes for its own single window message, just inline here rather than a dedicated
    // service (there's nothing to register/unregister with the OS the way RegisterHotKey needs -
    // WM_DISPLAYCHANGE is broadcast to every top-level window automatically).
    private const int WM_DISPLAYCHANGE = 0x007E;
    private HwndSource? _displayChangeHookSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Round 17, item 59: jump from a Stability-tab service-restart-loop warning to the
        // matching Services-tab entry - same "raise an event, let the shell handle cross-view-
        // model navigation" shape as the new-crash-dump toast wiring below, reusing SelectTabByName
        // (already built for the Ctrl+1..9 / --tab launch-flag shortcuts) rather than new plumbing.
        _viewModel.Stability.JumpToServiceRequested += name =>
        {
            SelectTabByName("Services");
            _viewModel.Services.FilterText = name;
        };

        Closing += (_, _) => _viewModel.Summary.GenerateReportOnExitIfEnabled();
        Closed += (_, _) =>
        {
            _viewModel.Search.NavigationRequested -= OnSearchNavigationRequested;
            _viewModel.Dispose();
            _trayIcon?.Dispose();
            _displayChangeHookSource?.RemoveHook(DisplayChangeWndProc);
            TaskManagerPlus.Services.TrayBalloonService.Icon = null;
        };
        SourceInitialized += (_, _) => { ApplyNativeWindowChrome(); InitializeTrayIcon(); InitializeGlobalHotkey(); InitializeDisplayChangeHook(); };
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // #486: Devices & Drivers' "System snapshot & driver diff..." button jumps to the Summary
        // tab's existing snapshot UI - the same thin, event-based cross-tab coupling SelectTabByName's
        // other callers (the `--tab` launch flag, Ctrl+1..9) already use, rather than either
        // ViewModel needing a direct reference to the other.
        _viewModel.DevicesDrivers.OpenSnapshotUiRequested += (_, _) => SelectTabByName("Summary");

        // suggestions.md #1000: the command palette's ViewModel has no tab-switching/drawer-opening
        // capability of its own (see SearchNavigationRequest's remarks) - this Window performs
        // whatever a search result's activation asked for.
        _viewModel.Search.NavigationRequested += OnSearchNavigationRequested;
    }

    /// <summary>#107: stops the Events tab's live-tail "Follow" subscription whenever the tab
    /// strip's selection moves away from it - a live EventLogWatcher has no reason to keep
    /// pushing rows into a grid nobody's looking at, and this is the one place that reliably knows
    /// when that happens regardless of how the user navigated away (tab click, Ctrl+1..9, --tab).
    /// Since the tab strip became a two-level group/leaf structure, Events is no longer a direct
    /// child of MainTabControl and selection changes reach here bubbled up from the group-level
    /// TabControl (and from any in-page section chip bar). So rather than filtering on
    /// OriginalSource being MainTabControl itself - which is now never true for the change that
    /// actually moves off Events - this asks the only question that matters: after this change, is
    /// Events still the visible leaf? A bubbled change from somewhere Events isn't visible answers
    /// no and stops the watcher, which is the correct outcome either way.</summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        if (!IsLeafTabActive("Events")) _viewModel.Events.OnTabDeactivated();
    }

    /// <summary>True when <paramref name="name"/> is on the currently selected path of tabs -
    /// i.e. its view is the one on screen. Walks down from MainTabControl through each selected
    /// TabItem whose content is itself a TabControl (the group level).</summary>
    private bool IsLeafTabActive(string name)
    {
        for (TabControl? control = MainTabControl; control is not null;)
        {
            if (control.SelectedItem is not TabItem tab) return false;
            if (string.Equals(HeaderNameOf(tab), name, StringComparison.OrdinalIgnoreCase)) return true;
            control = tab.Content as TabControl;
        }

        return false;
    }

    /// <summary>#690: forwards WM_DISPLAYCHANGE (resolution/color-depth change, or a monitor
    /// connect/disconnect that Windows resolves into a new desktop configuration) into
    /// SystemSpecsViewModel's persisted display-change history - see its RecordDisplayChange
    /// remarks. lParam's low/high words are the new desktop width/height in pixels (documented
    /// WM_DISPLAYCHANGE payload), wParam is the new bit depth.</summary>
    private void InitializeDisplayChangeHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _displayChangeHookSource = HwndSource.FromHwnd(hwnd);
        _displayChangeHookSource?.AddHook(DisplayChangeWndProc);
    }

    private IntPtr DisplayChangeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            try
            {
                int bitsPerPixel = wParam.ToInt32();
                long l = lParam.ToInt64();
                int width = (int)(l & 0xFFFF);
                int height = (int)((l >> 16) & 0xFFFF);
                _viewModel.SystemSpecs.RecordDisplayChange($"Display configuration changed - {width}x{height} @ {bitsPerPixel}-bit color");
            }
            catch
            {
                // Best-effort - a malformed/unexpected payload just means this one event isn't logged.
            }
        }
        return IntPtr.Zero;
    }

    // suggestions.md #1000: one shared instance, reused across Ctrl+K presses rather than a new
    // Window per open - mirrors MainViewModel's own single-instance MiniDashboardWindow toggle.
    private Views.CommandPaletteWindow? _commandPalette;

    /// <summary>suggestions.md #1000: Ctrl+K opens the command palette - see
    /// SearchNavigationRequest's remarks for why the actual navigation is performed here rather
    /// than inside GlobalSearchViewModel.</summary>
    private void OpenCommandPalette()
    {
        if (_commandPalette is { IsVisible: true }) { _commandPalette.Activate(); return; }
        _commandPalette = new Views.CommandPaletteWindow(_viewModel.Search) { Owner = this };
        _commandPalette.Show();
    }

    private void OnSearchNavigationRequested(TaskManagerPlus.Models.SearchNavigationRequest nav)
    {
        if (nav.TabName is { Length: > 0 } tab) SelectTabByName(tab);

        if (nav.TroubleshootPanel == "Glossary") _viewModel.Troubleshoot.ShowGlossaryCommand.Execute(null);
        else if (nav.TroubleshootPanel == "Timeline") _viewModel.Troubleshoot.ShowTimelineCommand.Execute(null);

        if (nav.SelectRuleId is { Length: > 0 } ruleId)
        {
            var row = _viewModel.RulesEditor.Rows.FirstOrDefault(r => r.Id == ruleId);
            if (row is not null) _viewModel.RulesEditor.SelectedRow = row;
        }

        if (nav.OpenSettings || nav.SelectRuleId is { Length: > 0 }) _viewModel.IsSettingsOpen = true;
    }

    /// <summary>Round 12, #84: selects a tab by header text (case-insensitive) - used by the
    /// `--tab &lt;name&gt;` launch flag from App.xaml.cs. Matches the same way Ctrl+1..9 already
    /// does (by header text, not a hardcoded index), so it keeps working if tabs are ever
    /// reordered. Silently does nothing on an unrecognized name rather than showing an error for
    /// what's ultimately a convenience shortcut, not a required argument.</summary>
    public void SelectTabByName(string name)
    {
        var path = FindTabPath(name);
        if (path is null) return;

        // Outermost first: selecting the group is what makes its nested TabControl the live one,
        // so the inner assignment has to come after it, not before.
        foreach (var (owner, tab) in path) owner.SelectedItem = tab;
    }

    /// <summary>Finds a tab by name anywhere in the group/leaf tree and returns the full
    /// (owner, tab) chain from MainTabControl down to it, or null if no tab matches.
    ///
    /// Breadth-first on purpose: a group and one of its leaves can legitimately share a name
    /// (the System group's first leaf is also called System), and the shallower one has to win so
    /// that `--tab System` lands where it did before the tabs were grouped.
    ///
    /// This walks TabItem.Content rather than the visual tree because a TabControl only realizes
    /// the selected tab's content - the nested TabControls are plain XAML-declared objects and are
    /// therefore reachable this way whether or not their group has ever been shown, which a
    /// VisualTreeHelper walk would not be.</summary>
    private List<(TabControl Owner, TabItem Tab)>? FindTabPath(string name)
    {
        var queue = new Queue<(TabControl Owner, List<(TabControl, TabItem)> Prefix)>();
        queue.Enqueue((MainTabControl, new List<(TabControl, TabItem)>()));

        while (queue.Count > 0)
        {
            var (owner, prefix) = queue.Dequeue();
            foreach (var item in owner.Items)
            {
                if (item is not TabItem tab) continue;

                var path = new List<(TabControl, TabItem)>(prefix) { (owner, tab) };
                if (string.Equals(HeaderNameOf(tab), name, StringComparison.OrdinalIgnoreCase)) return path;
                if (tab.Content is TabControl nested) queue.Enqueue((nested, path));
            }
        }

        return null;
    }

    /// <summary>The name a tab is addressed by. Header is a plain string for every tab but
    /// Stability, whose Header is a StackPanel so it can carry the new-dump alert badge - for that
    /// one `Header as string` is null, so matching on it alone silently failed to find the tab and
    /// --tab Stability / the Ctrl+K command palette's Stability results / any cross-tab jump to it
    /// just did nothing. AutomationProperties.Name is the fallback because it is the same value a
    /// screen reader announces, so the two can't drift apart.</summary>
    private static string? HeaderNameOf(TabItem tab)
    {
        if (tab.Header is string header && header.Length > 0) return header;
        string automationName = System.Windows.Automation.AutomationProperties.GetName(tab);
        return string.IsNullOrEmpty(automationName) ? null : automationName;
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

            // Round 14, item 27: reuse this same NotifyIcon for the new-crash-dump toast rather
            // than inventing a second notification mechanism.
            _viewModel.Stability.ShowTrayToastRequested += (title, text) =>
            {
                try { _trayIcon?.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Warning); }
                catch { /* best-effort - the in-app tab badge/banner still shows either way */ }
            };

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
        // #297: Ctrl+Alt+F - a manual flight-recorder trigger, registered through the same
        // HwndSource hook as Ctrl+Alt+T above. Calls the exact same HandleTriggerAsync method the
        // Responsiveness tab's "Trigger now" button and #297's automatic rules use.
        _viewModel.Hotkey.SecondaryPressed += () => Dispatcher.Invoke(() => _ = _viewModel.Responsiveness.HandleTriggerAsync("Manual trigger (Ctrl+Alt+F)"));
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
        // suggestions.md #1000: Ctrl+K opens the command palette - checked before the
        // Ctrl+1..9-only branch below so it isn't shadowed by the `return` there.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

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

        // Goes through SelectTabByName rather than scanning MainTabControl.Items directly: the
        // configured shortcut names are leaf tabs ("Memory", "Stability", ...), which are now one
        // level down inside their group, and this is the one place that knows how to walk there.
        // It also picks up HeaderNameOf's AutomationProperties.Name fallback, so Ctrl+1..9 can
        // reach Stability - whose Header is a panel, not a string - which it previously could not.
        var target = order[index];
        if (FindTabPath(target) is null) return;

        SelectTabByName(target);
        e.Handled = true;
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
