using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Maps a 0-100 percent value to a Star-weighted GridLength, for building a proportional
/// stacked bar out of plain Grid ColumnDefinitions - used by the Memory tab's "memory in use by
/// category" breakdown (Round 8 #37). A small floor keeps a genuinely-zero segment from
/// collapsing a ColumnDefinition to literal 0 width (which some layout passes render oddly).</summary>
public sealed class PercentToStarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        return new GridLength(Math.Max(percent, 0.5), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
