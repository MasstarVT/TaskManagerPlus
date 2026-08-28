using System.Windows;
using System.Windows.Controls;

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
}
