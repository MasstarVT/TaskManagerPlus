namespace TaskManagerPlus.Models;

/// <summary>
/// #197-199: models backing EventLogHealthService - the per-channel health detail (#197, extending
/// the Events tab's existing channel tree with an on-select detail panel rather than a wholly
/// separate dashboard - see EventsView.xaml's remarks on that choice), retention recommendations
/// with one-click apply (#198), and log-clearing/record-gap detection (#199).
/// </summary>

/// <summary>#197: one channel's full health picture - EventLogConfiguration (enabled, max size, log
/// mode, file path, provider list) next to EventLogInformation (record count, file size, IsLogFull)
/// plus a derived "about how many days of history this log currently holds", read from the oldest
/// record's own timestamp (a single forward-direction record read, not a full-log walk). Any field
/// that couldn't be read (access denied, channel not actually openable) is left null/empty rather
/// than guessed - see IsConfigReadable/IsInfoReadable for which half succeeded.</summary>
public sealed class ChannelHealthInfo
{
    public string ChannelName { get; init; } = string.Empty;

    public bool IsConfigReadable { get; init; }
    public bool? IsEnabled { get; init; }
    public string LogModeText { get; init; } = "Unknown";
    public long? MaxSizeBytes { get; init; }
    public string? FilePath { get; init; }
    public List<string> ProviderNames { get; init; } = new();

    public bool IsInfoReadable { get; init; }
    public long? RecordCount { get; init; }
    public long? FileSizeBytes { get; init; }
    public bool? IsLogFull { get; init; }

    /// <summary>The oldest record's own TimeCreated, read via a single forward-direction record
    /// read - null when the channel has no records, or when reading it failed/timed out.</summary>
    public DateTime? OldestRecordTime { get; init; }

    /// <summary>(Now - OldestRecordTime), in days - "this log currently holds about N days of
    /// history." Null when OldestRecordTime is null (empty channel, or unreadable).</summary>
    public double? EffectiveRetentionDays { get; init; }

    public string? ReadError { get; init; }
}

/// <summary>#198: a retention shortfall found for one channel - offered only when
/// ChannelHealthInfo.EffectiveRetentionDays is shorter than the app's 30-day lookback (and the
/// channel is enabled and its config was readable). SuggestedMaxSizeBytes is a simple doubling of
/// the current configured size (capped), not a precise days-to-bytes projection - stated plainly as
/// an estimate in the confirmation text, not a guaranteed exact day count.</summary>
public sealed class RetentionRecommendation
{
    public string ChannelName { get; init; } = string.Empty;
    public double CurrentRetentionDays { get; init; }
    public long CurrentMaxSizeBytes { get; init; }
    public long SuggestedMaxSizeBytes { get; init; }
    public long AdditionalDiskCostBytes => Math.Max(0, SuggestedMaxSizeBytes - CurrentMaxSizeBytes);

    /// <summary>True for a diagnostic channel that ships disabled - the recommendation is "enable
    /// this" rather than "raise its size".</summary>
    public bool SuggestEnabling { get; init; }
}

/// <summary>#198: what kind of registry-affecting change one EventLogConfigChangeRecord represents -
/// drives which `wevtutil sl` flag was used and which one reverts it.</summary>
public enum EventLogConfigChangeType
{
    MaxSize,
    Enabled,
}

/// <summary>#198: one change this app made to a channel's configuration via `wevtutil sl` -
/// persisted (event-log-config.json) so the app can report what it changed and revert it, even
/// after a restart. PreviousValue/NewValue are stored as plain strings (a byte count or "True"/
/// "False") since the two ChangeTypes need different value shapes and this file has no other reader
/// that needs them strongly typed.</summary>
public sealed class EventLogConfigChangeRecord
{
    public string ChannelName { get; set; } = string.Empty;
    public EventLogConfigChangeType ChangeType { get; set; }
    public string PreviousValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>event-log-config.json root - same shape as every other settings file in this app (a
/// plain serializable object, silent-fallback-to-defaults on a missing/corrupt file).</summary>
public sealed class EventLogConfigSettings
{
    public List<EventLogConfigChangeRecord> Changes { get; set; } = new();
}

/// <summary>#199: one System 104 ("the &lt;log&gt; log was cleared") or Security 1102 ("the audit
/// log was cleared") event - flagged prominently rather than silently folded into "no problems
/// found," since a cleared log can hide exactly the evidence a triage session is looking for.</summary>
public sealed class LogClearEvent
{
    public DateTime TimeCreated { get; init; }

    /// <summary>The channel the 104/1102 record itself lives in ("System" or "Security") - always
    /// one of the two, since only those channels log a clear this way.</summary>
    public string SourceChannel { get; init; } = string.Empty;

    /// <summary>Best-effort name of the log that was actually cleared, parsed from event 104's own
    /// inserted properties/message text ("Application"/"System"/... for 104; always "Security" for
    /// 1102, which has no such property since it only ever describes itself).</summary>
    public string ClearedChannelName { get; init; } = string.Empty;

    /// <summary>The account that cleared it, resolved via EventLogExplorerService.
    /// ResolveUserAccount - "Unknown" when the record carried no SID (shouldn't normally happen for
    /// these two events, but degrades rather than guessing).</summary>
    public string Account { get; init; } = "Unknown";
}

/// <summary>#199: a record-ID discontinuity or a suspiciously large time gap found within the most
/// recently read page of one channel - "quick flag, not a verdict": a missing range of record IDs
/// or an outsized gap between consecutive records' timestamps usually means the log rotated/was
/// truncated, but a channel that simply logs rarely can show a wide legitimate gap too.</summary>
public sealed class LogGapFlag
{
    public string ChannelName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime? FromTime { get; init; }
    public DateTime? ToTime { get; init; }
}
