namespace TaskManagerPlus.Models;

/// <summary>
/// #692: best-effort OEM thermal-profile/fan-mode reading, from whichever vendor WMI namespace
/// OemThermalProfileService managed to reach (Lenovo/HP/Dell today) - see that service's remarks
/// for exactly which class/property backs each vendor. <see cref="Unknown"/> (Available = false) is
/// the honest, expected default on any non-OEM/DIY-built system or a laptop from a vendor this app
/// doesn't recognize - never a guessed mode.
/// </summary>
public sealed class OemThermalProfileInfo
{
    public bool Available { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public string ModeText { get; init; } = "Unknown — no OEM thermal namespace on this system";

    public static readonly OemThermalProfileInfo Unknown = new();
}
