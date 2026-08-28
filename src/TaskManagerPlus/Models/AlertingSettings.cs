namespace TaskManagerPlus.Models;

/// <summary>
/// #964: persisted to alerting.json (AppPaths.SettingsDirectory) - a do-not-disturb time window
/// applied to every alert's toast/balloon delivery (never its alerts-history.jsonl log line, see
/// AlertDeliveryService), plus a per-rule alert-channel override keyed by Rule.Id. A rule pack can
/// already set its own default channel directly (Rule.AlertChannel); this dictionary is the
/// user-facing override layer on top of that default, the same "pack default + user override"
/// shape #923's RuleOverride already uses for enabled/severity - kept as its own file (rather than
/// folded into rules-overrides.json) since quiet hours has nothing to do with individual rules.
/// </summary>
public sealed class AlertingSettings
{
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Time-of-day the quiet window starts (local time). Default 22:00-07:00 - an
    /// overnight window (QuietHoursStart > QuietHoursEnd) is handled correctly by
    /// AlertDeliveryService's IsWithinQuietHours.</summary>
    public TimeSpan QuietHoursStart { get; set; } = new TimeSpan(22, 0, 0);

    public TimeSpan QuietHoursEnd { get; set; } = new TimeSpan(7, 0, 0);

    /// <summary>Rule.Id -> channel override. A rule id with no entry here just uses its own
    /// Rule.AlertChannel (default Toast).</summary>
    public Dictionary<string, AlertChannel> RuleChannelOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
