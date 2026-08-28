using System.Windows;
using System.Windows.Input;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>
/// suggestions.md #1000: the Ctrl+K command palette - a borderless overlay window (same
/// WindowStyle="None" + AllowsTransparency shape MiniDashboardWindow already establishes) over
/// GlobalSearchViewModel.Results. Enter/double-click activates the selected (or first) result via
/// GlobalSearchViewModel.Activate, which raises NavigationRequested for MainWindow to actually
/// carry out; Escape or losing focus closes the palette without side effects.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    public CommandPaletteWindow(GlobalSearchViewModel search)
    {
        InitializeComponent();
        DataContext = search;
        // A fresh palette open should never carry over the last search - start clean each time.
        search.SearchText = string.Empty;
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };
    }

    private void OnDeactivated(object? sender, EventArgs e) => Close();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSelectedOrFirst();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0) return;
        int next = Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ActivateSelectedOrFirst()
    {
        if (DataContext is not GlobalSearchViewModel search) return;
        var target = ResultsList.SelectedItem as SearchResult ?? (ResultsList.Items.Count > 0 ? ResultsList.Items[0] as SearchResult : null);
        if (target is null) return;
        search.Activate(target);
        Close();
    }

    private void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not GlobalSearchViewModel search) return;
        if (ResultsList.SelectedItem is SearchResult target)
        {
            search.Activate(target);
            Close();
        }
    }
}
