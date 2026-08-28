namespace TaskManagerPlus.Services;

/// <summary>
/// #455: a tiny process-lifetime cache letting SummaryViewModel's Health Check card show "N
/// unsigned/test-signed drivers found" without SummaryViewModel taking a dependency on
/// DevicesDriversViewModel or forcing an eager background scan at startup. The Devices &amp; Drivers
/// tab is deliberately on-demand (CLAUDE.md: expensive sweeps stay behind an explicit action, never
/// a timer) - this static holder is how its result reaches the Summary tab without violating that:
/// HasScanned stays false (and the Health Check rule stays silent) until the user has actually
/// opened the tab and run a signature check at least once this session, and the count is simply
/// whatever the most recent check found, session-lifetime only (never persisted - a stale count
/// from a previous session would be misleading rather than helpful).
/// </summary>
public static class DriverSignatureSummaryState
{
    public static bool HasScanned { get; private set; }
    public static int UnsignedOrTestSignedCount { get; private set; }

    public static void Report(int unsignedOrTestSignedCount)
    {
        HasScanned = true;
        UnsignedOrTestSignedCount = unsignedOrTestSignedCount;
    }
}
