using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closing += (_, _) => _viewModel.Summary.GenerateReportOnExitIfEnabled();
        Closed += (_, _) => _viewModel.Dispose();
        SourceInitialized += (_, _) => ApplyNativeWindowChrome();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
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
