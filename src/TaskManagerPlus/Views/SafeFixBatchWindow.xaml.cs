using System.Windows;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>#976: the "Run safe fixes" batch runner dialog - see SafeFixBatchViewModel's remarks.
/// Opened modally from SummaryView's Health Check card, the same "ViewModel new's up and shows a
/// Window directly" convention RemediationReviewWindow already uses.</summary>
public partial class SafeFixBatchWindow : Window
{
    public SafeFixBatchWindow(SafeFixBatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
