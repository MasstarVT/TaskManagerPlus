using System.Windows;
using System.Windows.Media;

namespace TaskManagerPlus.Common;

/// <summary>
/// suggestions.md #1014: attached properties that parameterize the one shared tab-strip
/// ControlTemplate (TabStripTemplate in Themes/Dark.xaml) across all three navigation levels.
/// The three strips share their entire structure - a header row hosting a ScrollViewer +
/// horizontal StackPanel of tabs (plus a trailing Tag presenter), over a selected-content row -
/// and differ only in chrome: header background, bottom border, and the strip's inner margin.
/// Before this, that shell existed in three hand-maintained template copies, and the level-1 copy
/// still hosted items in a TabPanel, which clips each tab by its own Margin during arrange (the
/// bug the other two copies' comments explain) - masked only by level 1's 2px tab margins.
/// </summary>
public static class TabStrip
{
    public static readonly DependencyProperty HeaderBackgroundProperty = DependencyProperty.RegisterAttached(
        "HeaderBackground", typeof(Brush), typeof(TabStrip), new FrameworkPropertyMetadata(Brushes.Transparent));

    public static Brush GetHeaderBackground(DependencyObject obj) => (Brush)obj.GetValue(HeaderBackgroundProperty);
    public static void SetHeaderBackground(DependencyObject obj, Brush value) => obj.SetValue(HeaderBackgroundProperty, value);

    public static readonly DependencyProperty HeaderBorderBrushProperty = DependencyProperty.RegisterAttached(
        "HeaderBorderBrush", typeof(Brush), typeof(TabStrip), new FrameworkPropertyMetadata(Brushes.Transparent));

    public static Brush GetHeaderBorderBrush(DependencyObject obj) => (Brush)obj.GetValue(HeaderBorderBrushProperty);
    public static void SetHeaderBorderBrush(DependencyObject obj, Brush value) => obj.SetValue(HeaderBorderBrushProperty, value);

    public static readonly DependencyProperty HeaderBorderThicknessProperty = DependencyProperty.RegisterAttached(
        "HeaderBorderThickness", typeof(Thickness), typeof(TabStrip), new FrameworkPropertyMetadata(new Thickness(0)));

    public static Thickness GetHeaderBorderThickness(DependencyObject obj) => (Thickness)obj.GetValue(HeaderBorderThicknessProperty);
    public static void SetHeaderBorderThickness(DependencyObject obj, Thickness value) => obj.SetValue(HeaderBorderThicknessProperty, value);

    public static readonly DependencyProperty StripMarginProperty = DependencyProperty.RegisterAttached(
        "StripMargin", typeof(Thickness), typeof(TabStrip), new FrameworkPropertyMetadata(new Thickness(0)));

    public static Thickness GetStripMargin(DependencyObject obj) => (Thickness)obj.GetValue(StripMarginProperty);
    public static void SetStripMargin(DependencyObject obj, Thickness value) => obj.SetValue(StripMarginProperty, value);
}
