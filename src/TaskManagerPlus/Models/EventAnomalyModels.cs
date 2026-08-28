namespace TaskManagerPlus.Models;

/// <summary>
/// #127-134/#136: models backing the Events tab's "Anomaly detection" deep-scan panel and the
/// Stability tab's "New error types this week" card / error-density heatmap. Every row here is
/// produced by EventAnomalyDetectionService from an explicit, button-gated scan (never a
/// DispatcherTimer) and is explicitly a heuristic/statistical flag, not a diagnosis - CLAUDE.md's
/// "quick flag, not a verdict" convention applies to this whole family of features, same as the
/// #117 knowledge base.
/// </summary>

/// <summary>#127: one (provider, eventId) signature whose last-24-hour count is unusual relative to
/// its own historical median - a robust (median-absolute-deviation-based) per-signature baseline,
/// not a fixed threshold, since a PC's normal log noise is highly individual (a chatty driver's
/// "normal" might be another PC's five-alarm fire). Presented as "unusual for this PC," never
/// "bad" - a high-count ID can be perfectly healthy background noise for one machine.</summary>
public sealed class EventIdBaselineFlag
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public int Last24HourCount { get; init; }
    public double MedianDailyCount { get; init; }

    /// <summary>The scaled median-absolute-deviation (a robust stand-in for standard deviation) the
    /// last-24h count was compared against - shown so the flag isn't a black box.</summary>
    public double RobustDeviation { get; init; }

    public int ObservedDays { get; init; }
    public string SampleMessage { get; init; } = string.Empty;
}

/// <summary>#128: a signature that only appears within the recent window of whatever was scanned,
/// with no occurrence in the older portion - the single strongest "something changed" signal in an
/// event log. FirstSeen/SampleMessage let the UI show exactly when it started and what it says.</summary>
public sealed class FirstOccurrenceFlag
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public DateTime FirstSeen { get; init; }
    public int OccurrenceCount { get; init; }
    public string SampleMessage { get; init; } = string.Empty;
}

/// <summary>#129: a run of the same (provider, eventId) signature clustered within a configurable
/// window (e.g. 20+ within 5 minutes), collapsed into one incident - so a driver retry storm reads
/// as one row instead of flooding the grid. Rows carries the original member records for the
/// expandable detail view; never mutates or drops them, just groups the presentation.</summary>
public sealed class BurstGroup
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Level { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime FirstTime { get; init; }
    public DateTime LastTime { get; init; }
    public string SampleMessage { get; init; } = string.Empty;
    public List<EventRecordRow> Rows { get; init; } = new();

    public TimeSpan Duration => LastTime - FirstTime;
}

/// <summary>#130: one cell of the day x hour-of-day Critical/Error density grid shown under the
/// Stability tab's Reliability History chart. "Day" is the actual chronological calendar date
/// within the scanned window (not a 1-31 day-of-month bucket that folds different months together)
/// so a punch-card-style pattern like "every night at 3 AM" or "only after resume on the 14th"
/// reads clearly - the grid always includes every day/hour combination in range, zero-filled, so
/// the UniformGrid renderer never has to guess at a missing cell.</summary>
public sealed class ErrorDensityHeatmapCell
{
    public DateTime Day { get; init; }
    public int Hour { get; init; }
    public int Count { get; init; }
}

/// <summary>#131: one provider's share of a channel's records within the scanned retention window -
/// "who is actually consuming the log's finite size," the direct answer to "why does my System log
/// only go back 2 days." Built from a lightweight raw-count scan (provider name only, no message/
/// XML formatting) so it stays fast even against a high-volume channel.</summary>
public sealed class ProviderChurnRow
{
    public string Channel { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public int RecordCount { get; init; }
    public double PercentOfTotal { get; init; }
}

/// <summary>#132: a signature whose inter-arrival intervals are near-constant (low coefficient of
/// variation), flagged as a probable retry/restart loop with its measured period - a service
/// restarting every 60 seconds reads very differently from the same total count spread randomly
/// across a day, even though a flat count wouldn't tell them apart.</summary>
public sealed class PeriodicLoopFlag
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public int OccurrenceCount { get; init; }
    public double MeanIntervalSeconds { get; init; }
    public double StdDevSeconds { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>Coefficient of variation (StdDev/Mean) - the lower, the more clock-like the
    /// interval; shown so "how confident is this" isn't hidden inside the flag/no-flag cutoff.</summary>
    public double CoefficientOfVariation => MeanIntervalSeconds > 0 ? StdDevSeconds / MeanIntervalSeconds : 0;

    public string PeriodDescription => MeanIntervalSeconds switch
    {
        < 90 => $"~every {MeanIntervalSeconds:0} sec",
        < 3600 => $"~every {MeanIntervalSeconds / 60:0.0} min",
        _ => $"~every {MeanIntervalSeconds / 3600:0.0} hr",
    };
}

/// <summary>#133: one provider's split between events logged within the first 120 seconds after a
/// boot marker (EventLog 6005 / Kernel-General 12) and everything else - boot-only errors point at
/// driver load order/startup services, steady-state errors point at hardware or a running app.</summary>
public sealed class BootErrorProfileRow
{
    public string Provider { get; init; } = string.Empty;
    public int BootCount { get; init; }
    public int SteadyStateCount { get; init; }
    public bool IsBootOnly { get; init; }
}

public sealed class BootErrorProfileResult
{
    public List<BootErrorProfileRow> Providers { get; init; } = new();
    public int BootMarkersFound { get; init; }
}

/// <summary>#134: one event signature that either newly appeared after a user-chosen "known good"
/// cutoff date, or stopped appearing after it - Timestamp is the first occurrence after the cutoff
/// for a "new" row, or the last occurrence before it for a "stopped" row (labelled contextually by
/// whichever list the UI is showing).</summary>
public sealed class EventSignatureDiffRow
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public DateTime Timestamp { get; init; }
    public string SampleMessage { get; init; } = string.Empty;
}

public sealed class SinceWorkingDiffResult
{
    public List<EventSignatureDiffRow> NewSignatures { get; init; } = new();
    public List<EventSignatureDiffRow> StoppedSignatures { get; init; } = new();
}

/// <summary>#136: one pinned event signature on the live watchlist - persisted to
/// event-watchlist.json (EventWatchlistSettingsService). Channel+Provider+EventId together are the
/// match key; Label is a free-text user note shown instead of the bare IDs when set.</summary>
public sealed class WatchlistEntry
{
    public string Channel { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string? Label { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{Provider} {EventId}" : Label;
}

/// <summary>event-watchlist.json root - same shape as EventFilterSettings/PollIntervalSettings.</summary>
public sealed class EventWatchlistSettings
{
    public List<WatchlistEntry> Entries { get; set; } = new();
}
