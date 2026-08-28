using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#938/#940: color-codes a Timeline marker - the danger color for a failure marker (a
/// failed update, a crash/service-failure record, a detected perf spike, an over-threshold
/// thermal transition), the accent color otherwise. Same TryFindResource("...Brush") lookup
/// DiagnosticStepStatusToBrushConverter already uses, so a live theme-family switch (which mutates
/// these brush instances in place - see CLAUDE.md's theming remarks) recolors these too.</summary>
public sealed class TimelineEventBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is TimelineEvent { IsFailure: true } ? "DangerBrush" : "AccentBrush";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
