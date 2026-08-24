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

    public static ThemeColors Defaults => new();
}
