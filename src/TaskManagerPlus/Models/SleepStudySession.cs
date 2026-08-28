namespace TaskManagerPlus.Models;

/// <summary>
/// #649/#650: one session row from `powercfg /sleepstudy` (a.k.a. `/systempowerreport`) - Windows'
/// own connected-standby/system-power diagnostic report, covering roughly the last three days of
/// sleep/resume transitions. Microsoft does not publish a versioned schema for this report's XML at
/// all (a step beyond the "undocumented but stable field names" caveat BatteryReportService's own
/// remarks already carry for `/batteryreport`) - every field here is populated via a deliberately
/// loose, local-name-contains scan (see SleepStudyService), and any session or field this parser
/// can't confidently recognize is left null/empty rather than guessed. When nothing recognizable is
/// found at all, the list stays empty and the Sleep panel shows a plain status note instead of a
/// broken table.
/// </summary>
public sealed class SleepStudySession
{
    public DateTime? Start { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Percentage of the session spent in the low-power/connected-standby state (as
    /// opposed to briefly active/working) - null when the report didn't expose a recognizable
    /// active/idle time split for this session.</summary>
    public double? LowPowerPercent { get; init; }

    /// <summary>Battery percentage-points lost over the session, when the report exposed an
    /// energy/charge delta for it. Null on AC-only sessions or when the report didn't expose one.</summary>
    public double? DrainPercent { get; init; }

    public List<SleepStudyOffender> TopOffenders { get; init; } = new();
}

/// <summary>One driver/device/process named as active (kept the SoC from reaching its lowest power
/// state) during a sleepstudy session, or - in the #650 aggregated ranking - across every parsed
/// session this report covered.</summary>
public sealed class SleepStudyOffender
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Best-effort category text as the report itself labeled it (e.g. "DRIVER",
    /// "SERVICE", "APPLICATION") - empty when the report didn't expose one.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>#650: how many of the parsed sessions this name appeared as an offender in - the
    /// single most actionable number from the aggregated ranking (a driver present in most
    /// sessions is the one actually worth investigating, which a single-session view buries).</summary>
    public int SessionCount { get; set; }
}
