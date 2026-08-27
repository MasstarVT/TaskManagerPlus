using System.Windows;
using System.Windows.Input;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>
/// Detached mini dashboard (#98/#99) - a small, always-on-top, draggable window showing a few
/// headline metrics, useful for reproducing an issue in another app or on a second monitor while
/// still watching CPU/RAM/Disk/Net/temp. Binds directly to the same MainViewModel (and therefore
/// the same already-ticking PerformanceViewModel/EnergyThermalsViewModel) the main window uses -
/// no second poller, just a second, smaller view over the same live data. Only one instance is
/// ever open at a time - see MainViewModel.ToggleMiniDashboardCommand.
/// </summary>
public partial class MiniDashboardWindow : Window
{
    public MiniDashboardWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        DataContext = mainViewModel;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Top + 24;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
