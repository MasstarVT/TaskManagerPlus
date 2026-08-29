using System.Windows;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;

namespace TaskManagerPlus.Common;

/// <summary>
/// Attached flag for the charts that show a bottom legend: LiveCharts' default legend text paint
/// is near-black, which is invisible against this app's dark chart cards (the GPU per-engine
/// chart's legend rendered as five colored dashes with no readable labels). Setting it here gives
/// legends the same fixed gray the per-ViewModel axis LabelsPaint values already use (SkiaSharp
/// paints live outside WPF's resource system, so DynamicResource can't reach them - see the
/// cross-tab-coupling remarks in CLAUDE.md for the same constraint on series colors).
/// </summary>
public static class ChartTheme
{
    /// <summary>Matches the AxisTextColor constant repeated in the chart-owning ViewModels.</summary>
    private static readonly SKColor LegendTextColor = new(0x9A, 0x9A, 0xA2);

    public static readonly DependencyProperty ThemedLegendProperty = DependencyProperty.RegisterAttached(
        "ThemedLegend", typeof(bool), typeof(ChartTheme), new PropertyMetadata(false, OnThemedLegendChanged));

    public static bool GetThemedLegend(DependencyObject obj) => (bool)obj.GetValue(ThemedLegendProperty);
    public static void SetThemedLegend(DependencyObject obj, bool value) => obj.SetValue(ThemedLegendProperty, value);

    private static void OnThemedLegendChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CartesianChart chart && e.NewValue is true)
            chart.LegendTextPaint = new SolidColorPaint(LegendTextColor);
    }
}
