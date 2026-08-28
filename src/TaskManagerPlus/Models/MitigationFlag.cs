namespace TaskManagerPlus.Models;

/// <summary>Round 15, #851: one badge in the Processes tab's mitigation-policy row ("DEP: On",
/// "ASLR: On", "CFG: Off", ...) - see ProcessMitigationService for how each is read.
/// IsEnabled is null for "Unknown" (couldn't be determined - see ProcessMitigationService's
/// per-policy try/catch), which the UI renders in a neutral color rather than as either state.</summary>
public sealed class MitigationFlag
{
    public string Name { get; init; } = string.Empty;
    public string StateText { get; init; } = "Unknown";
    public bool? IsEnabled { get; init; }

    public static MitigationFlag Of(string name, bool enabled) =>
        new() { Name = name, StateText = enabled ? "On" : "Off", IsEnabled = enabled };

    public static MitigationFlag Unknown(string name) =>
        new() { Name = name, StateText = "Unknown", IsEnabled = null };
}
