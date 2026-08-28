namespace TaskManagerPlus.Models;

/// <summary>#624: best-effort PSU identification via Win32_PowerSupply / Win32_SystemEnclosure -
/// see PsuService's remarks for why most consumer/DIY boards report neither and this ends up
/// null (falling back to the user-entered wattage in psu.json) far more often than a populated
/// name/wattage.</summary>
public sealed class PsuInfo
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Null when the WMI class exists but didn't populate a wattage figure (common - most
    /// OEMs that expose Win32_PowerSupply at all still leave RangeOfInputVoltage/wattage fields
    /// blank).</summary>
    public double? RatedWattageW { get; init; }

    public string Source { get; init; } = string.Empty;
}

/// <summary>#624: the one setting persisted to psu.json (PsuSettingsService) - a user-entered PSU
/// wattage, used as the sanity-check denominator whenever WMI doesn't report one (the common
/// case). Same "one small settings file, fails silently to defaults" shape as every other
/// persisted setting in this app.</summary>
public sealed class PsuSettings
{
    public double? UserRatedWattageW { get; set; }
}
