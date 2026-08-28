namespace TaskManagerPlus.Models;

/// <summary>
/// #500: one entry in the maintained known-problem-driver list (seeded from an embedded resource
/// into AppPaths.SettingsDirectory\known-problem-drivers.json on first run, then loaded from there
/// afterward so it can be edited/replaced without a rebuild - see KnownProblemDriverService). Plain
/// JSON-serializable DTO, no ObservableObject - this is read-only reference data, never edited from
/// inside the app itself.
/// </summary>
public sealed class KnownProblemDriverDefinition
{
    /// <summary>The driver's file name, matched case-insensitively against a loaded kernel module's
    /// file name and against a Stability-tab faulting-module name - e.g. "sptd.sys".</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Inclusive lower bound, dotted version text (e.g. "1.0.0.0") - null means "no lower
    /// bound". Both null means "flag this file name at any version".</summary>
    public string? MinVersion { get; set; }

    /// <summary>Inclusive upper bound - null means "no upper bound".</summary>
    public string? MaxVersion { get; set; }

    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Link to the evidence (KB article, CVE record, vendor advisory) backing this entry -
    /// null when this app doesn't have a specific stable link for it, shown as "no evidence link
    /// available" rather than a fabricated one.</summary>
    public string? EvidenceUrl { get; set; }
}

public sealed class KnownProblemDriverList
{
    public int SchemaVersion { get; set; } = 1;
    public List<KnownProblemDriverDefinition> Drivers { get; set; } = new();
}

/// <summary>#500: one definition matched against either the currently-loaded kernel module list
/// (#424's KernelModuleService) or the Stability tab's faulting-module data - explicitly labelled a
/// quick flag, not a verdict, both in this model's own MatchSourceText and in the UI: a file-name
/// (optionally version-ranged) match is circumstantial, not proof this specific machine is
/// experiencing the referenced issue.</summary>
public sealed class KnownProblemDriverMatch
{
    public string FileName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? EvidenceUrl { get; init; }

    /// <summary>e.g. "Currently loaded (version 1.2.3.4)" or "Named as a faulting module in a
    /// recent crash - see the Stability tab".</summary>
    public string MatchSourceText { get; init; } = string.Empty;

    public string? MatchedVersion { get; init; }
}
