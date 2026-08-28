namespace TaskManagerPlus.Services;

/// <summary>
/// #500 cross-reference support: a tiny process-lifetime cache letting #500's known-problem-driver
/// matcher (and, transitively, the Summary Health Check card) see the Stability tab's own
/// FaultingModule data without taking a dependency on StabilityViewModel or forcing an eager event-
/// log scan at startup - the exact same shape as DeviceProblemSummaryState (#470)/
/// DriverSignatureSummaryState (#455), which this mirrors. The Stability tab is on-demand (CLAUDE.md:
/// event-log scans stay behind an explicit action), so HasScanned stays false until the user has
/// actually opened it and run a refresh at least once this session - #500's matcher treats that the
/// same way #455/#470's Health Check rules already do: stay silent rather than imply "nothing found"
/// when nothing was actually looked at.
/// </summary>
public static class StabilityCrashSummaryState
{
    public static bool HasScanned { get; private set; }
    public static IReadOnlyList<string> FaultingModuleNames { get; private set; } = Array.Empty<string>();

    public static void Report(IEnumerable<string> faultingModuleNames)
    {
        HasScanned = true;
        FaultingModuleNames = faultingModuleNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
