using System.Windows;
using System.Windows.Controls;

namespace TaskManagerPlus.Views;

public partial class EnergyThermalsView : UserControl
{
    public EnergyThermalsView()
    {
        InitializeComponent();
    }

    /// <summary>#736: cross-link from the "Power plan &amp; sleep support" card over to the
    /// Startup tab's full hibernation/sleep-state inventory - same SelectTabByName mechanism as
    /// StartupView.xaml.cs's own cross-tab links (e.g. #713's ViewPowerTimeline_Click).</summary>
    private void ViewHibernationDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.SelectTabByName("Startup");
    }
}
