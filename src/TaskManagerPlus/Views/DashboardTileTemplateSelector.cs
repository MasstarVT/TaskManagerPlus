using System.Windows;
using System.Windows.Controls;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>Round 11, #69: picks the DataTemplate for one dashboard tile by Id - each tile's real
/// content template is declared as a keyed resource ("Tile_cpu", "Tile_memory", ...) in
/// SummaryView.xaml, so this selector is just a resource lookup rather than a hardcoded switch
/// tied to a specific ResourceDictionary instance.</summary>
public sealed class DashboardTileTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not DashboardTileViewModel tile) return null;
        if (container is FrameworkElement fe && fe.TryFindResource($"Tile_{tile.Id}") is DataTemplate dt)
            return dt;
        return base.SelectTemplate(item, container);
    }
}
