using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #959/#966: the always-on background health collector. Owns one low-frequency
/// <see cref="DispatcherTimer"/> (default 60s, independent of every other timer in this app,
/// including LoggingViewModel's user-started 1Hz CSV logger) that reads a handful of already-live
/// values off Performance/EnergyThermals/Services/Processes - never re-samples WMI/perf-counters
/// on its own - and appends one compact row to BackgroundHealthStoreService's health-history.jsonl.
///
/// A DispatcherTimer (UI thread, Background priority) rather than a background
/// System.Threading.Timer is deliberate: every value this collector reads lives on an
/// ObservableCollection/plain property these ViewModels only ever mutate from the UI thread, so
/// reading them from a genuinely different thread would be a race. The whole point of #959/#966 is
/// that this stays cheap enough that ticking on the UI thread every 60s is a non-issue - and #966's
/// own self-measurement (wrapping the whole tick, including the file write) is what proves that,
/// tick over tick, rather than just asserting it.
///
/// #966: each cycle's wall-clock duration and an estimated CPU% cost
/// (processor-time-delta / wall-clock-elapsed / logical processor count) are tracked as a rolling
/// average and exposed for the Background Health panel's cost readout. If recent cycles are
/// consistently over a sane cost threshold, the collector backs off by doubling its own interval
/// (capped) rather than continuing to tick at the same frequency - the opposite of what a
/// monitoring tool should ever do to the system it's monitoring.
/// </summary>
public sealed class BackgroundHealthCollectorService : IDisposable
{
    private readonly PerformanceViewModel _performance;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly ServicesViewModel _services;
    private readonly ProcessesViewModel _processes;

    private BackgroundHealthSettings _settings;
    private DispatcherTimer? _timer;
    private int _effectiveIntervalSeconds;

    // #966: rolling window of recent cycle costs, purely in-memory (the per-row cost is also
    // persisted into HealthHistoryRow itself, so a longer-term view survives a restart too - see
    // BackgroundHealthViewModel's cost readout, which prefers stored history when available).
    private readonly Queue<double> _recentCpuPercentEstimates = new();
    private readonly Queue<double> _recentDurationsMs = new();
    private const int RollingWindowSize = 20;

    private const double SlowCycleThresholdMs = 75.0;
    private const int ConsecutiveSlowCyclesBeforeBackoff = 5;
    private const int MaxIntervalSeconds = 600;
    private int _consecutiveSlowCycles;

    public bool IsEnabled => _settings.Enabled;
    public int ConfiguredIntervalSeconds => _settings.IntervalSeconds;
    public int EffectiveIntervalSeconds => _effectiveIntervalSeconds;
    public int BudgetMb => _settings.BudgetMb;

    public bool IsBackedOff { get; private set; }

    public double AverageCpuPercentEstimate { get; private set; }
    public double AverageDurationMs { get; private set; }

    /// <summary>Fired after every tick (success or failure) so the Background Health panel can
    /// refresh its cost/backoff readout live without polling this service on its own timer.</summary>
    public event Action? Ticked;

    public BackgroundHealthCollectorService(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals,
        ServicesViewModel services, ProcessesViewModel processes)
    {
        _performance = performance;
        _energyThermals = energyThermals;
        _services = services;
        _processes = processes;

        _settings = BackgroundHealthSettingsService.Load();
        _effectiveIntervalSeconds = ClampInterval(_settings.IntervalSeconds);

        if (_settings.Enabled) StartTimer();
    }

    private static int ClampInterval(int seconds) => Math.Clamp(seconds, 10, MaxIntervalSeconds);

    // ----- #959: settings the Background Health panel exposes ----------------------------------

    public void SetEnabled(bool enabled)
    {
        if (_settings.Enabled == enabled) return;
        _settings.Enabled = enabled;
        BackgroundHealthSettingsService.Save(_settings);

        if (enabled) StartTimer();
        else StopTimer();
    }

    public void SetIntervalSeconds(int seconds)
    {
        int clamped = ClampInterval(seconds);
        if (_settings.IntervalSeconds == clamped) return;
        _settings.IntervalSeconds = clamped;
        BackgroundHealthSettingsService.Save(_settings);

        // A manual interval change also resets any automatic backoff - the user gets what they
        // asked for until cost data says otherwise again.
        _consecutiveSlowCycles = 0;
        IsBackedOff = false;
        _effectiveIntervalSeconds = clamped;
        if (_timer is not null) _timer.Interval = TimeSpan.FromSeconds(_effectiveIntervalSeconds);
    }

    public void SetBudgetMb(int budgetMb)
    {
        int clamped = Math.Clamp(budgetMb, 1, 10_000);
        if (_settings.BudgetMb == clamped) return;
        _settings.BudgetMb = clamped;
        BackgroundHealthSettingsService.Save(_settings);
    }

    // ----- timer lifecycle -----------------------------------------------------------------------

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(_effectiveIntervalSeconds) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
    }

    // ----- #959/#966: one collection cycle --------------------------------------------------------

    private void Tick()
    {
        var wall = Stopwatch.StartNew();
        TimeSpan cpuBefore = default;
        bool haveCpuBefore = false;
        try
        {
            cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            haveCpuBefore = true;
        }
        catch { /* best-effort - cost estimate just reads 0 this cycle if this fails */ }

        try
        {
            var topProcess = _processes.Processes.Count > 0
                ? _processes.Processes.OrderByDescending(p => p.CpuPercent).FirstOrDefault()
                : null;

            var row = new HealthHistoryRow
            {
                TimestampUtc = DateTime.UtcNow,
                CpuPercent = _performance.CpuCurrentPercent,
                RamPercent = _performance.RamPercent,
                CpuTempC = _energyThermals.CpuPackageTempC,
                DiskQueueLength = _performance.DiskQueueLength,
                DiskLatencyMs = Math.Max(_performance.DiskReadLatencyMs, _performance.DiskWriteLatencyMs),
                NetworkHasErrors = _performance.HasNetworkErrors,
                FailedServiceCount = _services.Services.Count(s => s.HasFailedToStart),
                TopProcessName = topProcess?.Name,
            };

            double cpuMs = 0;
            if (haveCpuBefore)
            {
                try { cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds; }
                catch { /* best-effort */ }
            }
            wall.Stop();
            double wallMs = wall.Elapsed.TotalMilliseconds;
            int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
            double cpuPercentEstimate = wallMs > 0 ? Math.Clamp(cpuMs / wallMs / logicalProcessors * 100.0, 0, 100) : 0;

            row.CollectorDurationMs = wallMs;
            row.CollectorCpuPercentEstimate = cpuPercentEstimate;

            BackgroundHealthStoreService.AppendRow(row, _settings.BudgetMb);

            RecordCost(cpuPercentEstimate, wallMs);
            MaybeBackoff(wallMs);
        }
        catch
        {
            // Best-effort - a failed cycle (a transient read error off one of the live view-models,
            // a disk write failure, ...) never takes the collector down; the next scheduled tick
            // just tries again.
        }
        finally
        {
            Ticked?.Invoke();
        }
    }

    private void RecordCost(double cpuPercentEstimate, double durationMs)
    {
        _recentCpuPercentEstimates.Enqueue(cpuPercentEstimate);
        _recentDurationsMs.Enqueue(durationMs);
        while (_recentCpuPercentEstimates.Count > RollingWindowSize) _recentCpuPercentEstimates.Dequeue();
        while (_recentDurationsMs.Count > RollingWindowSize) _recentDurationsMs.Dequeue();

        AverageCpuPercentEstimate = _recentCpuPercentEstimates.Count > 0 ? _recentCpuPercentEstimates.Average() : 0;
        AverageDurationMs = _recentDurationsMs.Count > 0 ? _recentDurationsMs.Average() : 0;
    }

    /// <summary>#966: "if a cycle's cost exceeds a sane threshold repeatedly, back off (increase
    /// the interval automatically) rather than continuing to hammer at the same frequency." A
    /// single slow cycle (a one-off page fault, a momentarily busy disk, ...) doesn't trigger this -
    /// only ConsecutiveSlowCyclesBeforeBackoff in a row does, and each backoff step doubles the
    /// interval up to MaxIntervalSeconds.</summary>
    private void MaybeBackoff(double wallMs)
    {
        if (wallMs > SlowCycleThresholdMs)
        {
            _consecutiveSlowCycles++;
            if (_consecutiveSlowCycles >= ConsecutiveSlowCyclesBeforeBackoff && _effectiveIntervalSeconds < MaxIntervalSeconds)
            {
                _effectiveIntervalSeconds = Math.Min(MaxIntervalSeconds, _effectiveIntervalSeconds * 2);
                if (_timer is not null) _timer.Interval = TimeSpan.FromSeconds(_effectiveIntervalSeconds);
                IsBackedOff = true;
                _consecutiveSlowCycles = 0;
            }
        }
        else
        {
            _consecutiveSlowCycles = 0;
        }
    }

    public void Dispose() => StopTimer();
}
