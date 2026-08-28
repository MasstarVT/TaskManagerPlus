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

    // #216: best-effort device attribution - see DeviceInterruptAttributionService. Blank when no
    // device could be matched to this driver file, never a guess.
    public string DeviceName { get; init; } = string.Empty;
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

    // #216: best-effort device attribution - see DeviceInterruptAttributionService.
    public string DeviceName { get; init; } = string.Empty;
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

/// <summary>#215: one logical core's interrupt rate for the last sample interval, plus whether it's
/// flagged as a suspected storm relative to its siblings/an absolute ceiling - see
/// PerCoreDpcService.SampleInterruptStorm's remarks. "Quick flag, not a verdict."</summary>
public sealed class CoreInterruptRow
{
    public int CoreIndex { get; init; }
    public double InterruptsPerSec { get; init; }
    public bool IsSuspectedStorm { get; init; }
}

/// <summary>#217: one IRQ line and the device(s) allocated to it, via Win32_PnPAllocatedResource /
/// Win32_IRQResource / Win32_PnPEntity - see IrqResourceService. Flags lines with 3+ sharers, since
/// two devices peacefully sharing a level-triggered PCI IRQ is normal; three or more is the
/// classic "worth a second look" case.</summary>
public sealed class IrqShareRow
{
    public int IrqNumber { get; init; }
    public List<string> DeviceNames { get; init; } = new();
    public int SharerCount => DeviceNames.Count;
    public bool IsHeavilyShared => SharerCount >= 3;
    public string DevicesText => string.Join(", ", DeviceNames);
}

/// <summary>#218/#219: one device's MSI/MSI-X status and interrupt-affinity policy, read from its
/// `Device Parameters\Interrupt Management` registry subtree - see InterruptManagementService.
/// Combined into one row/one device enumeration since both facts live under the same subtree.
/// "Quick flag, not a verdict": IsHighTrafficClass is a name/class heuristic, not a confirmed
/// device category.</summary>
public sealed class DeviceInterruptRow
{
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceClass { get; init; } = string.Empty;

    // #218
    public bool? MsiSupported { get; init; }
    public int? MessageNumberLimit { get; init; }
    public string MsiStatusText => MsiSupported switch
    {
        true => MessageNumberLimit is > 0 ? $"MSI/MSI-X ({MessageNumberLimit} messages)" : "MSI/MSI-X",
        false => "Line-based",
        null => "Unknown",
    };

    /// <summary>#218: a high-traffic-class device (GPU/NIC/NVMe/USB controller, matched by class/
    /// name heuristic) still running line-based interrupts instead of MSI/MSI-X - worth a manual
    /// check, not a confirmed misconfiguration.</summary>
    public bool IsHighTrafficClass { get; init; }
    public bool IsLineBasedHighTraffic => IsHighTrafficClass && MsiSupported == false;

    // #219
    public int? DevicePolicy { get; init; }
    public string DevicePolicyText { get; init; } = "Unknown";
    public string? AssignmentSetOverride { get; init; }
    public int? DevicePriority { get; init; }
    public string DevicePriorityText { get; init; } = "Unknown";
    public string AffinityText => AssignmentSetOverride is { Length: > 0 } o
        ? $"{DevicePolicyText} — cores {o}"
        : DevicePolicyText;
}

/// <summary>#220: one row of the "Platform latency settings" card - a small, generically-named
/// ObservableCollection<PlatformLatencySettingRow> (see ResponsivenessViewModel) that later chunks
/// in this same domain are expected to append more rows to, per suggestions.md's own framing.</summary>
public sealed class PlatformLatencySettingRow
{
    public string SettingName { get; init; } = string.Empty;
    public string ValueText { get; init; } = "Unknown";
    public string? Note { get; init; }
}

/// <summary>#222: Wi-Fi background-scan-storm suspected-cause result - see WifiScanStormService.
/// Null/false fields mean "couldn't tell" or "not detected", never a guess.</summary>
public sealed class WifiScanStormResult
{
    public bool Detected { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string? AdapterName { get; init; }
    public int RecentScanEventCount { get; init; }
    public bool IsOnEthernet { get; init; }
}

/// <summary>#223: one device instance's arrive/remove churn count over the scanned window - see
/// EventLogService.ReadUsbChurnEvents.</summary>
public sealed class UsbChurnRow
{
    public string DeviceInstanceId { get; init; } = string.Empty;
    public string DeviceDescription { get; init; } = string.Empty;
    public int EventCount { get; init; }
    public DateTime LastEvent { get; init; }
}

/// <summary>#224: one Device-Manager-visible "problem device" - Win32_PnPEntity with a nonzero
/// ConfigManagerErrorCode, decoded via a small table of the most common codes - see
/// ProblemDeviceService.</summary>
public sealed class ProblemDeviceRow
{
    public string Name { get; init; } = string.Empty;
    public int ConfigManagerErrorCode { get; init; }
    public string ErrorText { get; init; } = string.Empty;
}

/// <summary>#225/#228/#234: current system timer resolution (NtQueryTimerResolution) plus the
/// derived wake-ups/sec and a threshold-based timer-coalescing inference - see
/// TimerResolutionService.Read's remarks. All-zero/empty StatusText-only means the read failed;
/// callers should show StatusText rather than the zeroed numeric fields in that case.</summary>
public sealed class TimerResolutionInfo
{
    public double CurrentMs { get; init; }

    /// <summary>NtQueryTimerResolution's "MinimumTime" - the finest (best/shortest) interval this
    /// system can be raised to.</summary>
    public double FinestMs { get; init; }

    /// <summary>NtQueryTimerResolution's "MaximumTime" - the coarsest (worst/longest) interval,
    /// i.e. the un-raised default for this system.</summary>
    public double CoarsestMs { get; init; }

    public double WakeupsPerSec { get; init; }

    /// <summary>#225: true when CurrentMs is meaningfully below the ~15.6ms Windows default -
    /// a permanently raised resolution is both a battery drain and a hint some app is busy-waiting.</summary>
    public bool IsRaised { get; init; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary>#234: plain-English, explicitly-labeled inference (not a direct API/tool read -
    /// none exists) of whether timer coalescing is likely defeated at the current resolution.</summary>
    public string CoalescingInferenceText { get; init; } = string.Empty;
}

/// <summary>#228: QPC frequency plus a short drift measurement against
/// GetSystemTimePreciseAsFileTime - see TimerResolutionService.CheckQpcDriftAsync's remarks. Null
/// on the ResponsivenessViewModel until the on-demand "Check QPC" button has been pressed at least
/// once.</summary>
public sealed class QpcDriftResult
{
    public long FrequencyHz { get; init; }
    public double DriftPpm { get; init; }
    public bool LooksStable { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#226: one process (or best-effort name) identified as holding/having held an
/// outstanding raised-timer-resolution request - either from a powercfg /energy report's
/// "Platform Timer Resolution" finding, or a best-effort name scan of the undocumented
/// GlobalTimerResolutionRequests registry value. See PowerReportService's remarks for both
/// sources.</summary>
public sealed class TimerResolutionRequesterRow
{
    public string ProcessName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#229: one Error/Warning row parsed from a powercfg /energy HTML report's
/// Errors/Warnings table - see PowerReportService.ParseFindings' remarks. A tolerant, best-effort
/// scrape: a Windows-build HTML layout change means fewer/no findings parse, never a fabricated
/// row.</summary>
public sealed class EnergyReportFinding
{
    public string Severity { get; init; } = string.Empty; // "Error" or "Warning"
    public string Description { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#230: one outstanding power request from `powercfg /requests` - Type is the section
/// (DISPLAY/SYSTEM/AWAYMODE/EXECUTION/PERFBOOST) and Holder is the process/service/driver name the
/// tool's own text output already names, plus any reason text it gave. An EXECUTION or PERFBOOST
/// request held forever changes scheduling/idle behavior and is otherwise completely invisible.</summary>
public sealed class PowerRequestRow
{
    public string Type { get; init; } = string.Empty;
    public string Holder { get; init; } = string.Empty;
}

/// <summary>#231: one top "activator" row from a powercfg /sleepstudy (or /systemsleepdiagnostics
/// fallback) HTML report - the component that kept the system from idling during modern standby.
/// Same tolerant best-effort HTML scrape as #229's findings - see
/// PowerReportService.ParseActivators' remarks.</summary>
public sealed class SleepStudyActivatorRow
{
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#235/#236/#243/#244: one top-level window from the always-on hung-window scan, with
/// its message-pump round-trip time (#236, refreshed on its own slower cadence - see
/// HungWindowService.RunProbeCycleAsync) and, for a currently-hung window only, a best-effort
/// wait-reason hint (#243) and cross-process chain guess (#244). Genuinely per-window, not
/// per-process (unlike ProcessRow.NotRespondingSeconds) - a multi-window app can have one hung
/// window and several fine ones. "Quick flag, not a verdict" for WaitHintText/ChainText: a wait
/// reason isn't a full stack trace, and the chain guess is a best-effort kernel-object-sharing
/// match, not a confirmed deadlock analysis.
///
/// Mutable/INotifyPropertyChanged (like ProcessRow), keyed by Hwnd, and merged in place each light
/// tick (ResponsivenessViewModel.MergeHungWindows) rather than cleared and rebuilt - the same
/// "preserve DataGrid selection/scroll position" reasoning CLAUDE.md documents for ProcessRow,
/// which matters here specifically so #242's right-click "Create dump" selection survives the ~2s
/// gap until the next tick.</summary>
public sealed class HungWindowRow : TaskManagerPlus.Common.ObservableObject
{
    public IntPtr Hwnd { get; init; }
    public int Pid { get; init; }
    public int ThreadId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;

    private bool _isHung;
    public bool IsHung { get => _isHung; set => SetProperty(ref _isHung, value); }

    /// <summary>#236: last known SendMessageTimeout round-trip, in milliseconds - null until the
    /// probe cycle has measured this window at least once. 250 (the capped timeout) means either a
    /// genuinely slow response or a hung window that never returned inside the cap.</summary>
    private double? _responseMs;
    public double? ResponseMs
    {
        get => _responseMs;
        set { if (SetProperty(ref _responseMs, value)) OnPropertyChanged(nameof(ResponseMsText)); }
    }
    public string ResponseMsText => ResponseMs.HasValue ? $"{ResponseMs.Value:0}" : "—";

    /// <summary>#237: how long this window has been continuously hung so far, for the live grid -
    /// distinct from the persisted HangLogEntry.DurationSeconds, which is only written once the
    /// window recovers.</summary>
    private TimeSpan? _hungFor;
    public TimeSpan? HungFor
    {
        get => _hungFor;
        set { if (SetProperty(ref _hungFor, value)) OnPropertyChanged(nameof(HungForText)); }
    }
    public string HungForText => HungFor is { } h ? $"{h.TotalSeconds:0}s" : string.Empty;

    /// <summary>#243: plain-English decode of the window's owning thread's ThreadState/WaitReason/
    /// StartAddress - see HungWindowService.DescribeWaitState. Null for a window that isn't
    /// currently hung, or when the thread couldn't be found/read.</summary>
    private string? _waitHintText;
    public string? WaitHintText { get => _waitHintText; set => SetProperty(ref _waitHintText, value); }

    /// <summary>#244: best-effort "X is waiting on Y" guess, resolved off-thread and cached per
    /// window (not recomputed every probe cycle) - see HungWindowService.ResolveHangChain. Null
    /// until resolved, or when nothing could be determined.</summary>
    private string? _chainText;
    public string? ChainText { get => _chainText; set => SetProperty(ref _chainText, value); }
}

/// <summary>#238: one app's ranked foreground-stall history - how many times, and how badly, a
/// window belonging to this process stalled past the configured threshold while it was the
/// foreground app. Fed by the same #236 probe loop, gated to "was this app in the foreground at
/// the moment of the probe" via the #238 SetWinEventHook. "Quick flag, not a verdict."</summary>
public sealed class ForegroundStallRow
{
    public string ProcessName { get; init; } = string.Empty;
    public int StallCount { get; set; }
    public double MaxStallMs { get; set; }
    public double TotalStallMs { get; set; }
    public DateTime LastStall { get; set; }
    public double AvgStallMs => StallCount > 0 ? TotalStallMs / StallCount : 0;
}

/// <summary>#241: hang-timeout registry audit - reused via the existing PlatformLatencySettingRow
/// shape (see HangTimeoutRegistryService), appended to ResponsivenessViewModel.PlatformLatencySettings
/// alongside the #220/#227/#232 rows already there.</summary>

/// <summary>#245: desktop heap sizes (from SharedSection) plus session-wide USER/GDI handle totals
/// summed across the process list ProcessesViewModel already polls (ProcessRow.GdiHandleCount/
/// UserHandleCount, Round 7 #7) - no new per-process syscall needed. Desktop-heap exhaustion
/// presents as "windows stop drawing / nothing opens" rather than high CPU, so this is a quick
/// flag worth surfacing even though Windows exposes no direct "heap usage" counter to compare
/// against - only the configured size and the session's own handle totals against the documented
/// 10,000/65,536 USER/GDI session limits.</summary>
public sealed class DesktopHeapInfo
{
    public int? InteractiveHeapKb { get; init; }
    public int? NoninteractiveHeapKb { get; init; }
    public string StatusText { get; init; } = string.Empty;

    public int TotalUserHandles { get; init; }
    public int TotalGdiHandles { get; init; }
    public const int UserHandleSessionLimit = 10_000;
    public const int GdiHandleSessionLimit = 65_536;
    public double UserHandlePercent => Math.Clamp(TotalUserHandles / (double)UserHandleSessionLimit * 100.0, 0, 100);
    public double GdiHandlePercent => Math.Clamp(TotalGdiHandles / (double)GdiHandleSessionLimit * 100.0, 0, 100);
    public bool IsNearLimit => UserHandlePercent > 75 || GdiHandlePercent > 75;
}

/// <summary>#246: one shell-related window's message-pump round-trip time (reusing #236's probe
/// logic against just Shell_TrayWnd/Progman/explorer.exe's own top-level frames) - see
/// ShellResponsivenessService.</summary>
public sealed class ShellResponsivenessRow
{
    public string WindowName { get; init; } = string.Empty;
    public double? ResponseMs { get; init; }
    public string ResponseMsText => ResponseMs.HasValue ? $"{ResponseMs.Value:0} ms" : "—";
}
