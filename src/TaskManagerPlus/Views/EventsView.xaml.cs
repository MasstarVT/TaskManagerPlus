using System.ComponentModel;
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
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>TreeView has no built-in two-way SelectedItem binding, same as
    /// ProcessesView's ProcessTreeView_SelectedItemChanged - wired here instead.</summary>
    private void ChannelTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is EventsViewModel vm)
            vm.SelectedChannel = e.NewValue as EventChannelNode;
    }

    // #116: the correlation columns follow ShowCorrelationColumns from here - a DataGridColumn
    // isn't in the visual or logical tree, so a binding on its Visibility has no DataContext or
    // ElementName scope to resolve against.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is EventsViewModel old) old.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is EventsViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyCorrelationColumnVisibility(vm);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventsViewModel.ShowCorrelationColumns) && sender is EventsViewModel vm)
            ApplyCorrelationColumnVisibility(vm);
    }

    private void ApplyCorrelationColumnVisibility(EventsViewModel vm)
    {
        var visibility = vm.ShowCorrelationColumns ? Visibility.Visible : Visibility.Collapsed;
        ActivityIdColumn.Visibility = visibility;
        RelatedActivityIdColumn.Visibility = visibility;
    }
}
