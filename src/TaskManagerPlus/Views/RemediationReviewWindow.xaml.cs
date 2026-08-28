using System.Windows;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>
/// #968-971: the "Fix this" review dialog - see RemediationReviewViewModel's remarks. Opened
/// modally (ShowDialog) from SummaryViewModel.FixFindingCommand, the same "ViewModel new's up and
/// shows a Window directly" convention MainViewModel already uses for MiniDashboardWindow.
/// </summary>
public partial class RemediationReviewWindow : Window
{
    public RemediationReviewWindow(RemediationReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
