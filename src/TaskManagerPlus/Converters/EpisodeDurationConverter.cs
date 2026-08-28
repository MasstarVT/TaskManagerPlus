using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#604: renders a ThrottleEpisode's (End - Start) span as "Xm Ys" / "Xs" - bound to the
/// whole ThrottleEpisode item (not a single property) since the duration needs both timestamps.</summary>
public sealed class EpisodeDurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ThrottleEpisode episode) return string.Empty;

        var span = episode.End - episode.Start;
        if (span.TotalSeconds < 1) return "<1s";
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
