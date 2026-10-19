using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskManagerPlus.Common;

/// <summary>
/// Mouse-wheel scroll chaining for nested ScrollViewers. A WPF ScrollViewer marks every wheel
/// event handled, even one it can't act on, so a list or card with its own ScrollViewer (a
/// DataGrid, a MaxHeight-bounded list, a multi-line TextBox) inside a tab's section ScrollViewer
/// swallowed the wheel and the page stopped scrolling whenever the pointer was over it. Registered
/// once as a class handler (App.OnStartup): when the innermost ScrollViewer under the pointer has
/// nothing left to scroll in the wheel's direction, the event is passed to its parent instead.
/// </summary>
public static class ScrollChaining
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer) return;

        // PreviewMouseWheel tunnels outer -> inner, so this runs for every ScrollViewer on the
        // path; only the innermost one decides, or an outer viewer at its limit would steal the
        // wheel from an inner list that can still scroll.
        if (HasScrollViewerBetween(e.OriginalSource as DependencyObject, viewer)) return;

        bool canScroll = e.Delta > 0
            ? viewer.VerticalOffset > 0
            : viewer.VerticalOffset < viewer.ScrollableHeight;
        if (canScroll) return;

        if (VisualTreeHelper.GetParent(viewer) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = viewer,
        });
    }

    private static bool HasScrollViewerBetween(DependencyObject? node, ScrollViewer viewer)
    {
        while (node is not null && !ReferenceEquals(node, viewer))
        {
            if (node is ScrollViewer) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
