namespace TaskManagerPlus.Services;

/// <summary>
/// #470: a tiny process-lifetime cache letting SummaryViewModel's Health Check card show "N
/// devices showing a problem code" without SummaryViewModel taking a dependency on
/// DevicesDriversViewModel or forcing an eager device-tree scan at startup - the exact same shape
/// as DriverSignatureSummaryState (#455), which this mirrors. The Devices &amp; Drivers tab's device
/// tree is on-demand (CLAUDE.md: expensive sweeps stay behind an explicit action), so HasScanned
/// stays false (and the Health Check rule stays silent) until the user has actually opened the tab
/// and loaded the device tree at least once this session. Session-lifetime only - never persisted,
/// since a stale count from a previous session would be misleading rather than helpful.
/// </summary>
public static class DeviceProblemSummaryState
{
    public static bool HasScanned { get; private set; }
    public static int ProblemDeviceCount { get; private set; }

    public static void Report(int problemDeviceCount)
    {
        HasScanned = true;
        ProblemDeviceCount = problemDeviceCount;
    }
}
