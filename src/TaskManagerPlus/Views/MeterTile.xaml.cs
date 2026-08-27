using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskManagerPlus.Views;

/// <summary>
/// Small dashboard widget: colored dot + title, a big value, and a colored progress bar with a
/// subtext underneath. Used on the Summary page for the Disk/Network/System-style tiles.
/// </summary>
public partial class MeterTile : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MeterTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(MeterTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubTextProperty =
        DependencyProperty.Register(nameof(SubText), typeof(string), typeof(MeterTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(MeterTile), new PropertyMetadata(0.0));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MeterTile), new PropertyMetadata(Brushes.Gray));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public MeterTile()
    {
        InitializeComponent();
    }

    /// <summary>Copies "Title: ValueText (SubText)" to the clipboard, so a user troubleshooting
    /// a PC can paste a reading straight into a forum post or support ticket without a
    /// screenshot. Best-effort - Clipboard.SetText can throw if another app is holding the
    /// clipboard open, which shouldn't crash the app over a convenience feature.</summary>
    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrEmpty(SubText) ? $"{Title}: {ValueText}" : $"{Title}: {ValueText} ({SubText})";
        try { Clipboard.SetText(text); } catch { /* ignore */ }
    }
}
