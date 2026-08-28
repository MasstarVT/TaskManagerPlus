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

    /// <summary>#779: cross-link from the update cache reclaim card over to the Storage tab.</summary>
    private void ViewCacheInStorage_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Storage");
    }
}
