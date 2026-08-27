namespace TaskManagerPlus.Models;

/// <summary>
/// The set of user-customizable colors, stored as "#RRGGBB" hex strings so it
/// serializes to plain JSON without needing a custom converter.
/// </summary>
public sealed class ThemeColors
{
    public string Accent { get; set; } = "#3FA7FF";
    public string Cpu { get; set; } = "#3FA7FF";
    public string Ram { get; set; } = "#B18CFF";
    public string Disk { get; set; } = "#FFA53F";
    public string NetworkReceive { get; set; } = "#3DD68C";
    public string NetworkSend { get; set; } = "#F0546A";

    /// <summary>Theme family: "Dark", "Light", "Green", "Amber", "Blue", or "Monochrome".</summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>Palette saturation multiplier: 0 = grayscale, 1 = normal, up to 2 = boosted.</summary>
    public double Saturation { get; set; } = 1.0;

    /// <summary>#76: swaps the Success/Warning/Danger status colors for a color-blind-safe set
    /// (blue/yellow/orange instead of green/amber/red) - a separate toggle from the theme-family
    /// system above, since it targets specifically the status/alert colors this app leans on
    /// throughout its diagnostic UI, not the whole palette.</summary>
    public bool ColorBlindSafeAlerts { get; set; }

    /// <summary>Round 11, #78: dense/compact DataGrid row height, for fitting more rows on screen
    /// at once (Processes/Services/Startup/... grids).</summary>
    public bool CompactRows { get; set; }

    /// <summary>Round 11, #79: independent UI scale, separate from Windows' own display scaling -
    /// see ThemeViewModel.FontScale's remarks for why this is a uniform layout scale rather than a
    /// literal per-control font-size override.</summary>
    public double FontScale { get; set; } = 1.0;

    public static ThemeColors Defaults => new();
}
