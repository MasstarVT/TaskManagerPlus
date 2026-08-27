namespace TaskManagerPlus.Models;

/// <summary>Round 11, #82: just the accent/family/saturation subset of <see cref="ThemeColors"/> -
/// a small, shareable "here's my color scheme" file, distinct from the app's full theme.json
/// (persisted automatically, never hand-exported) and from any other settings file. Deliberately a
/// separate type from ThemeColors rather than reusing it directly, so this file's shape stays
/// stable even if ThemeColors later grows fields (like #78/#79's CompactRows/FontScale) that have
/// nothing to do with color and shouldn't be bundled into a "palette" export.</summary>
public sealed class PalettePreset
{
    public string Accent { get; set; } = "#3FA7FF";
    public string Cpu { get; set; } = "#3FA7FF";
    public string Ram { get; set; } = "#B18CFF";
    public string Disk { get; set; } = "#FFA53F";
    public string NetworkReceive { get; set; } = "#3DD68C";
    public string NetworkSend { get; set; } = "#F0546A";
    public string ThemeMode { get; set; } = "Dark";
    public double Saturation { get; set; } = 1.0;
    public bool ColorBlindSafeAlerts { get; set; }
}
