using System.Runtime.InteropServices;
using System.Windows;
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
        Closed += (_, _) => _viewModel.Dispose();
        SourceInitialized += (_, _) => ApplyNativeWindowChrome();
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
