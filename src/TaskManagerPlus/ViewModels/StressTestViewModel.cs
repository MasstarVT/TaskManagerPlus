using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #695-#700: backs the Energy &amp; Thermals tab's "Stress test" panel - an entirely on-demand
/// (Start/Stop button-gated, never a background timer) N-thread CPU torture test, a memory
/// pattern-verify pass, a GPU load test, and a combined-load soak test, all evaluated against
/// explicit pass/fail criteria and protected by one always-active safety-abort guard
/// (StressTestSafetyMonitor - see its own remarks for exactly how "unconditional" is enforced).
///
/// Composed at the MainViewModel level (not inside EnergyThermalsViewModel, which is already the
/// single largest file in this app) rather than folded in as another region there - this is a
/// genuinely separate subsystem (its own settings, its own history, its own safety guard) with
/// just enough surface area to earn its own ViewModel, the same way GpuViewModel earned its own
/// file instead of being folded into EnergyThermalsViewModel despite both tabs sharing sensor data.
/// It still needs live readings from Performance/EnergyThermals/Gpu (temperature, clock, TDR
/// watch), so those three are taken by reference rather than re-polled - StressTestPanel.xaml
/// reaches this instance the same cross-ViewModel way the Sleep panel's Logging/Summary cards
/// reach sibling ViewModels: {Binding DataContext.StressTest, RelativeSource={RelativeSource
/// AncestorType=Window}} (see EnergyThermalsView.xaml's inclusion of StressTestPanel).
/// </summary>
public sealed class StressTestViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly PerformanceViewModel _performance;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly GpuViewModel _gpu;

    private readonly StressTestSettings _settings = StressTestSettingsService.Load();
    private List<StressTestHistoryEntry> _historyEntries;

    private CancellationTokenSource? _cts;
    private volatile bool _manualStopRequested;
    private CpuTortureResult? _cpuResult;
    private MemoryVerifyResult? _memoryResult;

    private StressTestType _selectedTestType = StressTestType.CpuTorture;
    public StressTestType SelectedTestType { get => _selectedTestType; set => SetProperty(ref _selectedTestType, value); }

    // The getters clamp too: stress-test.json is user-editable, and an out-of-range thread count or
    // share loaded from it would otherwise reach the workload unchecked.
    public int DurationSeconds
    {
        get => Math.Clamp(_settings.DefaultDurationSeconds, 10, 3600);
        set
        {
            value = Math.Clamp(value, 10, 3600);
            if (_settings.DefaultDurationSeconds == value) return;
            _settings.DefaultDurationSeconds = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public int CpuThreadCount
    {
        get => _settings.CpuThreadCount > 0 ? Math.Min(_settings.CpuThreadCount, Environment.ProcessorCount * 2) : Environment.ProcessorCount;
        set
        {
            value = Math.Clamp(value, 1, Environment.ProcessorCount * 2);
            if (_settings.CpuThreadCount == value) return;
            _settings.CpuThreadCount = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public double MemoryTestShareOfFreePercent
    {
        get => double.IsFinite(_settings.MemoryTestShareOfFreePercent) ? Math.Clamp(_settings.MemoryTestShareOfFreePercent, 5, 70) : 25.0;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 5, 70) : 25.0;
            if (Math.Abs(_settings.MemoryTestShareOfFreePercent - value) < 0.01) return;
            _settings.MemoryTestShareOfFreePercent = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>#699: the abort-trigger temperature. 0 shown as "auto" - see
    /// ResolveEffectiveTempCeiling's remarks for how the real default is derived each run.
    /// Setting this to a positive value overrides the auto default from then on (capped at
    /// MaxTempCeilingC).</summary>
    public double TempCeilingC
    {
        // Clamped like the run uses it, so a hand-edited 150 in stress-test.json shows as 110.
        get => double.IsFinite(_settings.TempCeilingC) ? Math.Clamp(_settings.TempCeilingC, 0, MaxTempCeilingC) : 0;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0, MaxTempCeilingC) : 0;
            if (Math.Abs(_settings.TempCeilingC - value) < 0.01) return;
            _settings.TempCeilingC = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveTempCeilingText));
        }
    }

    // Set for the duration of a run, so the panel shows the ceiling that run is actually using
    // even if the auto reference moves underneath it.
    private string? _runTempCeilingText;

    public string EffectiveTempCeilingText => _runTempCeilingText ?? DescribeEffectiveTempCeiling().Text;

    /// <summary>Whether the WHEA/TDR half of the safety-abort guard is active - NOT a way to
    /// disable the guard's temperature check (that one is never optional, see
    /// StressTestSafetyMonitor's remarks), only whether a hardware-error-log delta also triggers an
    /// abort.</summary>
    public bool AbortOnWheaDelta
    {
        get => _settings.AbortOnWheaDelta;
        set { if (_settings.AbortOnWheaDelta == value) return; _settings.AbortOnWheaDelta = value; StressTestSettingsService.Save(_settings); OnPropertyChanged(); }
    }

    public bool AbortOnTdr
    {
        get => _settings.AbortOnTdr;
        set { if (_settings.AbortOnTdr == value) return; _settings.AbortOnTdr = value; StressTestSettingsService.Save(_settings); OnPropertyChanged(); }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Bound by StressTestPanel.xaml's GPU-load Canvas via a DataTrigger
    /// (EnterActions/ExitActions BeginStoryboard/StopStoryboard) - see #697's remarks in that file
    /// for why a WPF-compositor render loop, not raw D3D interop.</summary>
    private bool _isGpuRenderActive;
    public bool IsGpuRenderActive { get => _isGpuRenderActive; private set => SetProperty(ref _isGpuRenderActive, value); }

    private string _statusText = "Idle.";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private StressTestRunResult? _lastResult;
    public StressTestRunResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (!SetProperty(ref _lastResult, value)) return;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _lastComparisonText = string.Empty;
    public string LastComparisonText { get => _lastComparisonText; private set => SetProperty(ref _lastComparisonText, value); }

    private string _reportStatusText = string.Empty;
    public string ReportStatusText { get => _reportStatusText; private set => SetProperty(ref _reportStatusText, value); }

    public ObservableCollection<StressTestHistoryEntry> RunHistory { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    public StressTestViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals, GpuViewModel gpu)
    {
        _performance = performance;
        _energyThermals = energyThermals;
        _gpu = gpu;

        _historyEntries = StressTestHistoryService.Load();
        foreach (var e in _historyEntries.Take(20)) RunHistory.Add(e);

        StartCommand = new RelayCommand(_ => _ = StartAsync(), _ => !IsRunning);
        StopCommand = new RelayCommand(_ => Stop(), _ => IsRunning);
        ExportMarkdownCommand = new RelayCommand(_ => ExportMarkdown(), _ => LastResult is not null);
        ExportHtmlCommand = new RelayCommand(_ => ExportHtml(), _ => LastResult is not null);
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => LastResult is not null);

        // The auto ceiling follows the throttle-point reference, which is learned (or revised)
        // after this panel first binds.
        _energyThermals.PropertyChanged += OnEnergyThermalsPropertyChanged;
    }

    private void OnEnergyThermalsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnergyThermalsViewModel.CpuThrottlePointReferenceC))
            OnPropertyChanged(nameof(EffectiveTempCeilingText));
    }

    private void Stop()
    {
        if (!IsRunning) return;
        _manualStopRequested = true;
        StatusText = "Stopping...";
        try { _cts?.Cancel(); } catch { /* already cancelled/disposed */ }
    }

    private async Task StartAsync()
    {
        if (IsRunning) return;

        _cpuResult = null;
        _memoryResult = null;
        _manualStopRequested = false;
        IsRunning = true;

        var type = SelectedTestType;
        var runStart = DateTime.Now;
        // Round 18, #1053: natural completion and ActualDuration come off a monotonic clock -
        // a DST/NTP wall-clock step during an overnight soak must neither extend the real
        // CPU/GPU/disk load past the requested duration nor mis-report how long it ran.
        // runStart (wall clock) stays for timestamps and event-log filtering only.
        long runStartTimestamp = Stopwatch.GetTimestamp();
        var requestedDuration = TimeSpan.FromSeconds(Math.Max(1, DurationSeconds));

        var (effectiveCeiling, ceilingText) = DescribeEffectiveTempCeiling();
        _runTempCeilingText = ceilingText;
        OnPropertyChanged(nameof(EffectiveTempCeilingText));
        var safety = new StressTestSafetyMonitor(_settings, effectiveCeiling);
        safety.BeginRun(runStart);

        var trace = new List<StressTestTraceSample>();
        string? abortReason = null;
        bool loadsGpu = type is StressTestType.GpuLoad or StressTestType.CombinedSoak;
        bool gpuTempSeen = false;

        StatusText = $"Running {StressTestReportService.DescribeType(type)}...";

        // #1094: the safety monitor's temperature ceiling can only act on a reading - on a machine
        // with no readable CPU/GPU package temperature (a VM, an unsupported board, a sensor-init
        // failure) the run would otherwise proceed with the advertised thermal abort silently
        // inert. Warn up front rather than implying coverage that doesn't exist; the WHEA/TDR half
        // of the guard still runs either way, and FinishRun repeats the note if no reading ever
        // appeared during the run. A GPU-loading run with only a CPU reading is half-guarded, and
        // says so too.
        var (initialCpuTemp, initialGpuTemp) = GuardTemperatures(type, ReadFreshSensorSnapshot());
        if (HotterOf(initialCpuTemp, initialGpuTemp) is null)
            StatusText += " Warning: no temperature reading is available right now, so the thermal safety ceiling cannot act until one appears - only the WHEA/TDR guard is live.";
        else if (loadsGpu && initialGpuTemp is null)
            StatusText += " Warning: no GPU temperature reading is available right now, so the thermal safety ceiling is watching the CPU only.";

        LastComparisonText = string.Empty;
        ReportStatusText = string.Empty;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        // Round 18, #1021: everything from here on runs under try/finally - a fault anywhere
        // (most plausibly the supervisor itself) must still cancel the workloads and reset
        // IsRunning/_cts, or the GPU render loop keeps running with the temperature-ceiling
        // abort dead and Start stays disabled until app restart, breaking the "always-on safety
        // monitor, no opt-out" invariant in the class remarks.
        try
        {
            // #699: the ONE supervising sample loop every test type funnels through - this is what
            // makes the safety-abort guard unconditional rather than something each test type would
            // need to remember to wire up itself. It runs synchronously on a dedicated thread, not
            // the thread pool: the CPU torture workers can occupy every pool thread, and a pool
            // continuation queued behind them would leave the guard unchecked for many seconds.
            var supervisorTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    while (true)
                    {
                        // GPU-driving test types guard both CPU and GPU - a combined soak can push
                        // either past a safe ceiling. Round 18, #1020: read off the lock-protected
                        // scalar snapshot, never the UI-owned sensor collections/properties, from
                        // this background thread. One read feeds both the trace and the guard, so
                        // the recorded temperature is exactly what the ceiling acted on.
                        var sensors = ReadFreshSensorSnapshot();
                        var (cpuTemp, gpuTemp) = GuardTemperatures(type, sensors);
                        if (gpuTemp is { } g && double.IsFinite(g)) gpuTempSeen = true;
                        trace.Add(BuildTraceSample(safety, sensors, HotterOf(cpuTemp, gpuTemp)));

                        var reason = safety.CheckSample(cpuTemp, gpuTemp);
                        if (reason is not null) { abortReason = reason; break; }

                        if (Stopwatch.GetElapsedTime(runStartTimestamp) >= requestedDuration) break; // natural completion (#1053: monotonic)

                        if (ct.WaitHandle.WaitOne(SampleInterval)) break; // cancelled
                    }
                }
                finally
                {
                    // Signal every workload to stop, whether this loop broke because of an abort,
                    // natural duration completion, an external Stop()/cts.Cancel() - or (#1021) a
                    // fault in the loop itself. Idempotent if already cancelled.
                    try { cts.Cancel(); } catch { /* already cancelled/disposed */ }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Task workloadTask = type switch
            {
                StressTestType.CpuTorture => RunCpuTortureAsync(requestedDuration, ct),
                StressTestType.MemoryVerify => RunMemoryVerifyAsync(ct),
                StressTestType.GpuLoad => RunGpuLoadAsync(ct),
                StressTestType.CombinedSoak => RunCombinedSoakAsync(requestedDuration, ct),
                _ => Task.CompletedTask,
            };

            try { await workloadTask; }
            catch { /* individual test services already catch internally - defensive only */ }

            try { cts.Cancel(); } catch { /* ensure the supervisor also stops if the workload finished early (e.g. memory test) */ }

            // #1021: a supervisor fault must not skip FinishRun/the reset below - surface it as an
            // abort reason (honest: the safety guard stopped watching partway through) instead.
            try { await supervisorTask; }
            catch (Exception ex)
            {
                abortReason ??= $"safety supervisor error ({ex.Message})";
            }

            if (abortReason is null && _manualStopRequested) abortReason = "stopped by user before completion";

            var actualDuration = Stopwatch.GetElapsedTime(runStartTimestamp);

            // Final, authoritative WHEA/TDR check - independent of whether AbortOnWheaDelta/
            // AbortOnTdr were enabled (those only control whether a delta triggers an early abort;
            // pass/fail itself always checks for one, per #699's fixed criteria list). Read off the
            // UI thread: up to 500 WHEA records each get FormatDescription + regex parsing, which
            // froze the window at the end of every run on a machine with a corrected-error backlog.
            var (wheaCount, tdrCount) = await Task.Run(() =>
            {
                var finalEventLog = new EventLogService();
                return (SafeCount(() => finalEventLog.ReadWheaEvents().Count(e => e.TimeCreated >= runStart)),
                        SafeCount(() => finalEventLog.ReadGpuTdrEvents().Count(e => e.TimeCreated >= runStart)));
            });

            FinishRun(type, runStart, requestedDuration, actualDuration, trace, abortReason, effectiveCeiling, wheaCount, tdrCount,
                gpuUnguarded: loadsGpu && !gpuTempSeen);
        }
        finally
        {
            try { cts.Cancel(); } catch { /* already cancelled/disposed */ }
            _cts = null;
            _runTempCeilingText = null;
            OnPropertyChanged(nameof(EffectiveTempCeilingText));
            IsRunning = false;
        }
    }

    private async Task RunCpuTortureAsync(TimeSpan duration, CancellationToken ct)
        => _cpuResult = await CpuTortureTestService.RunAsync(CpuThreadCount, duration, ct);

    private async Task RunMemoryVerifyAsync(CancellationToken ct)
    {
        double freeGb = _performance.RamAvailableGb;
        // #696: deliberately capped below the free-memory figure the Memory tab already tracks -
        // never more than 70% of what's free right now, on top of the user's own configured share -
        // so this test can never push the system into paging.
        double shareGb = Math.Min(freeGb * (MemoryTestShareOfFreePercent / 100.0), freeGb * 0.7);
        long bytes = (long)(shareGb * 1024L * 1024 * 1024);
        _memoryResult = await MemoryVerifyTestService.RunAsync(bytes, ct);
    }

    private async Task RunGpuLoadAsync(CancellationToken ct)
    {
        IsGpuRenderActive = true;
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* expected - the supervisor/Stop cancels this */ }
        finally { IsGpuRenderActive = false; }
    }

    private async Task RunCombinedSoakAsync(TimeSpan duration, CancellationToken ct)
    {
        IsGpuRenderActive = true;
        try
        {
            var cpuTask = RunCpuTortureAsync(duration, ct);
            var diskTask = DiskLoadTestService.RunAsync(ct);
            await Task.WhenAll(cpuTask, diskTask);
        }
        finally
        {
            IsGpuRenderActive = false;
        }
    }

    /// <summary>#699: resolves the effective abort-trigger temperature, and the panel's text for
    /// it, in one place - the user's override when set, else 5°C of margin below
    /// EnergyThermalsViewModel's own #608 throttle-point reference (kept between MinAutoTempCeilingC
    /// and MaxTempCeilingC), or a conservative 90°C when no reported/inferred reference exists at
    /// all yet (a fresh install with no thermal history). The auto value is re-derived each run
    /// rather than written into TempCeilingC, so it follows the reference as it's learned and is
    /// never shown as "(custom)".</summary>
    private (double CeilingC, string Text) DescribeEffectiveTempCeiling()
    {
        if (_settings.TempCeilingC > 0)
        {
            double custom = Math.Min(_settings.TempCeilingC, MaxTempCeilingC);
            return (custom, $"{custom:0.#}°C (custom)");
        }

        if (_energyThermals.CpuThrottlePointReferenceC is not { } refC || !double.IsFinite(refC))
            return (DefaultTempCeilingC, $"{DefaultTempCeilingC:0.0}°C (auto: no throttle-point reference available yet)");

        double margin = refC - 5.0;
        double auto = Math.Clamp(margin, MinAutoTempCeilingC, MaxTempCeilingC);
        string how = margin > MaxTempCeilingC
            ? $"the {MaxTempCeilingC:0}°C maximum; the throttle-point reference is {refC:0.#}°C"
            : margin < MinAutoTempCeilingC
                ? $"the {MinAutoTempCeilingC:0}°C minimum; the throttle-point reference is {refC:0.#}°C"
                : $"5°C below the {refC:0.#}°C throttle-point reference";
        return (auto, $"{auto:0.#}°C (auto: {how})");
    }

    // Upper bound on the abort ceiling - above any real CPU/GPU throttle point, so a typo or a
    // hand-edited stress-test.json can't park the always-on thermal check where it never fires.
    private const double MaxTempCeilingC = 110.0;

    // Lower bound on the AUTO ceiling only: a low or wrongly learned throttle-point reference
    // must not park the guard where every run aborts within seconds. A user override may go lower.
    private const double MinAutoTempCeilingC = 70.0;

    private const double DefaultTempCeilingC = 90.0;

    /// <summary>The sensor snapshot, or an all-null one when it hasn't been republished within a
    /// few poll intervals: a sensor tick that faults or hangs before publishing leaves the last
    /// reading frozen, and the safety ceiling must see that as "no reading", not a live one.</summary>
    private StressSensorSnapshot ReadFreshSensorSnapshot()
    {
        var s = _energyThermals.GetStressSensorSnapshot();
        if (s.PublishedTickMs <= 0) return default;
        double staleAfterMs = Math.Max(5000, 3 * s.PollIntervalSeconds * 1000);
        return Environment.TickCount64 - s.PublishedTickMs <= staleAfterMs ? s : default;
    }

    /// <summary>The temperatures the safety ceiling guards for this test type: CPU package always,
    /// GPU only for the types that load the GPU.</summary>
    private static (double? CpuTempC, double? GpuTempC) GuardTemperatures(StressTestType type, StressSensorSnapshot s)
        => type is StressTestType.GpuLoad or StressTestType.CombinedSoak
            ? (s.CpuPackageTempC, s.GpuTempC)
            : (s.CpuPackageTempC, null);

    /// <summary>Round 18, #1020: runs on the supervisor's background thread, so it never touches
    /// the source ViewModels' UI-owned ObservableCollections (Voltages/Fans/LiveAdapters), whose
    /// Clear+Add rebuilds on the UI thread would throw InvalidOperationException into a
    /// mid-enumeration reader here - those values come from the lock-protected scalar snapshots
    /// the ViewModels publish each tick instead. The remaining direct reads are plain (non-nullable)
    /// doubles off PerformanceViewModel, whose aligned 64-bit reads are atomic.</summary>
    private StressTestTraceSample BuildTraceSample(StressTestSafetyMonitor safety, StressSensorSnapshot sensors, double? guardTempC)
    {
        double? throttlePercent = _performance.CpuVsBasePercent < 0 ? Math.Min(100, -_performance.CpuVsBasePercent) : 0;
        double? gpuUtilization = _gpu.GetMaxAdapterUtilizationSnapshot();

        return new StressTestTraceSample
        {
            Timestamp = DateTime.Now,
            TempC = guardTempC,
            ClockGhz = _performance.CpuCurrentClockGhz > 0 ? _performance.CpuCurrentClockGhz : null,
            PackagePowerW = sensors.TotalPackagePowerW,
            ThrottlePercent = throttlePercent,
            FanRpm = sensors.PrimaryFanRpm,
            RailVoltage = sensors.VcoreRailVoltage,
            GpuUtilizationPercent = gpuUtilization,
            WheaEventsSinceStart = safety.WheaEventsSinceStart,
            TdrEventsSinceStart = safety.TdrEventsSinceStart,
        };
    }

    /// <summary>The higher of two optional temperature readings - null only when neither is
    /// available. The trace's headline TempC for the guarded readings, so a combined CPU+GPU load
    /// can never hide behind whichever of the two happens to run cooler.</summary>
    private static double? HotterOf(double? a, double? b) => (a, b) switch
    {
        ({ } av, { } bv) => Math.Max(av, bv),
        ({ } av, null) => av,
        (null, { } bv) => bv,
        _ => null,
    };

    /// <param name="gpuUnguarded">A GPU-loading run that never had a GPU temperature reading.</param>
    private void FinishRun(StressTestType type, DateTime runStart, TimeSpan requestedDuration, TimeSpan actualDuration, List<StressTestTraceSample> trace, string? abortReason, double effectiveCeiling, int wheaCount, int tdrCount, bool gpuUnguarded)
    {
        // A memory test that skipped itself (too little free memory, allocation refused) tested
        // nothing - neither a computation failure nor a pass; the run reports SKIPPED instead.
        bool memorySkipped = _memoryResult is { Skipped: true };
        var testedMemory = memorySkipped ? null : _memoryResult;
        bool computationChecked = _cpuResult is not null || testedMemory is not null;
        bool computationOk = (_cpuResult?.Passed ?? true) && (testedMemory?.Passed ?? true);

        bool clockChecked = type is StressTestType.CpuTorture or StressTestType.CombinedSoak;
        var clockSamples = trace.Where(t => t.ClockGhz.HasValue).Select(t => t.ClockGhz!.Value).ToList();
        double? avgClock = clockSamples.Count > 0 ? clockSamples.Average() : null;
        // No clock samples or no rated base clock means the criterion can't be judged - Unknown,
        // not a pass (see StressTestCriteria.ClockMeasured).
        bool clockMeasured = clockChecked && avgClock is not null && _performance.CpuBaseClockGhz > 0;
        bool sustainedClockOk = clockMeasured && avgClock >= _performance.CpuBaseClockGhz;

        var tempSamples = trace.Where(t => t.TempC.HasValue).Select(t => t.TempC!.Value).ToList();
        double? peakTemp = tempSamples.Count > 0 ? tempSamples.Max() : null;
        double? throttleReference = _energyThermals.CpuThrottlePointReferenceC;
        bool tempMeasured = peakTemp is not null;
        bool peakTempOk = tempMeasured && peakTemp < (throttleReference ?? 90.0);

        var powerSamples = trace.Where(t => t.PackagePowerW.HasValue).Select(t => t.PackagePowerW!.Value).ToList();
        double? peakPower = powerSamples.Count > 0 ? powerSamples.Max() : null;
        var fanSamples = trace.Where(t => t.FanRpm.HasValue).Select(t => t.FanRpm!.Value).ToList();
        double? peakFan = fanSamples.Count > 0 ? fanSamples.Max() : null;

        // wheaCount/tdrCount: the final WHEA/TDR check, read off-thread by StartAsync.
        var criteria = new StressTestCriteria
        {
            ComputationChecked = computationChecked,
            ComputationOk = computationOk,
            NoWheaDelta = wheaCount == 0,
            NoTdr = tdrCount == 0,
            ClockChecked = clockChecked,
            ClockMeasured = clockMeasured,
            SustainedClockAtOrAboveBase = sustainedClockOk,
            TempMeasured = tempMeasured,
            PeakTempBelowThrottlePoint = peakTempOk,
            Aborted = abortReason is not null,
            AbortReason = abortReason,
            Skipped = memorySkipped,
            SkipReason = memorySkipped ? _memoryResult!.SkipReason : null,
        };

        int threadCount = type is StressTestType.CpuTorture or StressTestType.CombinedSoak ? CpuThreadCount : 0;

        var result = new StressTestRunResult
        {
            TestType = type,
            StartedAt = runStart,
            RequestedDuration = requestedDuration,
            ActualDuration = actualDuration,
            ThreadCount = threadCount,
            EffectiveTempCeilingC = effectiveCeiling,
            ThrottlePointReferenceC = throttleReference,
            Trace = trace,
            Criteria = criteria,
            CpuResult = _cpuResult,
            MemoryResult = _memoryResult,
            PeakTempC = peakTemp,
            AvgClockGhz = avgClock,
            PeakPowerW = peakPower,
            PeakFanRpm = peakFan,
        };

        LastResult = result;

        // A skipped run has nothing to compare - kept out of the run-to-run history so it can't
        // show up there as a failure (or be compared against as if it had run).
        if (memorySkipped && abortReason is null)
        {
            LastComparisonText = string.Empty;
            StatusText = $"Run SKIPPED: {criteria.SkipReason ?? "the memory test couldn't run"}";
            return;
        }

        var historyEntry = new StressTestHistoryEntry
        {
            Timestamp = runStart,
            TestType = type,
            DurationSeconds = actualDuration.TotalSeconds,
            Passed = result.Passed,
            AbortReason = abortReason,
            PeakTempC = peakTemp,
            AvgClockGhz = avgClock,
            PeakPowerW = peakPower,
            PeakFanRpm = peakFan,
            ThreadCount = threadCount,
            // Reached only when a skipped run was also aborted; the history row then says SKIPPED.
            Skipped = memorySkipped,
        };

        var previous = memorySkipped ? null : StressTestHistoryService.FindMostRecentOfSameType(_historyEntries, type, runStart);
        _historyEntries = StressTestHistoryService.Append(historyEntry);
        RunHistory.Clear();
        foreach (var e in _historyEntries.Take(20)) RunHistory.Add(e);

        LastComparisonText = memorySkipped
            ? string.Empty
            : previous is not null
                ? StressTestReportService.BuildComparisonText(result, previous)
                : "No previous run of this test type to compare against yet.";

        // #1094: a verdict with zero temperature samples must say so - "PASSED" would otherwise
        // imply the thermal ceiling was standing guard when it never had data to act on. Same for
        // a GPU-loading run whose GPU half never had a reading.
        string noTempNote = tempSamples.Count == 0
            ? " No temperature reading was available during this run, so the thermal safety ceiling could not act (only the WHEA/TDR guard was live)."
            : gpuUnguarded
                ? " No GPU temperature reading was available during this run, so the thermal safety ceiling watched the CPU only."
                : string.Empty;

        StatusText = (result.Passed
            ? "Run PASSED."
            : abortReason is not null
                ? $"Run ABORTED: {abortReason}."
                : "Run FAILED - see the criteria breakdown below.") + noTempNote;
    }

    private static int SafeCount(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private void ExportMarkdown() => ExportReport("Markdown files (*.md)|*.md|All files (*.*)|*.*", ".md",
        (result, previous) => StressTestReportService.BuildRunMarkdown(result, previous));

    private void ExportHtml() => ExportReport("HTML files (*.html)|*.html|All files (*.*)|*.*", ".html",
        (result, previous) => StressTestReportService.BuildRunHtml(result, previous));

    private void ExportReport(string filter, string defaultExt, Func<StressTestRunResult, StressTestHistoryEntry?, string> build)
    {
        if (LastResult is not { } result) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export stress test report",
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = $"StressTest-{result.TestType}-{result.StartedAt:yyyy-MM-dd_HH-mm-ss}{defaultExt}",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // A skipped run tested nothing, so its report has no "compared to the previous run" section.
            var previous = result.Criteria.Skipped
                ? null
                : StressTestHistoryService.FindMostRecentOfSameType(_historyEntries, result.TestType, result.StartedAt);
            File.WriteAllText(dialog.FileName, build(result, previous));
            ReportStatusText = $"Report saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ReportStatusText = $"Couldn't save report: {ex.Message}";
        }
    }

    /// <summary>Cancels any run in progress - called from MainViewModel.Dispose() so closing the
    /// app mid-run doesn't leave a torture-test worker thread or the GPU render loop running past
    /// window shutdown.</summary>
    public void Dispose()
    {
        _energyThermals.PropertyChanged -= OnEnergyThermalsPropertyChanged;
        Stop();
    }

    private void ExportCsv()
    {
        if (LastResult is not { } result) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export stress test trace (CSV)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"StressTest-{result.TestType}-{result.StartedAt:yyyy-MM-dd_HH-mm-ss}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            StressTestReportService.ExportTraceCsv(result, dialog.FileName);
            ReportStatusText = $"Trace saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ReportStatusText = $"Couldn't save trace: {ex.Message}";
        }
    }
}
