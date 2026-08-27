using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskManagerPlus.Views;

/// <summary>
/// TMOG-style "VFD meter": a dense, glowing digital-readout tile. Shares MeterTile's
/// Title/ValueText/SubText/Percent/AccentBrush dependency-property surface so it's a
/// near drop-in replacement, plus Glow (phosphor drop-shadow, off by default in dense
/// grids for render cost) and an optional Unit suffix.
/// </summary>
public partial class VfdMeter : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(VfdMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(VfdMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubTextProperty =
        DependencyProperty.Register(nameof(SubText), typeof(string), typeof(VfdMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(VfdMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(VfdMeter), new PropertyMetadata(0.0));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(VfdMeter),
            new PropertyMetadata(Brushes.Gray, OnAccentBrushChanged));

    public static readonly DependencyProperty GlowProperty =
        DependencyProperty.Register(nameof(Glow), typeof(bool), typeof(VfdMeter),
            new PropertyMetadata(true, OnGlowChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Whether the value readout has a phosphor-glow drop shadow. Default true;
    /// set false in dense grids (e.g. per-core tiles) to cut render cost.</summary>
    public bool Glow { get => (bool)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    public VfdMeter()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateGlow();
    }

    /// <summary>Copies "Title: ValueText Unit (SubText)" to the clipboard - see MeterTile's
    /// CopyValue_Click, which this mirrors (the two controls share the same DP surface but
    /// aren't related by inheritance, so the handler is duplicated rather than shared).</summary>
    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        var valuePart = string.IsNullOrEmpty(Unit) ? ValueText : $"{ValueText} {Unit}";
        var text = string.IsNullOrEmpty(SubText) ? $"{Title}: {valuePart}" : $"{Title}: {valuePart} ({SubText})";
        try { Clipboard.SetText(text); } catch { /* ignore */ }
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VfdMeter)d).UpdateGlow();

    private static void OnGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VfdMeter)d).UpdateGlow();

    private void UpdateGlow()
    {
        if (ValueTextBlock is null) return;

        if (!Glow)
        {
            ValueTextBlock.Effect = null;
            return;
        }

        var color = (AccentBrush as SolidColorBrush)?.Color ?? Colors.Gray;
        ValueTextBlock.Effect = new DropShadowEffect
        {
            Color = color,
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.85,
        };
    }
}
