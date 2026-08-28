namespace TaskManagerPlus.Models;

/// <summary>#661: one power-scheme setting whose AC and/or DC index differs between the active
/// scheme and the built-in Balanced scheme's own defaults (SCHEME_BALANCED) - see
/// PowerPlanService.ReadPlanSettingDiffAsync's remarks. Only settings that actually differ are
/// surfaced; identical settings are filtered out before this reaches the UI.</summary>
public sealed class PowerPlanSettingDiff
{
    public string SubgroupName { get; init; } = string.Empty;
    public string SettingName { get; init; } = string.Empty;

    /// <summary>Raw "Current AC/DC Power Setting Index" hex text as powercfg reports it (values
    /// aren't always a simple percent/int the way #631's processor-state read is - some settings
    /// are opaque bitmasks or GUID selections), so this is kept as display text rather than parsed
    /// further.</summary>
    public string ActiveAcText { get; init; } = string.Empty;
    public string ActiveDcText { get; init; } = string.Empty;
    public string DefaultAcText { get; init; } = string.Empty;
    public string DefaultDcText { get; init; } = string.Empty;

    public bool AcDiffers { get; init; }
    public bool DcDiffers { get; init; }
}
