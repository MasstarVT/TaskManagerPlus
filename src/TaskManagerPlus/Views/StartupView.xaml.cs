using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
    }

    /// <summary>#708: cross-link from "Drivers that failed to load at boot" over to the Services
    /// tab's own driver list - flips ShowDrivers on there and switches tabs, the same
    /// SelectTabByName mechanism App.xaml.cs's `--tab` launch flag already uses. Minimal
    /// code-behind, same as MainWindow's own tab-switching helpers - this view's ViewModel has no
    /// reference to sibling ViewModels/the window by design (see CLAUDE.md's cross-tab-coupling
    /// remarks), so this is the same kind of thin glue Ctrl+1..9 already is.</summary>
    private void ViewDriversInServices_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is MainViewModel mainViewModel) mainViewModel.Services.ShowDrivers = true;
        window.SelectTabByName("Services");
    }

    /// <summary>#713: cross-link from the boot performance card over to the Stability tab's
    /// "Power & boot timeline" strip.</summary>
    private void ViewPowerTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Stability");
    }
}
