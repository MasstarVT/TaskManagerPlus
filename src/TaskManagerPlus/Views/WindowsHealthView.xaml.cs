using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

public partial class WindowsHealthView : UserControl
{
    public WindowsHealthView()
    {
        InitializeComponent();
    }

    /// <summary>#777: cross-link from a flagged update-stack service over to the Services tab -
    /// same SelectTabByName mechanism StartupView.xaml.cs's own cross-links use.</summary>
    private void ViewUpdateServicesInServices_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is MainViewModel mainViewModel) mainViewModel.Services.FilterText = "wuauserv";
        window.SelectTabByName("Services");
    }

    /// <summary>#779: cross-link from the update cache reclaim card over to the Storage tab.
    /// suggestions.md #1003: addresses the Capacity section, home of the reclaimable-space
    /// inventory (#356-360) this card is pointing at.</summary>
    private void ViewCacheInStorage_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Storage", section: "Capacity");
    }

    /// <summary>#792: cross-link from a WMI-Activity query-failure group's client process over to
    /// the Processes tab, filtered to that name - same SelectTabByName/FilterText mechanism
    /// StartupView.xaml.cs's own cross-links use.</summary>
    private void ViewWmiClientProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string processName } || string.IsNullOrWhiteSpace(processName)) return;
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is MainViewModel mainViewModel) mainViewModel.Processes.FilterText = processName;
        window.SelectTabByName("Processes");
    }

    /// <summary>#799: cross-link from the process-environment-drift summary over to the Processes
    /// tab - just switches tabs (drift can affect many differently-named processes at once, so
    /// there's no single name to filter by, unlike the other cross-links in this file).</summary>
    private void ViewDriftedProcesses_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Processes");
    }
}
