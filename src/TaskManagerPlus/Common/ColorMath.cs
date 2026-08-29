using System.Windows.Media;

namespace TaskManagerPlus.Common;

/// <summary>
/// Small shared color-math helpers used by theming (accent shading, palette
/// saturation adjustment). Kept separate from ThemeViewModel now that a
/// second consumer (theme-family saturation) needs the same kind of math.
/// </summary>
public static class ColorMath
{
    /// <summary>Blends a color toward white by the given amount (0..1).</summary>
    public static Color Lighten(Color c, double amount)
    {
        byte L(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * amount, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    /// <summary>Perceived (relative) luminance in the 0..1 range.</summary>
    /// <remarks>A cheap gamma-encoded weighted average - fine for "is this accent light or dark
    /// enough to need flipped text", but NOT the WCAG definition: it skips the sRGB linearization,
    /// so its 0.5 midpoint does not correspond to WCAG's contrast midpoint. Anything tuned against
    /// WCAG contrast ratios (the color-blind-safe palette pairing) must use
    /// <see cref="WcagRelativeLuminance"/> instead.</remarks>
    public static double RelativeLuminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>WCAG 2.x relative luminance (sRGB-linearized), 0..1 - the L in the contrast-ratio
    /// formula (L1 + 0.05) / (L2 + 0.05). 0.179 is the luminance at which white and black text
    /// reach equal contrast, i.e. the natural "is this a light or a dark surface" threshold.</summary>
    public static double WcagRelativeLuminance(Color c)
    {
        static double Lin(byte channel)
        {
            double v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>
    /// Scales a color's saturation by <paramref name="factor"/> (1.0 = unchanged,
    /// 0.0 = grayscale, &gt;1.0 = boosted, clamped at full saturation) via an
    /// HSL round-trip. Lightness and hue are preserved.
    /// </summary>
    public static Color AdjustSaturation(Color c, double factor)
    {
        if (factor == 1.0) return c;

        var (h, s, l) = ToHsl(c);
        s = Math.Clamp(s * factor, 0.0, 1.0);
        return FromHsl(h, s, l, c.A);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;

        if (max == min)
            return (0.0, 0.0, l); // achromatic

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;

        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        double r, g, b;
        if (s == 0.0)
        {
            r = g = b = l; // achromatic
        }
        else
        {
            double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double p = 2.0 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        return Color.FromArgb(alpha, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }
}
