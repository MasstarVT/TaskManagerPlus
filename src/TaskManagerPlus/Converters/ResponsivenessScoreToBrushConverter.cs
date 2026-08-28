using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>#294: color-codes the Processes-tab responsiveness-score dot - green at/above
/// ResponsivenessScoreService.GoodThreshold, amber at/above FairThreshold, red below, dim gray for
/// null (no data yet - see ProcessRow.ResponsivenessScore's remarks - deliberately distinct from
/// "measured and fine", the same "no data reads dimmer than measured-and-idle" convention
/// BackgroundActivityStateToBrushConverter's Unknown color already establishes). Colors match this
/// app's actual SuccessColor/WarningColor/DangerColor palette values (see Themes/Dark.xaml) rather
/// than inventing a new palette, the same "same three-color shape as ServiceStatusToBrushConverter"
/// idiom that converter's own remarks describe.</summary>
public sealed class ResponsivenessScoreToBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush Fair = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42));
    private static readonly Brush Poor = new SolidColorBrush(Color.FromRgb(0xF0, 0x54, 0x6A));
    private static readonly Brush Unknown = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52));

    static ResponsivenessScoreToBrushConverter()
    {
        Good.Freeze(); Fair.Freeze(); Poor.Freeze(); Unknown.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double score) return Unknown;
        return score >= Services.ResponsivenessScoreService.GoodThreshold ? Good
             : score >= Services.ResponsivenessScoreService.FairThreshold ? Fair
             : Poor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
