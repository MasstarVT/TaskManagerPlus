namespace TaskManagerPlus.Models;

/// <summary>#662: one commonly-hidden-in-the-Control-Panel-UI power setting that actually matters
/// day to day (processor performance boost mode, core-parking min/max cores, minimum/maximum
/// processor state, system cooling policy), read from `powercfg /qh` (which - unlike the plain
/// `/q` #661 uses - includes settings the Control Panel Power Options UI hides by default) and
/// paired AC-next-to-DC so a "fast plugged in, crawls on battery" mismatch is visible at a glance.
/// See PowerPlanService.ReadHiddenPowerSettingsAsync's remarks.</summary>
public sealed class HiddenPowerSettingRow
{
    public string SettingName { get; init; } = string.Empty;

    /// <summary>Fixed, hand-written plain-English explanation of what this setting actually does -
    /// not sourced from Windows (powercfg's own setting descriptions are themselves indirect
    /// string resource references this app doesn't resolve; see the service's remarks).</summary>
    public string Explanation { get; init; } = string.Empty;

    public string AcValueText { get; init; } = "Unknown";
    public string DcValueText { get; init; } = "Unknown";

    /// <summary>True when the AC and DC values for this setting are meaningfully different -
    /// exactly the "only visible side by side" case #662 calls out (e.g. minimum processor state
    /// 100% on AC but 5% on DC).</summary>
    public bool ValuesDiffer { get; init; }
}
