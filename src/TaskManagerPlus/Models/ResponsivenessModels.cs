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

/// <summary>
/// #247/#248/#254: one DwmGetCompositionTimingInfo sample - an in-box, no-ETW view of whether the
/// compositor is keeping up, plus the per-second dropped/missed deltas since the previous sample
/// (#248) - see DwmCompositionService.Sample's remarks. IsAvailable is false (StatusText explains
/// why) when the API call itself fails - composition disabled, a remote-desktop session, or a
/// cbSize mismatch against this Windows build's actual DWM_TIMING_INFO layout - never a guessed
/// number in that case.
/// </summary>
public sealed class DwmCompositionInfo
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;

    public double RefreshRateHz { get; init; }

    /// <summary>The compositor's own effective frame time - qpcRefreshPeriod converted to
    /// milliseconds via QueryPerformanceFrequency, falling back to 1000/RefreshRateHz when the QPC
    /// period field isn't populated on this Windows build.</summary>
    public double CompositionFrameTimeMs { get; init; }

    public ulong FramesDisplayed { get; init; }
    public ulong FramesDropped { get; init; }
    public ulong FramesMissed { get; init; }
    public uint FramesLate { get; init; }
    public uint FramesOutstanding { get; init; }

    /// <summary>#248: dropped+missed frames per second since the previous sample - 0 on the first
    /// sample of a session (no previous counters to diff against yet).</summary>
    public double DroppedMissedPerSec { get; init; }
}

/// <summary>#254: HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers HwSchMode/TdrDelay/TdrLevel
/// - see DwmCompositionService.ReadHardwareScheduling. A missing HwSchMode value means "unsupported
/// on this Windows build/driver", not "disabled" - shown as Unknown, never guessed as either state.</summary>
public sealed class HardwareSchedulingInfo
{
    public int? HwSchModeRaw { get; init; }
    public string HwSchModeText { get; init; } = "Unknown";
    public int? TdrDelaySeconds { get; init; }
    public string TdrDelayText => TdrDelaySeconds.HasValue ? $"{TdrDelaySeconds} sec" : "Unknown (using Windows' default, ~2 sec)";
    public int? TdrLevel { get; init; }
    public string TdrLevelText { get; init; } = "Unknown";
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#249: p50/p99/max DwmFlush()-to-DwmFlush() interval (the vblank period, with jitter) -
/// see VBlankJitterService. Empty (SampleCount == 0) until the Start/Stop probe has collected at
/// least one interval.</summary>
public sealed class VBlankJitterSnapshot
{
    public int SampleCount { get; init; }
    public double P50Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>
/// #250/#251/#252: one app's aggregated present/frame-time stats from a PresentMonitorService
/// capture window - FPS/frame-time (#250) plus 1%-low/0.1%-low/stddev/hitch-count (#251, the
/// numbers that actually correspond to felt stutter, unlike average FPS) and a best-effort
/// present-mode classification (#252, "Unknown" is a legitimate outcome - see
/// PresentMonitorService.ClassifyPresentMode's remarks).
/// </summary>
public sealed class PresentAppRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int FrameCount { get; init; }
    public double AvgFps { get; init; }
    public double AvgFrameTimeMs { get; init; }

    // #251
    public double Low1PercentFps { get; init; }
    public double Low01PercentFps { get; init; }
    public double FrameTimeStdDevMs { get; init; }
    public int HitchCount { get; init; }

    // #252 - "Unknown" when the captured events didn't carry a recognizable present-mode field.
    public string PresentModeText { get; init; } = "Unknown";
    public string PresentModeNote { get; init; } = string.Empty;
}

/// <summary>#259: one long-running GPU queue packet or preemption event from the same DxgKrnl
/// capture #250 uses, named to its owning process - see PresentMonitorService.IngestGpuPacket.
/// "Quick flag, not a verdict": a long packet is a near-miss for a TDR, not a confirmed one - see
/// the Stability tab for actual TDR (GPU driver reset) event history.</summary>
public sealed class GpuStallRow
{
    public DateTime Timestamp { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty; // "Long GPU packet" or "Preemption"
    public double? DurationUs { get; init; }
    public string DurationText => DurationUs.HasValue ? $"{DurationUs.Value / 1000.0:0.##} ms" : "—";
}

/// <summary>#253: one monitor's current vs. maximum-supported refresh rate/colour depth, from
/// EnumDisplayDevices + EnumDisplaySettingsEx - see DisplayModeService.ReadAudit. IsUnderRunning
/// flags a high-refresh panel left running well below what it supports; "quick flag, not a
/// verdict" - some users deliberately cap refresh rate for battery life.</summary>
public sealed class DisplayModeRow
{
    public string MonitorName { get; init; } = string.Empty;
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public int CurrentRefreshHz { get; init; }
    public int MaxRefreshHz { get; init; }
    public int CurrentColorDepthBits { get; init; }
    public bool IsUnderRunning => MaxRefreshHz > 0 && CurrentRefreshHz > 0 && CurrentRefreshHz < MaxRefreshHz * 0.9;
    public string SummaryText => $"{CurrentWidth}x{CurrentHeight} @ {CurrentRefreshHz} Hz (max {MaxRefreshHz} Hz), {CurrentColorDepthBits}-bit";
}

/// <summary>#253: all monitors' display-mode audit plus the mixed-refresh-rate flag across them -
/// see DisplayModeService.ReadAudit.</summary>
public sealed class DisplayModeAudit
{
    public List<DisplayModeRow> Monitors { get; init; } = new();
    public bool MixedRefreshRates => Monitors.Select(m => m.CurrentRefreshHz).Distinct().Count() > 1;
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#255: Game DVR / fullscreen-optimisation registry audit - GameConfigStore/policy/
/// GameBar values plus any app under AppCompatFlags\Layers with the fullscreen-optimization-
/// disabling compatibility token set. See DisplayModeService.ReadGameDvrAudit. Null fields mean
/// "value not present" (using Windows' default), never a guessed true/false.</summary>
public sealed class GameDvrAuditInfo
{
    public bool? GameDvrEnabled { get; init; }
    public bool? GameDvrPolicyDisabled { get; init; }
    public bool? GameBarAutoModeEnabled { get; init; }
    public List<string> FullscreenOptForcedOffApps { get; init; } = new();
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#256/#257: a live input-queue-delay snapshot (p99/max, from comparing each WM_INPUT's
/// arrival against its own GetMessageTime() queue timestamp) plus the derived mouse/keyboard
/// report rate (from the tightest back-to-back raw-input intervals actually observed) and the
/// registry-configured input queue sizes - see InputLatencyService. SampleCount == 0 means the
/// probe hasn't collected anything yet (not started, or no input arrived while running).</summary>
public sealed class InputLatencySnapshot
{
    public int SampleCount { get; init; }
    public double P99DelayMs { get; init; }
    public double MaxDelayMs { get; init; }

    // #257 - null until enough consecutive same-device events have arrived to estimate a rate.
    public double? MouseReportHz { get; init; }
    public double? KeyboardReportHz { get; init; }
    public string MouseQueueSizeText { get; init; } = "Unknown";
    public string KeyboardQueueSizeText { get; init; } = "Unknown";

    public string StatusText { get; init; } = string.Empty;
}

// ================================================================================================
// #260-270 (Scheduler, priority and thread-wait analysis) - see SchedulerService/
// ProcessPowerThrottleService/ProcessPriorityService/Win32PrioritySeparationService/MmcssService
// for how these are populated.
// ================================================================================================

/// <summary>#260: run-queue pressure - System\Processor Queue Length plus a derived, explicitly
/// approximate "ready threads per core" figure (Windows exposes no true per-core ready-queue
/// counter, only this one system-wide value - see ResponsivenessViewModel.SampleLight's remarks).
/// A sustained queue length well above the logical processor count means threads are waiting for
/// CPU even when total CPU% looks fine.</summary>
public sealed class RunQueuePressureInfo
{
    public double ProcessorQueueLength { get; init; }
    public int LogicalProcessorCount { get; init; }
    public double ReadyThreadsPerCoreApprox => LogicalProcessorCount > 0 ? ProcessorQueueLength / LogicalProcessorCount : 0;

    /// <summary>Quick flag, not a verdict - a rough "well above core count" rule of thumb (2x),
    /// not a documented threshold.</summary>
    public bool IsElevated => LogicalProcessorCount > 0 && ProcessorQueueLength > LogicalProcessorCount * 2.0;
}

/// <summary>#261: one state/wait-reason bucket's thread count for a single process - see
/// SchedulerService.BuildWaitBreakdown.</summary>
public sealed class ThreadWaitBreakdownRow
{
    public string BucketName { get; init; } = string.Empty;
    public int ThreadCount { get; init; }
}

/// <summary>#262: one thread ranked by how long it's been continuously waiting - see
/// SchedulerService.RankLongestBlocked. WaitSecondsApprox is built from an approximate clock-tick
/// conversion (SchedulerService.ClockTickMs) - good for ranking/rough duration, not a certified
/// figure.</summary>
public sealed class LongestBlockedThreadRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int ThreadId { get; init; }
    public string WaitReasonText { get; init; } = string.Empty;
    public double WaitSecondsApprox { get; init; }
    public string StartAddressText { get; init; } = string.Empty;

    /// <summary>Blank when the start address couldn't be resolved to any loaded module - never a guess.</summary>
    public string ModuleName { get; init; } = string.Empty;
}

/// <summary>#263: one thread's context-switch rate since the previous sweep - a thread
/// ping-ponging thousands of times a second is the signature of a spin-wait/livelock. A record (not
/// a plain sealed class) so SchedulerService.ResolveTopModules can produce a module-resolved copy
/// via `with` rather than a second constructor path.</summary>
public sealed record ThreadCsRateRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int ThreadId { get; init; }
    public double ContextSwitchesPerSec { get; init; }
    public long StartAddress { get; init; }
    public string ModuleName { get; init; } = string.Empty;

    /// <summary>Quick flag, not a verdict - a rough rate cutoff (2000/sec), not a documented
    /// threshold for "this is spinning".</summary>
    public bool IsSuspectedSpin => ContextSwitchesPerSec >= 2000;
}

/// <summary>#265: one process's share of the system-wide context-switch rate, aggregated from
/// #263's per-thread rates - see SchedulerService.AttributeByProcess.</summary>
public sealed class ContextSwitchAttributionRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double ContextSwitchesPerSec { get; init; }
    public double PercentOfTotal { get; init; }
}

/// <summary>#264: one "possible priority inversion" sample - see
/// SchedulerService.DetectPriorityInversions. Explicitly a sampled inference over a few consecutive
/// ticks, never a traced/proven verdict.</summary>
public sealed class PriorityInversionHint
{
    public string HighPriorityProcess { get; init; } = string.Empty;
    public int HighPriorityThreadId { get; init; }
    public int HighPriority { get; init; }
    public string LowerPriorityProcess { get; init; } = string.Empty;
    public int LowerPriorityThreadId { get; init; }
    public int LowerPriority { get; init; }
    public int ConsecutiveSamples { get; init; }

    public string SummaryText =>
        $"{HighPriorityProcess} (tid {HighPriorityThreadId}, priority {HighPriority}) has been ready-but-not-running for {ConsecutiveSamples} consecutive samples while {LowerPriorityProcess} (tid {LowerPriorityThreadId}, priority {LowerPriority}) keeps running. Possible priority inversion — a sampled pattern, not a traced/proven one.";
}

/// <summary>#269: MMCSS service status plus the SystemResponsiveness/NetworkThrottlingIndex
/// registry values and per-task scheduling profile - see MmcssService.Read.</summary>
public sealed class MmcssAuditInfo
{
    public bool ServiceRunning { get; init; }
    public string ServiceStatusText { get; init; } = "Unknown";
    public int? SystemResponsiveness { get; init; }
    public int? NetworkThrottlingIndex { get; init; }
    public List<MmcssTaskProfileRow> TaskProfiles { get; init; } = new();
    public string StatusText { get; init; } = string.Empty;

    public string SystemResponsivenessText => SystemResponsiveness is { } v ? $"{v}%" : "Not set — Windows default (10% on desktop SKUs)";

    /// <summary>0xFFFFFFFF (-1 as a signed DWORD) means throttling is disabled; absent means
    /// Windows' documented default of 10 packets/ms applies.</summary>
    public string NetworkThrottlingText => NetworkThrottlingIndex switch
    {
        null => "Not set — Windows default (throttles non-multimedia network traffic to ~10 packets/ms while multimedia plays)",
        -1 => "Disabled (no network throttling while multimedia plays)",
        var v => $"{v} packets/ms (Windows default is 10)",
    };
}

/// <summary>#269: one multimedia task's ("Audio"/"Games"/"Pro Audio") MMCSS scheduling profile -
/// see MmcssService.Read. "Unknown" means the value/subkey wasn't present, never a guess.</summary>
public sealed class MmcssTaskProfileRow
{
    public string TaskName { get; init; } = string.Empty;
    public string GpuPriority { get; init; } = "Unknown";
    public string Priority { get; init; } = "Unknown";
    public string SchedulingCategory { get; init; } = "Unknown";
    public string SfioPriority { get; init; } = "Unknown";
}

// ----- #284-293: Background-activity ribbon (background task interference) ------------------

/// <summary>#284: whether a known background workload looks active right now. Unknown is
/// distinct from Inactive - it means the underlying source (a WMI namespace, a service, a
/// registry key) couldn't be read at all, per CLAUDE.md's "degrade to Unknown, never fabricate"
/// rule, not that the workload was checked and found idle.</summary>
public enum BackgroundActivityState { Unknown, Inactive, Active }

/// <summary>#284: one row in the background-activity ribbon - the container/shell that #285-293
/// each populate one or more rows of. Rebuilt fresh from whichever dedicated info property backs
/// it (DefenderScan, SysMain, DeliveryOptimization, ...) every time any of those change, rather
/// than merged in place - a small (13-row), read-only, no-selection-state list, the same "clear
/// and rebuild" tier CLAUDE.md's data-flow convention calls out for lists like this.</summary>
public sealed class BackgroundActivityRow
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public BackgroundActivityState State { get; init; } = BackgroundActivityState.Unknown;
    public string CostText { get; init; } = string.Empty;

    /// <summary>Whether an expanded detail card for this row exists further down the
    /// Responsiveness tab - the ribbon itself never expands in place (see the item's own framing:
    /// "a plain data-bound ItemsControl... don't over-engineer a new custom control for this").</summary>
    public bool HasDetail { get; init; }

    public string StateText => State switch
    {
        BackgroundActivityState.Active => "Active",
        BackgroundActivityState.Inactive => "Idle",
        _ => "Unknown",
    };
}

/// <summary>#285: Windows Defender scan state and schedule, from MSFT_MpComputerStatus/
/// MSFT_MpPreference (root\Microsoft\Windows\Defender WMI namespace) - extends the Summary tab's
/// existing bare "MsMpEng at N% CPU" health-check observation into naming which scan is running
/// and what its CPU cap is. IsAvailable=false (the whole namespace missing, e.g. a third-party AV
/// owns real-time protection with Defender's engine disabled) degrades every field to Unknown/hidden,
/// never a guessed state.</summary>
public sealed class DefenderScanInfo
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = "Loading...";

    public bool RealTimeProtectionEnabled { get; init; }
    public DateTime? QuickScanStartTime { get; init; }
    public DateTime? FullScanStartTime { get; init; }
    public DateTime? SignatureLastUpdated { get; init; }

    public int? ScanAvgCpuLoadFactor { get; init; }
    public bool ScanOnlyIfIdleEnabled { get; init; }
    public string? ScanScheduleTimeText { get; init; }

    /// <summary>Quick flag, not a verdict: Windows exposes no documented "a scan is running right
    /// now" boolean, so this combines MsMpEng's own CPU% (already polled by the Processes tab, see
    /// ResponsivenessViewModel.SampleBackgroundActivityLight) with whichever scan start-time is
    /// most recent - the same heuristic SummaryViewModel's existing health-check rule already
    /// uses, just given a name and a time.</summary>
    public bool IsScanLikelyActive { get; init; }

    public string ScanActivityText { get; init; } = string.Empty;
}

/// <summary>#286: real-time-scan hot paths - directories real-time scanning keeps working over,
/// mined from Microsoft-Windows-Windows Defender/Operational (event IDs 1000/1001 scan start/
/// finish, 1116/1117 detections) plus the currently-configured exclusion list. Advisory only -
/// this app makes no automatic changes to exclusions, read-only report only.</summary>
public sealed class DefenderHotPathResult
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public List<DefenderHotPathRow> HotPaths { get; init; } = new();
    public List<string> ExclusionPaths { get; init; } = new();
}

public sealed class DefenderHotPathRow
{
    public string Directory { get; init; } = string.Empty;
    public int EventCount { get; init; }
    public DateTime LastSeen { get; init; }
    public bool IsAlreadyExcluded { get; init; }
}

/// <summary>#287: Search indexer live process cost - SearchIndexer.exe/SearchProtocolHost.exe/
/// SearchFilterHost.exe CPU+disk, reusing ProcessesViewModel's already-polled per-process data
/// (no second poll). See SearchIndexerActivityService.ReadLiveState.</summary>
public sealed class SearchIndexerLiveInfo
{
    public bool AnyProcessRunning { get; init; }
    public double TotalCpuPercent { get; init; }
    public double TotalDiskBytesPerSec { get; init; }
}

/// <summary>#287: on-demand Search indexer crawl-state scan - Microsoft-Windows-Search/Operational
/// crawl-start/stop events plus the HKLM back-off settings and a best-effort indexed-location
/// list.</summary>
public sealed class SearchIndexerCrawlResult
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool CrawlLikelyInProgress { get; init; }
    public DateTime? LastCrawlStart { get; init; }
    public DateTime? LastCrawlStop { get; init; }
    public List<string> IndexedLocations { get; init; } = new();

    /// <summary>The indexer's own idle/back-off delay setting (ms), when present under
    /// HKLM\SOFTWARE\Microsoft\Windows Search - Unknown (null) means the value wasn't found,
    /// never a guessed default.</summary>
    public int? BackOffDelayMs { get; init; }
}

/// <summary>#288: SysMain (Superfetch) service state plus the prefetch/superfetch registry
/// configuration. Deliberately the simpler of the two options the item allows: service state +
/// registry config only, not per-service-inside-svchost.exe I/O attribution - matching SysMain's
/// own disk I/O back to its specific svchost.exe host process needs more plumbing (a service-to-
/// PID-inside-a-shared-svchost join) than this one ribbon row warrants. See
/// BackgroundActivityService.ReadSysMain.</summary>
public sealed class SysMainInfo
{
    public bool ServiceRunning { get; init; }
    public string ServiceStatusText { get; init; } = "Unknown";
    public bool? PrefetcherEnabled { get; init; }
    public bool? SuperfetchEnabled { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#289: Delivery Optimization service state plus the peer-caching policy
/// (DODownloadMode). See BackgroundActivityService.ReadDeliveryOptimization for the cheap part
/// and DeliveryOptimizationService.ReadRecentActivityAsync for the on-demand event-log part.</summary>
public sealed class DeliveryOptimizationInfo
{
    public bool ServiceRunning { get; init; }
    public string ServiceStatusText { get; init; } = "Unknown";
    public int? DownloadMode { get; init; }
    public string DownloadModeText { get; init; } = "Unknown";
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#289: on-demand Delivery Optimization activity - Microsoft-Windows-
/// DeliveryOptimization/Operational event volume as a proxy for "how much has DO been doing
/// lately". A live transfer-volume number (bytes currently in flight) isn't cheaply readable
/// without the DO COM/PowerShell API, a much heavier ask than this app's other event-log reads -
/// see DeliveryOptimizationService's remarks.</summary>
public sealed class DeliveryOptimizationEventResult
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public int RecentEventCount { get; init; }
    public DateTime? LastEventTime { get; init; }
}

/// <summary>#290: Windows Update/servicing process cost - wuauserv service state plus
/// TrustedInstaller.exe/TiWorker.exe/MoUsoCoreWorker.exe/CompatTelRunner.exe CPU+disk, reusing
/// already-polled process data. None of these processes running is the normal case, not an
/// error - see WindowsUpdateActivityService.ReadProcessState.</summary>
public sealed class WindowsUpdateActivityInfo
{
    public bool ServiceRunning { get; init; }
    public string ServiceStatusText { get; init; } = "Unknown";
    public List<ProcessCostRow> ActiveProcesses { get; init; } = new();
}

/// <summary>#290: on-demand Windows Update/servicing scan/install events from
/// Microsoft-Windows-WindowsUpdateClient/Operational - names the current update operation rather
/// than leaving a long CBS servicing pass as an anonymous busy disk.</summary>
public sealed class WindowsUpdateEventResult
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public List<WindowsUpdateEventRow> RecentEvents { get; init; } = new();
}

public sealed class WindowsUpdateEventRow
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>#293/#290/#287: one process's CPU/disk cost, reused across every ribbon cluster that
/// just needs to show "this known background client is running, at this cost" from
/// already-polled process data - cloud-sync clients, game-download clients, and Windows Update
/// worker processes all share this same shape.</summary>
public sealed class ProcessCostRow
{
    public string ProcessName { get; init; } = string.Empty;
    public int Pid { get; init; }
    public double CpuPercent { get; init; }
    public double DiskBytesPerSec { get; init; }
}

/// <summary>#291: one active BITS transfer from `bitsadmin /list /allusers /verbose`.</summary>
public sealed class BitsTransferRow
{
    public string DisplayName { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string TypeText { get; init; } = string.Empty;
    public string StateText { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string PriorityText { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long BytesTotal { get; init; }
}

/// <summary>#292: Automatic Maintenance window/last-run state from
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance - an adaptive
/// label/value list (like BootTimeBreakdown's components) rather than fixed named properties,
/// since this key's exact value names aren't a documented, versioned contract. Reuses
/// PlatformLatencySettingRow, the same generic label/value shape #217-220's device-topology
/// bundle already established for "one small platform-setting fact per row".</summary>
public sealed class AutomaticMaintenanceInfo
{
    public bool KeyPresent { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public List<PlatformLatencySettingRow> Settings { get; init; } = new();
}

/// <summary>#293: Storage Sense's configured enable/frequency state from
/// HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense - live "running right now"
/// detection isn't cheaply available (no documented API/event for it), so this reports
/// configuration only, the same "document what you found" allowance the item text gives. The
/// underlying registry value names (01/2048) are an undocumented-but-community-confirmed
/// convention, the same "Quick flag, not a verdict" tier this app's AV-mitigation bitmask reads
/// already use.</summary>
public sealed class StorageSenseInfo
{
    public bool KeyPresent { get; init; }
    public bool? Enabled { get; init; }
    public string RunFrequencyText { get; init; } = "Unknown";
    public string StatusText { get; init; } = string.Empty;
}
