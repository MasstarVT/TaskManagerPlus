using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Views;

public partial class ServicesView : UserControl
{
    public ServicesView()
    {
        InitializeComponent();
    }

    // The status dot's ring pulses green whenever a row's Status actually changes (a service
    // started, stopped, or otherwise transitioned) - not just on first render. DataGrid rows are
    // virtualized/recycled, so the ring keeps its own handler in Tag and re-subscribes to
    // whichever ServiceRow its container currently represents.
    private void PulseRing_Loaded(object sender, RoutedEventArgs e)
    {
        var ring = (FrameworkElement)sender;
        ring.Tag ??= CreateHandler(ring);
        Subscribe(ring, ring.DataContext);
    }

    private void PulseRing_Unloaded(object sender, RoutedEventArgs e)
    {
        var ring = (FrameworkElement)sender;
        Unsubscribe(ring, ring.DataContext);
    }

    private void PulseRing_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var ring = (FrameworkElement)sender;
        Unsubscribe(ring, e.OldValue);
        Subscribe(ring, e.NewValue);
    }

    private static PropertyChangedEventHandler CreateHandler(FrameworkElement ring) => (_, args) =>
    {
        if (args.PropertyName == nameof(ServiceRow.Status))
            Pulse(ring);
    };

    private static void Subscribe(FrameworkElement ring, object? dataContext)
    {
        if (dataContext is ServiceRow row && ring.Tag is PropertyChangedEventHandler handler)
            row.PropertyChanged += handler;
    }

    private static void Unsubscribe(FrameworkElement ring, object? dataContext)
    {
        if (dataContext is ServiceRow row && ring.Tag is PropertyChangedEventHandler handler)
            row.PropertyChanged -= handler;
    }

    private static void Pulse(FrameworkElement ring)
    {
        if (ring.TryFindResource("StatusPulse") is Storyboard storyboard)
            ring.BeginStoryboard(storyboard, HandoffBehavior.SnapshotAndReplace);
    }
}
