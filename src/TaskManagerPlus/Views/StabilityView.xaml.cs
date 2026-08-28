using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

public partial class StabilityView : UserControl
{
    public StabilityView()
    {
        InitializeComponent();
    }

    /// <summary>#741: cross-link from a "Failed resumes" entry back over to the Startup tab's
    /// hiberfile/hibernation card - same SelectTabByName mechanism as StartupView.xaml.cs's own
    /// cross-tab links.</summary>
    private void ViewHiberfileCard_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Startup");
    }

    /// <summary>#781: cross-link from a "did an update break this?" flag over to the Windows Health
    /// tab's own #780 update-uninstall list - triggers the list load there (if not already loaded)
    /// so the user lands on a populated grid instead of an empty one they'd have to know to refresh.</summary>
    private void ViewUpdateUninstallInWindowsHealth_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is MainViewModel mainViewModel)
        {
            var windowsHealth = mainViewModel.WindowsHealth;
            if (!windowsHealth.HasLoadedRemovableUpdates && windowsHealth.LoadRemovableUpdatesCommand.CanExecute(null))
                windowsHealth.LoadRemovableUpdatesCommand.Execute(null);
        }
        window.SelectTabByName("Windows Health");
    }
}
