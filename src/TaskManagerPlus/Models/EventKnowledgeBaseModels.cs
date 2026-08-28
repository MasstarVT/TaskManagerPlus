namespace TaskManagerPlus.Models;

/// <summary>
/// #117-126: the known-bad Event ID knowledge base - a small, curated "what this ID usually means"
/// entry keyed by (Provider, EventId), bundled read-only as an embedded resource
/// (Resources/EventKnowledgeBase.json) with a user-editable
/// %AppData%\TaskManagerPlus\event-kb-overrides.json merged on top - see
/// EventKnowledgeBaseService for the load/merge mechanics. Every field here is explicitly
/// informational, never authoritative - CLAUDE.md's "Quick flag, not a verdict" convention (#118)
/// applies to this entire type, the same as every other heuristic feature in this app.
/// </summary>
public sealed class EventKbEntry
{
    public string Provider { get; set; } = string.Empty;
    public int EventId { get; set; }

    /// <summary>One-line plain-English meaning - "what this event usually means", kept distinct
    /// from the raw Windows-authored message text shown elsewhere in the detail pane (#118's
    /// "what Windows literally logged" vs. "what this usually means" split).</summary>
    public string Meaning { get; set; } = string.Empty;

    public List<string> LikelyCauses { get; set; } = new();

    /// <summary>#120: this app's own severity opinion, independent of whatever Level Windows
    /// assigned the event - the whole point being that the two disagree constantly (DCOM 10016 is
    /// an Error that's almost always cosmetic; disk 153 and Ntfs 98 are Warnings that mean a
    /// failing drive).</summary>
    public EventKbSeverity SeverityRank { get; set; } = EventKbSeverity.Warning;

    public string NextStep { get; set; } = string.Empty;

    /// <summary>#118: "entries with low confidence are labelled as such rather than omitted" - most
    /// entries here are High/Medium (well-documented, widely-recognized event IDs); Low is reserved
    /// for a genuinely fuzzy pattern-match rather than a precise, well-known signature.</summary>
    public EventKbConfidence Confidence { get; set; } = EventKbConfidence.Medium;

    /// <summary>#121: true for the classic log-spam entries (DCOM 10016 permission errors,
    /// Kernel-EventTracing 2/3 session-start races, Group Policy 1129 on a non-domain PC) that are
    /// safe to collapse behind "hide known-noise" by default.</summary>
    public bool IsBenign { get; set; }

    /// <summary>#125: when NextStep maps to something this app already knows how to do, ActionKind
    /// says which real action to offer - None for the overwhelming majority of entries. Per #125,
    /// a KB category with no matching existing app action (storage/chkdsk, performance-counter
    /// rebuild, ...) stays text-only rather than getting a button with nothing real behind it - see
    /// EventsViewModel.ResolveKbAction's remarks for exactly what was searched for and not found.</summary>
    public EventKbActionKind ActionKind { get; set; } = EventKbActionKind.None;
}

public enum EventKbSeverity
{
    Verbose = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4,
}

public enum EventKbConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>#125: which real, already-existing app action (if any) a KB entry's next step maps to.
/// RestartService is currently the only one wired (Service Control Manager 7031/7009, via
/// ServicesViewModel.RestartCommand) - a grep of this codebase found no existing "run chkdsk"/
/// volume-repair action and no existing "rebuild performance counters" (lodctr /R) action to reuse
/// for the storage/Perflib KB categories, so those stay None (text-only next step).</summary>
public enum EventKbActionKind
{
    None,
    RestartService,
}

/// <summary>event-kb-overrides.json's root shape - a plain array wrapper, the same "small JSON,
/// silent fallback to built-in defaults on a missing/corrupt file" convention every other settings
/// file under AppPaths.SettingsDirectory uses (see EventFilterSettingsService/PollIntervalSettingsService).
/// An override entry with the same (Provider, EventId) as a bundled entry replaces it; a new
/// (Provider, EventId) is simply added - see EventKnowledgeBaseService.Reload.</summary>
public sealed class EventKbOverridesFile
{
    public List<EventKbEntry> Entries { get; set; } = new();
}

/// <summary>#124: one status code found in an event's message, resolved via `certutil -error` -
/// ResolvedText is the raw code itself (IsResolved=false) when certutil couldn't decode it, never a
/// guessed meaning (this app's "degrade, never fabricate" rule).</summary>
public sealed class StatusCodeExplain
{
    public string Code { get; init; } = string.Empty;
    public string ResolvedText { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
}
