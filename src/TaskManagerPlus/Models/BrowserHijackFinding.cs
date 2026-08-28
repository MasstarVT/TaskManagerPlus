namespace TaskManagerPlus.Models;

/// <summary>
/// One #897 browser-hijack-check row - a homepage/search-engine/startup-page override, a
/// force-installed extension policy, a native-messaging-host registration, or an external-
/// extensions file, found for one browser/profile (or "Policy" for a machine-wide registry
/// finding). A new small model rather than folding into BrowserExtensionInfo, per that item's own
/// text - this isn't about one installed extension, it's about configuration shapes that are
/// themselves the signal.
/// </summary>
public sealed class BrowserHijackFinding
{
    public string Browser { get; init; } = string.Empty;

    /// <summary>Profile folder name ("Default", "Profile 1", ...) or "Policy" for a machine-wide
    /// registry-policy finding that isn't tied to one profile.</summary>
    public string ProfileOrScope { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
}
