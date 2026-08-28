using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
    }

    /// <summary>TreeView has no built-in two-way SelectedItem binding, same as
    /// ProcessesView's ProcessTreeView_SelectedItemChanged - wired here instead.</summary>
    private void ChannelTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is EventsViewModel vm)
            vm.SelectedChannel = e.NewValue as EventChannelNode;
    }
}
