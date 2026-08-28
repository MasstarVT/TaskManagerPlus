using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#705: renders a nullable BootType as its display label ("Full restart", "Fast Startup
/// resume", "Resume from hibernate", or "Unknown boot type") - shares the same wording
/// BootTypeExtensions.ToDisplayLabel already uses in the ViewModel's own computed text, via that
/// same extension method, so the two never drift apart.</summary>
public sealed class BootTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as BootType?).ToDisplayLabel();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
