using System.Runtime.InteropServices;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #247/#248/#254: DwmGetCompositionTimingInfo (dwmapi.dll) - an in-box, no-ETW view of whether
/// the DWM compositor is keeping up, plus the GraphicsDrivers hardware-scheduling/TDR registry
/// values. Per CLAUDE.md's "prefer a known tool" rule, raw P/Invoke is the documented exception
/// here: there is no Windows tool or WMI class that reports composition frame timing, only this
/// dwmapi.dll call (same tier as TimerResolutionService's NtQueryTimerResolution).
///
/// DWM_TIMING_INFO is a public, SDK-documented struct (unlike the classic MOF DPC/ISR schema
/// DpcLatencyService has to tolerantly parse) - its layout is stable, but this app still degrades
/// gracefully if a future Windows build changes it: DwmGetCompositionTimingInfo validates the
/// caller-supplied cbSize against its own compiled-in expected size and returns an error if they
/// don't match (rather than reading past a mismatched buffer), so a struct-layout drift on some
/// future build surfaces as IsAvailable=false/an explanatory StatusText, never garbage numbers.
/// The call can also fail legitimately today - composition disabled, or a remote-desktop session -
/// same "degrade to Unknown, never fabricate" handling either way.
///
/// Instantiated once by ResponsivenessViewModel and sampled on its own lightweight timer (#247's
/// own framing: "rides the existing _lightTimer, cheap API call") - #248's per-second dropped/
/// missed rate is derived by diffing this instance's own previous-sample counters, the same
/// stateful-instance diffing idiom PerCoreDpcService already uses for its per-core counters.
/// #254's hardware-scheduling/TDR registry read is a separate, much cheaper method
/// (ReadHardwareScheduling) - called once at start-up plus a manual refresh, per CLAUDE.md's
/// on-demand convention, not on the per-tick timer.
/// </summary>
public sealed class DwmCompositionService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_RATIONAL
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public DWM_RATIONAL rateRefresh;
        public ulong qpcRefreshPeriod;
        public DWM_RATIONAL rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public uint cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

    private ulong? _prevDropped;
    private ulong? _prevMissed;
    private DateTime _prevSampleTime;

    /// <summary>#247/#248: called every light tick - see the class remarks.</summary>
    public DwmCompositionInfo Sample()
    {
        try
        {
            var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            int hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref info);
            if (hr != 0)
            {
                return new DwmCompositionInfo
                {
                    IsAvailable = false,
                    StatusText = $"Unknown - DwmGetCompositionTimingInfo failed (0x{hr:X8}). Composition may be disabled, or this is a remote-desktop session.",
                };
            }

            double refreshHz = info.rateRefresh.uiDenominator > 0
                ? info.rateRefresh.uiNumerator / (double)info.rateRefresh.uiDenominator
                : 0;

            double freq = TimerResolutionService.QpcFrequency;
            double frameTimeMs = freq > 0 && info.qpcRefreshPeriod > 0
                ? info.qpcRefreshPeriod / freq * 1000.0
                : (refreshHz > 0 ? 1000.0 / refreshHz : 0);

            var now = DateTime.UtcNow;
            double droppedMissedPerSec = 0;
            if (_prevDropped is { } pd && _prevMissed is { } pm && info.cFramesDropped >= pd && info.cFramesMissed >= pm)
            {
                double elapsedSec = (now - _prevSampleTime).TotalSeconds;
                if (elapsedSec > 0)
                    droppedMissedPerSec = ((info.cFramesDropped - pd) + (info.cFramesMissed - pm)) / elapsedSec;
            }
            _prevDropped = info.cFramesDropped;
            _prevMissed = info.cFramesMissed;
            _prevSampleTime = now;

            bool plausibleRefresh = refreshHz is > 0 and < 1000;
            return new DwmCompositionInfo
            {
                IsAvailable = true,
                RefreshRateHz = plausibleRefresh ? refreshHz : 0,
                CompositionFrameTimeMs = frameTimeMs is > 0 and < 1000 ? frameTimeMs : 0,
                FramesDisplayed = info.cFramesDisplayed,
                FramesDropped = info.cFramesDropped,
                FramesMissed = info.cFramesMissed,
                FramesLate = info.cFramesLate,
                FramesOutstanding = info.cFramesOutstanding,
                DroppedMissedPerSec = droppedMissedPerSec,
                StatusText = plausibleRefresh
                    ? $"Compositor running at {refreshHz:0.##} Hz ({frameTimeMs:0.###} ms/frame). {info.cFramesDropped} dropped / {info.cFramesMissed} missed frame(s) since this session started."
                    : "Composition timing available, but the reported refresh rate looks implausible on this Windows build - showing the raw frame counters only.",
            };
        }
        catch (Exception ex)
        {
            return new DwmCompositionInfo { IsAvailable = false, StatusText = $"Unknown - {ex.Message}" };
        }
    }

    /// <summary>#254: HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers - HwSchMode (2=on,
    /// 1=off, absent=unsupported by this driver/Windows build) plus TdrDelay/TdrLevel from the same
    /// key. On-demand (start-up + manual refresh), not the light tick - a plain registry read but
    /// grouped with the other "how is this machine's display pipeline configured" facets rather
    /// than re-read every 2 seconds for a value that essentially never changes without a reboot.</summary>
    public static HardwareSchedulingInfo ReadHardwareScheduling()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            object? hwSchVal = key?.GetValue("HwSchMode");
            object? tdrDelayVal = key?.GetValue("TdrDelay");
            object? tdrLevelVal = key?.GetValue("TdrLevel");

            int? hwSch = hwSchVal is int h ? h : null;
            int? tdrDelay = tdrDelayVal is int td ? td : null;
            int? tdrLevel = tdrLevelVal is int tl ? tl : null;

            string hwSchText = hwSch switch
            {
                2 => "Enabled",
                1 => "Disabled",
                null => "Unknown (not supported by this GPU driver/Windows build, or using the driver's own default)",
                _ => $"Unknown value ({hwSch})",
            };

            string tdrLevelText = tdrLevel switch
            {
                0 => "0 - detection/recovery disabled",
                1 => "1 - detection enabled, kernel-mode recovery only",
                2 => "2 - detection disabled (reserved)",
                3 => "3 - full detection and recovery (Windows default)",
                null => "Unknown (using Windows' default - full detection and recovery)",
                _ => $"Unknown value ({tdrLevel})",
            };

            return new HardwareSchedulingInfo
            {
                HwSchModeRaw = hwSch,
                HwSchModeText = hwSchText,
                TdrDelaySeconds = tdrDelay,
                TdrLevel = tdrLevel,
                TdrLevelText = tdrLevelText,
                StatusText = "Hardware-accelerated GPU scheduling changes present latency measurably - quick flag, not a directive to change it.",
            };
        }
        catch (Exception ex)
        {
            return new HardwareSchedulingInfo { StatusText = $"Unknown - {ex.Message}" };
        }
    }
}
