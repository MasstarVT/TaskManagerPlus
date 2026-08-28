namespace TaskManagerPlus.Models;

/// <summary>#491: one registry value found under one of WHEA's policy keys - shown as raw name/data
/// regardless of whether this app has a documented meaning for it (Description/IsConcerning are
/// only filled in for the handful of values Microsoft's own WDK documentation names - see
/// WheaPolicyService), so nothing here is a guess.</summary>
public sealed class WheaPolicyValue
{
    public string Name { get; init; } = string.Empty;
    public string ValueText { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsConcerning { get; init; }
}

/// <summary>
/// #491: WHEA's own hardware-error-handling policy configuration, read from the registry -
/// HKLM\SYSTEM\CurrentControlSet\Control\WHEA\Policy (the key Microsoft's WDK "WHEA Policy
/// Settings" documentation names for WHEA's Predictive Failure Analysis configuration) plus
/// \WHEA\Policies (checked too, contents shown as raw name/value pairs since this app doesn't have
/// a verified documented schema for that location). WHEA assumes fully-enabled defaults for any
/// value not present under either key, so "nothing found" is the common, healthy case - not
/// evidence of a problem by itself.
/// </summary>
public sealed class WheaPolicyInfo
{
    public bool PolicyKeyFound { get; init; }
    public bool PoliciesKeyFound { get; init; }
    public IReadOnlyList<WheaPolicyValue> Values { get; init; } = Array.Empty<WheaPolicyValue>();
    public bool IsConcerning => Values.Any(v => v.IsConcerning);
}
