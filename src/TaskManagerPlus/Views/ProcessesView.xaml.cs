using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
    }

    /// <summary>Round 7 #1: TreeView has no built-in two-way SelectedItem binding, so the process
    /// tree's selection is pushed into the view model manually here - the flat DataGrid's
    /// SelectedItem two-way binding does the equivalent job for the non-tree view.</summary>
    private void ProcessTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not ProcessesViewModel vm) return;

        // When a tick removes the selected node (its process exited, or its parent did and it
        // moves to the root level), WPF moves the selection itself - to the removed node's parent,
        // or to the first root - which would silently retarget End task and the inspectors at a
        // process the user never picked. Ignore those moves and settle the selection once the
        // tree update has finished.
        if (vm.IsSyncingProcessTree)
        {
            if (!_treeSelectionFixupQueued)
            {
                _treeSelectionFixupQueued = true;
                // Loaded priority runs after layout, so a node just re-inserted has its container.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestoreTreeSelectionAfterSync));
            }
            return;
        }

        vm.SelectedProcess = (e.NewValue as ProcessTreeNode)?.Row;
    }

    private bool _treeSelectionFixupQueued;

    /// <summary>Re-selects the still-running selected process at its new place in the tree, or
    /// clears the selection (in the tree and the view model) when it has exited.</summary>
    private void RestoreTreeSelectionAfterSync()
    {
        _treeSelectionFixupQueued = false;
        if (DataContext is not ProcessesViewModel vm) return;

        var row = vm.SelectedProcess;
        if (row is not null && FindTreeContainer(ProcessTreeView, row) is { } current)
        {
            current.IsSelected = true;
            return;
        }

        if (ProcessTreeView.SelectedItem is ProcessTreeNode moved && FindTreeContainer(ProcessTreeView, moved.Row) is { } wrong)
            wrong.IsSelected = false; // raises SelectedItemChanged(null), which clears the view model's selection
        else
            vm.SelectedProcess = null;
    }

    private static TreeViewItem? FindTreeContainer(ItemsControl parent, ProcessRow row)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (item is ProcessTreeNode node && ReferenceEquals(node.Row, row)) return container;
            if (FindTreeContainer(container, row) is { } found) return found;
        }
        return null;
    }

    /// <summary>#761: cross-link from a selected svchost.exe row over to the Services tab's svchost
    /// group breakdown - same SelectTabByName mechanism StartupView.xaml.cs's own cross-links use
    /// (this view's ViewModel has no reference to sibling ViewModels/the window by design, see
    /// CLAUDE.md's cross-tab-coupling remarks). Button IsEnabled is bound to
    /// ProcessesViewModel.IsSvchostRowSelected, so this only ever fires for a svchost row.</summary>
    private void ViewSvchostGroupsInServices_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is MainViewModel mainViewModel) mainViewModel.Services.ShowSvcHostGroups = true;
        window.SelectTabByName("Services");
    }
}
