using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Views;

/// <summary>
/// #498: the mandatory warning screen + typed-confirmation modal in front of enabling Driver
/// Verifier - see DevicesDriversViewModel.OpenVerifierSetupWizard for how this is opened and
/// EnableVerifierAsync for what runs after a successful ShowDialog(). Deliberately a real Window
/// with a required exact-text confirmation phrase, not a single MessageBox - see CLAUDE.md's
/// safety-critical callout for this feature. Plain code-behind (no separate ViewModel) matching
/// this app's existing small-modal-window convention (MiniDashboardWindow, ToastWindow) - "no DI
/// container, everything new'd directly" extends to not inventing MVVM ceremony for a one-shot
/// dialog either.
/// </summary>
public partial class DriverVerifierSetupWindow : Window
{
    private const string RequiredPhrase = "ENABLE VERIFIER";

    public ObservableCollection<VerifierCandidateDriver> Candidates { get; }

    /// <summary>Populated only once the user has typed the exact confirmation phrase, checked at
    /// least one driver, and clicked Enable - empty otherwise (including after Cancel).</summary>
    public List<string> SelectedDriverFileNames { get; private set; } = new();

    public DriverVerifierSetupWindow(Window? owner, IEnumerable<VerifierCandidateDriver> candidates)
    {
        InitializeComponent();
        if (owner is not null) Owner = owner;

        Candidates = new ObservableCollection<VerifierCandidateDriver>(candidates);
        foreach (var c in Candidates) c.PropertyChanged += (_, _) => UpdateEnableState();
        DataContext = this;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var c in Candidates) c.IsChecked = true;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var c in Candidates) c.IsChecked = false;
    }

    private void OnConfirmTextChanged(object sender, TextChangedEventArgs e) => UpdateEnableState();

    /// <summary>The one gate this whole window exists to enforce: the button stays disabled until
    /// the exact-case confirmation phrase is typed AND at least one driver is checked.</summary>
    private void UpdateEnableState()
    {
        if (EnableButton is null) return; // fires once during InitializeComponent before the button exists
        EnableButton.IsEnabled = ConfirmTextBox.Text == RequiredPhrase && Candidates.Any(c => c.IsChecked);
    }

    private void OnEnable(object sender, RoutedEventArgs e)
    {
        SelectedDriverFileNames = Candidates.Where(c => c.IsChecked).Select(c => c.FileName).ToList();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
