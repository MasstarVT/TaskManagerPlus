using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #699: the stress-test suite's always-active safety-abort guard. StressTestViewModel's single
/// supervising sample loop calls <see cref="CheckSample"/> every sample, for every one of the four
/// test types (695/696/697/698) - there is exactly one code path that runs a test, and that path
/// always creates one of these and checks it every sample; no test type, and no setting, can skip
/// the call itself. What IS adjustable (StressTestSettings, persisted to stress-test.json) is only
/// WHERE the thresholds sit and whether the WHEA/TDR half is included - the temperature ceiling
/// check below is never optional and never throttled.
///
/// The WHEA/TDR half re-scans the event log, which is real I/O (EventLogService.ReadWheaEvents/
/// ReadGpuTdrEvents can each walk up to 500 events) - per CLAUDE.md's on-demand-vs-polled
/// convention this is throttled to at most once every HardwareErrorCheckInterval internally, but
/// that throttling only changes HOW OFTEN the hardware-error half runs; the temperature half (the
/// fast-acting guard against actually damaging the machine) runs on every single call, unthrottled.
/// </summary>
public sealed class StressTestSafetyMonitor
{
    private static readonly TimeSpan HardwareErrorCheckInterval = TimeSpan.FromSeconds(5);

    private readonly StressTestSettings _settings;
    private readonly EventLogService _eventLog = new();

    private DateTime _runStart;
    // #1098: monotonic (Environment.TickCount64), not wall clock - a DST/NTP step backwards during
    // an overnight soak must not suspend the WHEA/TDR abort checks for the step's duration, the
    // same reasoning #1052/#1053 applied to the rest of the stress-test feature's timing.
    private long _nextHardwareErrorCheckDueMs = long.MinValue;
    private int _wheaEventsSinceStart;
    private int _tdrEventsSinceStart;

    public double EffectiveTempCeilingC { get; }

    public StressTestSafetyMonitor(StressTestSettings settings, double effectiveTempCeilingC)
    {
        _settings = settings;
        EffectiveTempCeilingC = effectiveTempCeilingC;
    }

    /// <summary>Running totals as of the most recent hardware-error check - exposed so
    /// StressTestViewModel can stamp WheaEventsSinceStart/TdrEventsSinceStart onto each trace
    /// sample without re-scanning the event log itself.</summary>
    public int WheaEventsSinceStart => _wheaEventsSinceStart;
    public int TdrEventsSinceStart => _tdrEventsSinceStart;

    public void BeginRun(DateTime runStart)
    {
        _runStart = runStart;
        _nextHardwareErrorCheckDueMs = long.MinValue; // force an immediate first check
        _wheaEventsSinceStart = 0;
        _tdrEventsSinceStart = 0;
    }

    /// <summary>Returns a non-null abort reason the instant the run must stop - unconditional,
    /// called every sample regardless of test type. Never throws (every event-log read is
    /// individually try/caught) - a safety check that itself could crash the run would defeat the
    /// point.</summary>
    public string? CheckSample(double? tempC)
    {
        // Temperature: always checked, every call, never throttled - this is the guard that
        // actually protects the hardware.
        if (tempC is { } t && t >= EffectiveTempCeilingC)
            return $"temperature {t:0.#}°C reached the safety ceiling ({EffectiveTempCeilingC:0.#}°C)";

        if (!_settings.AbortOnTdr && !_settings.AbortOnWheaDelta) return null;

        long nowMs = Environment.TickCount64; // #1098: monotonic - see _nextHardwareErrorCheckDueMs
        if (nowMs < _nextHardwareErrorCheckDueMs) return null;
        _nextHardwareErrorCheckDueMs = nowMs + (long)HardwareErrorCheckInterval.TotalMilliseconds;

        if (_settings.AbortOnTdr)
        {
            int tdrCount = SafeCount(() => _eventLog.ReadGpuTdrEvents().Count(e => e.TimeCreated >= _runStart));
            bool isNew = tdrCount > _tdrEventsSinceStart;
            _tdrEventsSinceStart = tdrCount;
            if (isNew) return "a GPU driver reset (TDR) was logged during this run";
        }

        if (_settings.AbortOnWheaDelta)
        {
            int wheaCount = SafeCount(() => _eventLog.ReadWheaEvents().Count(e => e.TimeCreated >= _runStart));
            bool isNew = wheaCount > _wheaEventsSinceStart;
            _wheaEventsSinceStart = wheaCount;
            if (isNew) return $"{wheaCount} new WHEA hardware-error event(s) were logged during this run";
        }

        return null;
    }

    private static int SafeCount(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
