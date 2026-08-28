using System.Text;

namespace TaskManagerPlus.Models;

/// <summary>
/// Data shapes for the Responsiveness tab (suggestions.md #201-214) - DPC/ISR-by-driver rows,
/// per-core DPC/interrupt/queue rows, spike events with foreground-app context, driver identity
/// info, and a start/stop measurement-session summary. See DpcLatencyService for how these are
/// populated and PerCoreDpcService for the per-core rows.
/// </summary>

/// <summary>One driver's aggregated DPC stats for the current sampling window (#201), including
/// the timer-vs-device split (#208) and the joined identity (#211) / known-offender hint (#212).
/// A missing identity join (Version/DriverDate/Provider/Signer all empty) is expected, not a bug -
/// see DriverIdentityService's remarks on why the driverquery/pnputil join is best-effort.</summary>
public sealed class DriverDpcRow
{
    public string DriverName { get; init; } = string.Empty;
    public int EventCount { get; init; }
    public double TotalTimeUs { get; init; }
    public double MaxTimeUs { get; init; }
    public double AvgTimeUs => EventCount > 0 ? TotalTimeUs / EventCount : 0;

    // #208: DPCs queued from a hardware ISR vs. an expiring kernel timer.
    public int TimerDpcCount { get; init; }
    public int DeviceDpcCount { get; init; }
    public string TimerVsDeviceText => $"{TimerDpcCount} timer / {DeviceDpcCount} device";

    // #211: joined driver metadata - empty strings when no match was found, never guessed.
    public string Version { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Signer { get; init; } = string.Empty;
    public bool IsOutdated { get; init; }
    public string IdentityText { get; init; } = string.Empty;

    // #212: "usually means..." hint - null when this driver isn't in the small built-in table.
    public string? KnownOffenderHint { get; init; }
}

/// <summary>One driver's aggregated ISR stats (#203) - kept as a separate row type from
/// DriverDpcRow deliberately: a driver can be fine in DPC and terrible in ISR (or vice versa).</summary>
public sealed class DriverIsrRow
{
    public string DriverName { get; init; } = string.Empty;
    public int Count { get; init; }
    public double TotalTimeUs { get; init; }
    public double MaxTimeUs { get; init; }
    public double AvgTimeUs => Count > 0 ? TotalTimeUs / Count : 0;
}

/// <summary>A single DPC/ISR spike above the configured threshold, stamped with the foreground
/// window/process at that moment (#209) - "quick flag, not a verdict": a correlation, not proof
/// the foreground app caused the spike.</summary>
public sealed class DpcSpikeEvent
{
    public DateTime Timestamp { get; init; }
    public string DriverName { get; init; } = string.Empty;
    public double DurationUs { get; init; }
    public string Kind { get; init; } = string.Empty; // "DPC" or "ISR"
    public string ForegroundContext { get; init; } = string.Empty;
}

/// <summary>One logical core's DPC/interrupt-time percentage for the last sample interval (#205).</summary>
public sealed class CoreDpcRow
{
    public int CoreIndex { get; init; }
    public double DpcPercent { get; init; }
    public double InterruptPercent { get; init; }
}

/// <summary>One logical core's DPC queue depth/rate (#206) - a high queue with low DPC time
/// points at an interrupt storm rather than a slow driver.</summary>
public sealed class CoreDpcQueueRow
{
    public string CoreLabel { get; init; } = string.Empty;
    public double DpcsQueuedPerSec { get; init; }
    public double DpcRate { get; init; }
}

/// <summary>Joined driver metadata from driverquery /v /fo csv + pnputil /enum-drivers (#211) -
/// see DriverIdentityService's remarks for the best-effort matching this is built from.</summary>
public sealed class DriverIdentityInfo
{
    public string FileName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Signer { get; init; } = string.Empty;
    public string InfName { get; init; } = string.Empty;
    public bool IsOutdated { get; init; }
}

/// <summary>DPC watchdog headroom info (#204), read from the registry - see DpcWatchdogService.</summary>
public sealed class DpcWatchdogInfo
{
    public bool WatchdogEnabled { get; init; }
    public int? TimeoutValue { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>One driver's min/avg/max/p99 DPC time over a completed measurement session (#213).</summary>
public sealed class DriverSessionStat
{
    public string DriverName { get; init; } = string.Empty;
    public double MinUs { get; init; }
    public double AvgUs { get; init; }
    public double MaxUs { get; init; }
    public double P99Us { get; init; }
}

/// <summary>Start/Stop measurement session summary (#213) - scopes min/avg/max/p99 per driver plus
/// total DPC time as a percentage of wall clock to the window a user actually measured, rather than
/// reading whole-uptime averages.</summary>
public sealed class MeasurementSessionSummary
{
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public double TotalDpcTimeUs { get; init; }
    public double DpcTimePercentOfWallClock { get; init; }
    public List<DriverSessionStat> PerDriver { get; init; } = new();

    /// <summary>Plain-text rendering for the "Copy summary" button.</summary>
    public string ToSummaryText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Responsiveness measurement — started {StartedAt:g}, duration {Duration:mm\\:ss}");
        sb.AppendLine($"Total DPC time: {TotalDpcTimeUs / 1000.0:0.##} ms ({DpcTimePercentOfWallClock:0.###}% of wall clock)");
        sb.AppendLine();
        if (PerDriver.Count == 0)
        {
            sb.AppendLine("(no DPC events were captured/parsed during this session)");
        }
        else
        {
            sb.AppendLine($"{"Driver",-32} {"Min(us)",8} {"Avg(us)",8} {"Max(us)",8} {"P99(us)",8}");
            foreach (var d in PerDriver.OrderByDescending(d => d.MaxUs))
                sb.AppendLine($"{Truncate(d.DriverName, 32),-32} {d.MinUs,8:0.#} {d.AvgUs,8:0.#} {d.MaxUs,8:0.#} {d.P99Us,8:0.#}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
