using System.Windows;
using System.Windows.Controls;
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
        if (DataContext is ProcessesViewModel vm)
            vm.SelectedProcess = (e.NewValue as ProcessTreeNode)?.Row;
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
