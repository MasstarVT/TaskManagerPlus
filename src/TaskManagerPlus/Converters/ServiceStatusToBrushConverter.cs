using System.Globalization;
using System.ServiceProcess;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>Color-codes a service status dot: green running, gray stopped, amber mid-transition.</summary>
public sealed class ServiceStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Running = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush Stopped = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x72));
    private static readonly Brush Transitioning = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42));

    static ServiceStatusToBrushConverter()
    {
        Running.Freeze(); Stopped.Freeze(); Transitioning.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ServiceControllerStatus.Running ? Running
         : value is ServiceControllerStatus.Stopped ? Stopped
         : Transitioning;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
