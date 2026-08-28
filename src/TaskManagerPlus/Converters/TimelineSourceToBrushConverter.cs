using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#137: per-source colour coding for the unified incident timeline - one fixed accent
/// per TimelineSource, independent of the active theme palette (the timeline mixes several source
/// kinds in one list, so a legend-stable colour per source matters more here than theme-matching).</summary>
public sealed class TimelineSourceToBrushConverter : IValueConverter
{
    private static readonly Brush EventLog = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x4D));
    private static readonly Brush Minidump = new SolidColorBrush(Color.FromRgb(0xE5, 0x4B, 0x4B));
    private static readonly Brush Boot = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush Shutdown = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42));
    private static readonly Brush CsvLog = new SolidColorBrush(Color.FromRgb(0x4D, 0xA6, 0xE0));
    private static readonly Brush WerReport = new SolidColorBrush(Color.FromRgb(0xC9, 0x4B, 0x8C));
    private static readonly Brush Other = new SolidColorBrush(Color.FromRgb(0x9A, 0x6C, 0xE0));

    static TimelineSourceToBrushConverter()
    {
        EventLog.Freeze(); Minidump.Freeze(); Boot.Freeze(); Shutdown.Freeze(); CsvLog.Freeze(); WerReport.Freeze(); Other.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimelineSource source
            ? source switch
            {
                TimelineSource.EventLog => EventLog,
                TimelineSource.Minidump => Minidump,
                TimelineSource.Boot => Boot,
                TimelineSource.Shutdown => Shutdown,
                TimelineSource.CsvLog => CsvLog,
                TimelineSource.WerReport => WerReport,
                _ => Other,
            }
            : Other;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
