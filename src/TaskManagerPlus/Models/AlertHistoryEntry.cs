using System.Text.Json.Serialization;

namespace TaskManagerPlus.Models;

/// <summary>
/// #963: one line in alerts-history.jsonl - every threshold/rule alert that fired, regardless of
/// whether it was actually shown (see WasSuppressedByQuietHours) or would normally be silent (see
/// Channel == SilentLogOnly, still logged here per #964's "still log to alerts-history.jsonl"
/// requirement). AlertDeliveryService is the one place that appends these.
/// </summary>
public sealed class AlertHistoryEntry
{
    public DateTime TimestampUtc { get; set; }

    /// <summary>Convenience for XAML binding (the Background Health panel's alert list) - not
    /// persisted, since TimestampUtc alone is the source of truth on disk.</summary>
    [JsonIgnore]
    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    /// <summary>Stable id - a rule-engine finding's Rule.Id, or one of the three fixed-threshold
    /// synthetic ids ("builtin.threshold.cpu"/"builtin.threshold.memory"/"builtin.threshold.temperature")
    /// so #965's escalation counting has something stable to key off for either kind of alert.</summary>
    public string RuleId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public RuleSeverity Severity { get; set; } = RuleSeverity.Medium;
    public AlertChannel Channel { get; set; } = AlertChannel.Toast;

    /// <summary>#964: true when this alert fired during a configured quiet-hours window and its
    /// toast/balloon was suppressed as a result (the log line is still written either way).</summary>
    public bool WasSuppressedByQuietHours { get; set; }

    /// <summary>#965: true when this alert was forced through (and its severity bumped one level)
    /// because the same rule fired EscalateAfterRepeats times within EscalateWindowSeconds.</summary>
    public bool WasEscalated { get; set; }
}
