namespace TaskManagerPlus.Models;

/// <summary>Round 18, #367: one StorPort request whose duration exceeded the capture's threshold -
/// shape is in place for when a full ETW session is wired up; see StorPortTraceService's remarks
/// for why this chunk ships the capture path as a labeled stub rather than a live session.</summary>
public sealed class StorPortLatencyEvent
{
    public DateTime TimestampUtc { get; init; }
    public string Device { get; init; } = string.Empty;
    public double DurationMs { get; init; }
    public long IoSizeBytes { get; init; }

    /// <summary>"Read"/"Write"/"Unknown" - StorPort event 10/11 carries this, but it's not
    /// populated by anything in this chunk (see StorPortTraceService's remarks).</summary>
    public string Direction { get; init; } = string.Empty;
}

public sealed class StorPortTraceResult
{
    public bool Available { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public List<StorPortLatencyEvent> Events { get; init; } = new();
}
