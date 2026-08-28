using System.Text.Json.Serialization;

namespace TaskManagerPlus.Models;

/// <summary>#915: the serializable half of one <see cref="DiagnosticStep"/> - deliberately a
/// separate, plain-data type rather than serializing <see cref="DiagnosticStep"/> itself, since
/// that class also carries live-only fields (the <c>Check</c> delegate, predicates) that have no
/// meaning once read back from disk on a later run of the app.</summary>
public sealed class TroubleshootStepRecord
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DiagnosticStepStatus Status { get; set; }
    public string ResultText { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
    public double? DurationMs { get; set; }
}

/// <summary>#915: one persisted Troubleshoot run transcript - written to
/// <c>AppPaths.SettingsDirectory\Runs\&lt;timestamp&gt;.json</c> by
/// <c>TroubleshootRunHistoryService</c> after every run finishes, and read back for the tab's
/// "Past runs" panel (open a saved run read-only, or re-run the same symptom fresh).</summary>
public sealed class TroubleshootRunRecord
{
    public string SymptomId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string VerdictText { get; set; } = string.Empty;
    public List<TroubleshootStepRecord> Steps { get; set; } = new();

    /// <summary>Not serialized - filled in by TroubleshootRunHistoryService.ListSaved() after
    /// deserializing, so the "Past runs" panel can act on the exact file a list entry came from
    /// (open it again, or use it to identify the run) without a second directory scan.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }
}
