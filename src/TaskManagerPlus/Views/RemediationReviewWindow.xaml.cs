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

        // Closing mid-run would leave the action running with no window left to stop it from.
        Closing += (_, e) =>
        {
            if (!viewModel.IsBusy || !viewModel.CancelRunCommand.CanExecute(null)) return;
            var answer = MessageBox.Show(this,
                "An action is still running. Stop it and close this window?\n\nChoose No to keep the window open until it finishes.",
                "Action still running", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) viewModel.CancelRunCommand.Execute(null);
            else e.Cancel = true;
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
