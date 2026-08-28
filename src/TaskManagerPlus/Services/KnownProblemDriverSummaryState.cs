namespace TaskManagerPlus.Services;

/// <summary>
/// #500: session-lifetime bridge letting SummaryViewModel's Health Check card show "N known-
/// problem driver(s) found" without depending on DevicesDriversViewModel - the same shape as
/// DriverSignatureSummaryState (#455)/DeviceProblemSummaryState (#470). Stays silent
/// (HasScanned false) until the Devices &amp; Drivers tab's own known-problem-driver scan has run at
/// least once this session.
/// </summary>
public static class KnownProblemDriverSummaryState
{
    public static bool HasScanned { get; private set; }
    public static int MatchCount { get; private set; }

    public static void Report(int matchCount)
    {
        HasScanned = true;
        MatchCount = matchCount;
    }
}
