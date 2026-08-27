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
}
