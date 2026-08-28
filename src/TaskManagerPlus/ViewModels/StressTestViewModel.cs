using System.Collections.ObjectModel;
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

    public IReadOnlyList<StressTestType> TestTypes { get; } =
        new[] { StressTestType.CpuTorture, StressTestType.MemoryVerify, StressTestType.GpuLoad, StressTestType.CombinedSoak };

    private StressTestType _selectedTestType = StressTestType.CpuTorture;
    public StressTestType SelectedTestType { get => _selectedTestType; set => SetProperty(ref _selectedTestType, value); }

    public int DurationSeconds
    {
        get => _settings.DefaultDurationSeconds;
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
        get => _settings.CpuThreadCount > 0 ? _settings.CpuThreadCount : Environment.ProcessorCount;
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
        get => _settings.MemoryTestShareOfFreePercent;
        set
        {
            value = Math.Clamp(value, 5, 70);
            if (Math.Abs(_settings.MemoryTestShareOfFreePercent - value) < 0.01) return;
            _settings.MemoryTestShareOfFreePercent = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>#699: the abort-trigger temperature. 0 shown as "auto" - see
    /// ResolveEffectiveTempCeiling's remarks for how the real default is derived the first time a
    /// run needs one. Setting this to a positive value overrides the auto default from then on.</summary>
    public double TempCeilingC
    {
        get => _settings.TempCeilingC;
        set
        {
            value = Math.Max(0, value);
            if (Math.Abs(_settings.TempCeilingC - value) < 0.01) return;
            _settings.TempCeilingC = value;
            StressTestSettingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveTempCeilingText));
        }
    }

    public string EffectiveTempCeilingText => _settings.TempCeilingC > 0
        ? $"{_settings.TempCeilingC:0.#}°C (custom)"
        : _energyThermals.CpuThrottlePointReferenceC is { } refC
            ? $"{refC - 5.0:0.#}°C (auto: 5°C below the {refC:0.#}°C throttle-point reference)"
            : "90.0°C (auto: no throttle-point reference available yet)";

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
        var requestedDuration = TimeSpan.FromSeconds(Math.Max(1, DurationSeconds));

        double effectiveCeiling = ResolveEffectiveTempCeiling();
        var safety = new StressTestSafetyMonitor(_settings, effectiveCeiling);
        safety.BeginRun(runStart);

        var trace = new List<StressTestTraceSample>();
        string? abortReason = null;

        StatusText = $"Running {StressTestReportService.DescribeType(type)}...";
        LastComparisonText = string.Empty;
        ReportStatusText = string.Empty;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        // #699: the ONE supervising sample loop every test type funnels through - this is what
        // makes the safety-abort guard unconditional rather than something each test type would
        // need to remember to wire up itself.
        var supervisorTask = Task.Run(async () =>
        {
            while (true)
            {
                trace.Add(BuildTraceSample(safety));

                // GPU-driving test types watch whichever of CPU/GPU is hotter, not just one - a
                // combined soak can push either past a safe ceiling.
                double? tempForSafety = type is StressTestType.GpuLoad or StressTestType.CombinedSoak
                    ? HotterOf(_energyThermals.CpuPackageTempC, _energyThermals.GpuTempC)
                    : _energyThermals.CpuPackageTempC;

                var reason = safety.CheckSample(tempForSafety);
                if (reason is not null) { abortReason = reason; break; }

                if (DateTime.Now - runStart >= requestedDuration) break; // natural completion

                try { await Task.Delay(SampleInterval, ct); }
                catch (OperationCanceledException) { break; }
            }

            // Signal every workload to stop, whether this loop broke because of an abort, natural
            // duration completion, or an external Stop()/cts.Cancel() - idempotent if already
            // cancelled.
            try { cts.Cancel(); } catch { /* already cancelled/disposed */ }
        });

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
        await supervisorTask;

        if (abortReason is null && _manualStopRequested) abortReason = "stopped by user before completion";

        FinishRun(type, runStart, requestedDuration, trace, abortReason, effectiveCeiling);

        _cts = null;
        IsRunning = false;
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

    /// <summary>#699: resolves and (once) persists the effective abort-trigger temperature - 5°C of
    /// margin below EnergyThermalsViewModel's own #608 throttle-point reference, or a conservative
    /// 90°C when no reported/inferred reference exists at all yet (a fresh install with no thermal
    /// history). Resolved once and written back to stress-test.json so it stays stable across runs
    /// unless the user explicitly changes it afterward.</summary>
    private double ResolveEffectiveTempCeiling()
    {
        if (_settings.TempCeilingC > 0) return _settings.TempCeilingC;

        double resolved = _energyThermals.CpuThrottlePointReferenceC is { } refC ? refC - 5.0 : 90.0;
        _settings.TempCeilingC = resolved;
        StressTestSettingsService.Save(_settings);
        OnPropertyChanged(nameof(TempCeilingC));
        OnPropertyChanged(nameof(EffectiveTempCeilingText));
        return resolved;
    }

    private StressTestTraceSample BuildTraceSample(StressTestSafetyMonitor safety)
    {
        double? throttlePercent = _performance.CpuVsBasePercent < 0 ? Math.Min(100, -_performance.CpuVsBasePercent) : 0;
        var railVoltage = _energyThermals.Voltages.FirstOrDefault(v => v.Value.HasValue &&
            (v.SensorName.Contains("Vcore", StringComparison.OrdinalIgnoreCase) || v.SensorName.Contains("CPU Core", StringComparison.OrdinalIgnoreCase)));
        var fan = _energyThermals.Fans.FirstOrDefault(f => f.Value.HasValue);
        double? gpuUtilization = _gpu.LiveAdapters.Count > 0 ? _gpu.LiveAdapters.Max(a => a.TotalUtilizationPercent) : null;

        return new StressTestTraceSample
        {
            Timestamp = DateTime.Now,
            TempC = HotterOf(_energyThermals.CpuPackageTempC, _energyThermals.GpuTempC),
            ClockGhz = _performance.CpuCurrentClockGhz > 0 ? _performance.CpuCurrentClockGhz : null,
            PackagePowerW = _energyThermals.TotalPackagePowerW,
            ThrottlePercent = throttlePercent,
            FanRpm = fan?.Value,
            RailVoltage = railVoltage?.Value,
            GpuUtilizationPercent = gpuUtilization,
            WheaEventsSinceStart = safety.WheaEventsSinceStart,
            TdrEventsSinceStart = safety.TdrEventsSinceStart,
        };
    }

    /// <summary>The higher of two optional temperature readings - null only when neither is
    /// available. Used both for the trace's headline TempC and the safety monitor's own check, so
    /// a combined CPU+GPU load can never hide behind whichever of the two happens to run cooler.</summary>
    private static double? HotterOf(double? a, double? b) => (a, b) switch
    {
        ({ } av, { } bv) => Math.Max(av, bv),
        ({ } av, null) => av,
        (null, { } bv) => bv,
        _ => null,
    };

    private void FinishRun(StressTestType type, DateTime runStart, TimeSpan requestedDuration, List<StressTestTraceSample> trace, string? abortReason, double effectiveCeiling)
    {
        var actualDuration = DateTime.Now - runStart;

        bool computationChecked = _cpuResult is not null || _memoryResult is not null;
        bool computationOk = (_cpuResult?.Passed ?? true) && (_memoryResult?.Passed ?? true);

        bool clockChecked = type is StressTestType.CpuTorture or StressTestType.CombinedSoak;
        var clockSamples = trace.Where(t => t.ClockGhz.HasValue).Select(t => t.ClockGhz!.Value).ToList();
        double? avgClock = clockSamples.Count > 0 ? clockSamples.Average() : null;
        bool sustainedClockOk = !clockChecked || avgClock is null || _performance.CpuBaseClockGhz <= 0 || avgClock >= _performance.CpuBaseClockGhz;

        var tempSamples = trace.Where(t => t.TempC.HasValue).Select(t => t.TempC!.Value).ToList();
        double? peakTemp = tempSamples.Count > 0 ? tempSamples.Max() : null;
        double? throttleReference = _energyThermals.CpuThrottlePointReferenceC;
        bool peakTempOk = peakTemp is null || peakTemp < (throttleReference ?? 90.0);

        var powerSamples = trace.Where(t => t.PackagePowerW.HasValue).Select(t => t.PackagePowerW!.Value).ToList();
        double? peakPower = powerSamples.Count > 0 ? powerSamples.Max() : null;
        var fanSamples = trace.Where(t => t.FanRpm.HasValue).Select(t => t.FanRpm!.Value).ToList();
        double? peakFan = fanSamples.Count > 0 ? fanSamples.Max() : null;

        // Final, authoritative WHEA/TDR check - independent of whether AbortOnWheaDelta/AbortOnTdr
        // were enabled (those only control whether a delta triggers an early abort; pass/fail
        // itself always checks for one, per #699's fixed criteria list).
        var finalEventLog = new EventLogService();
        int wheaCount = SafeCount(() => finalEventLog.ReadWheaEvents().Count(e => e.TimeCreated >= runStart));
        int tdrCount = SafeCount(() => finalEventLog.ReadGpuTdrEvents().Count(e => e.TimeCreated >= runStart));

        var criteria = new StressTestCriteria
        {
            ComputationChecked = computationChecked,
            ComputationOk = computationOk,
            NoWheaDelta = wheaCount == 0,
            NoTdr = tdrCount == 0,
            ClockChecked = clockChecked,
            SustainedClockAtOrAboveBase = sustainedClockOk,
            PeakTempBelowThrottlePoint = peakTempOk,
            Aborted = abortReason is not null,
            AbortReason = abortReason,
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
        };

        var previous = StressTestHistoryService.FindMostRecentOfSameType(_historyEntries, type, runStart);
        _historyEntries = StressTestHistoryService.Append(historyEntry);
        RunHistory.Clear();
        foreach (var e in _historyEntries.Take(20)) RunHistory.Add(e);

        LastComparisonText = previous is not null
            ? StressTestReportService.BuildComparisonText(result, previous)
            : "No previous run of this test type to compare against yet.";

        StatusText = result.Passed
            ? "Run PASSED."
            : abortReason is not null
                ? $"Run ABORTED: {abortReason}."
                : "Run FAILED - see the criteria breakdown below.";
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
            var previous = StressTestHistoryService.FindMostRecentOfSameType(_historyEntries, result.TestType, result.StartedAt);
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
    public void Dispose() => Stop();

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
