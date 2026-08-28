using System.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #963-#965: the one place an alert (one of SummaryViewModel's three fixed threshold alerts, or a
/// rule-engine finding that just newly fired) gets delivered and logged. Always appends to
/// alerts-history.jsonl via AlertHistoryService (#963) regardless of whether anything was actually
/// shown; applies #964's quiet-hours window to toast/balloon delivery only (the log line is
/// written either way) and #964's per-rule channel override on top of the rule's own default
/// channel; and applies #965's repeat-escalation, which forces delivery through (even over a
/// SilentLogOnly channel) and bumps the logged severity one level once a rule has fired
/// EscalateAfterRepeats times within EscalateWindowSeconds.
/// </summary>
public static class AlertDeliveryService
{
    public static void Deliver(string ruleId, string title, string message, RuleSeverity severity, AlertChannel defaultChannel,
        int? escalateAfterRepeats, int? escalateWindowSeconds)
    {
        var settings = AlertingSettingsService.Load();

        // #964: a per-rule override (set from the Background Health panel) takes precedence over
        // the rule pack's own default channel - case-insensitive lookup regardless of whether the
        // dictionary that came back off System.Text.Json kept the OrdinalIgnoreCase comparer the
        // in-memory default was constructed with.
        var overrideEntry = settings.RuleChannelOverrides
            .FirstOrDefault(kv => string.Equals(kv.Key, ruleId, StringComparison.OrdinalIgnoreCase));
        var channel = overrideEntry.Key is not null ? overrideEntry.Value : defaultChannel;

        // #965: escalation - count how many times this rule already fired within its window
        // (not counting the alert about to be logged), and force delivery through if this one
        // would push it to/over the threshold.
        bool escalated = false;
        if (escalateAfterRepeats is { } repeats && repeats > 0 && escalateWindowSeconds is { } windowSeconds && windowSeconds > 0)
        {
            int recentCount = AlertHistoryService.CountRecent(ruleId, TimeSpan.FromSeconds(windowSeconds));
            if (recentCount + 1 >= repeats) escalated = true;
        }

        var effectiveChannel = escalated ? AlertChannel.Toast : channel;
        var effectiveSeverity = escalated ? BumpSeverity(severity) : severity;

        bool withinQuietHours = IsWithinQuietHours(settings);
        bool suppressedByQuietHours = withinQuietHours && effectiveChannel != AlertChannel.SilentLogOnly;

        AlertHistoryService.Append(new AlertHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            RuleId = ruleId,
            Title = title,
            Message = message,
            Severity = effectiveSeverity,
            Channel = effectiveChannel,
            WasSuppressedByQuietHours = suppressedByQuietHours,
            WasEscalated = escalated,
        });

        if (suppressedByQuietHours) return;

        switch (effectiveChannel)
        {
            case AlertChannel.SilentLogOnly:
                return;
            case AlertChannel.TrayBalloon:
                // #964: a genuine Windows balloon tip when the tray icon exists; best-effort
                // fallback to the toast popup (rather than silently dropping the alert) when it
                // doesn't - see TrayBalloonService's remarks.
                if (!TrayBalloonService.TryShowBalloon(title, message, effectiveSeverity == RuleSeverity.High))
                    ToastService.Show(title, message, isCritical: effectiveSeverity == RuleSeverity.High);
                return;
            default:
                ToastService.Show(title, message, isCritical: effectiveSeverity == RuleSeverity.High);
                return;
        }
    }

    private static RuleSeverity BumpSeverity(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Info => RuleSeverity.Low,
        RuleSeverity.Low => RuleSeverity.Medium,
        RuleSeverity.Medium => RuleSeverity.High,
        _ => RuleSeverity.High,
    };

    /// <summary>#964: handles an overnight window (e.g. 22:00-07:00) as well as a same-day one. A
    /// degenerate window (start == end) reads as "no quiet hours" rather than "always quiet" -
    /// almost certainly not what a user setting two identical pickers meant.</summary>
    private static bool IsWithinQuietHours(AlertingSettings settings)
    {
        if (!settings.QuietHoursEnabled) return false;
        var start = settings.QuietHoursStart;
        var end = settings.QuietHoursEnd;
        if (start == end) return false;

        var now = DateTime.Now.TimeOfDay;
        return start < end ? now >= start && now < end : now >= start || now < end;
    }
}
