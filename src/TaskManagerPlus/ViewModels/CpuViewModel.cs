using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One NUMA node's worth of cores, for the CPU tab's per-core grid.</summary>
public sealed class CoreGroup
{
    public int NumaNode { get; init; }
    public IReadOnlyList<CoreUsage> Cores { get; init; } = Array.Empty<CoreUsage>();
}

/// <summary>
/// Backs the CPU tab. Deliberately owns no HardwareMonitorService of its own - it's a thin
/// composition over the single shared PerformanceViewModel sampler (same pattern as
/// SummaryViewModel), since CPU/Memory/Storage/Network all come from one
/// HardwareMonitorService.Sample() call per tick. Splitting each into its own sampler would
/// mean redundant PerformanceCounter instantiation for identical underlying data. It does own
/// one small timer of its own (#9's thermal-throttle flag), since that's a composite reading
/// across two other view-models (Performance and EnergyThermals) ticking on different intervals -
/// the same "cheap, no I/O of its own" tradeoff SummaryViewModel's Health Check timer makes.
/// </summary>
public sealed class CpuViewModel : ObservableObject, IDisposable
{
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly ProcessesViewModel _processes;
    private readonly DispatcherTimer _throttleTimer;
    private bool _affinityRefreshInFlight;

    public PerformanceViewModel Performance { get; }

    /// <summary>#608/#603: exposes the shared EnergyThermalsViewModel instance so CpuView.xaml can
    /// bind directly to its thermal-headroom/thermal-zone/firmware-event properties (e.g.
    /// EnergyThermals.CpuThermalHeadroomC) without new cross-ViewModel plumbing - the same
    /// "expose the composed instance as a public property" shape Performance above already uses.</summary>
    public EnergyThermalsViewModel EnergyThermals => _energyThermals;

    /// <summary>Round 8 #25/#28/#29/#30: static CPU identification readouts (microcode,
    /// mitigation override status, instruction-set support, cache sizes) - queried once in the
    /// constructor via CpuFeatureService, not per tick, since none of it changes at runtime.</summary>
    private CpuFeatureInfo _features = new();
    public CpuFeatureInfo Features { get => _features; private set => SetProperty(ref _features, value); }

    /// <summary>Round 8 #24: best-effort core-affinity heatmap for the current top-CPU processes -
    /// see CoreAffinityService's remarks for why this is framed as "preferred/ideal core", not a
    /// live trace of actual scheduling. Refreshed on the same 2s cadence as the throttle flag,
    /// off the UI thread since it walks native per-thread calls.</summary>
    public ObservableCollection<CoreAffinityCell> CoreAffinity { get; } = new();

    /// <summary>
    /// Best-effort "is the CPU thermal-throttling right now" flag (#9) - true only when the CPU
    /// is both running hot (LibreHardwareMonitorLib package temp) AND meaningfully below its
    /// rated base clock (PerformanceViewModel.CpuVsBasePercent) while under real load. This is a
    /// heuristic, not a verified fact: Windows/LibreHardwareMonitorLib expose no "throttle reason"
    /// API on most consumer hardware (that's vendor-proprietary MSR data HWiNFO reads directly),
    /// so a CPU that's simply idle-clocked, or throttling for a non-thermal reason (power/current
    /// limit), won't necessarily show this flag correctly - the same "quick visual flag, not a
    /// verdict" tradeoff the process signature check and driver-date filtering already document.
    /// </summary>
    private bool _isThrottling;
    public bool IsThrottling { get => _isThrottling; private set => SetProperty(ref _isThrottling, value); }

    private string _throttleText = string.Empty;
    public string ThrottleText { get => _throttleText; private set => SetProperty(ref _throttleText, value); }

    /// <summary>
    /// Best-effort "the CPU looks power-limited rather than thermal-limited" flag (#35) - true
    /// when the package is pinned at (or very near) its own highest power draw seen this session
    /// while running meaningfully below base clock under load, but is NOT also reading hot -
    /// distinct from IsThrottling above, which requires a hot package temp. The practical
    /// difference matters for troubleshooting: a power ceiling points at a PSU/motherboard power
    /// limit or a vendor-set PL1/PL2 cap, not the cooler. Same heuristic tier as IsThrottling -
    /// this app has no access to the vendor-proprietary "limit reason" MSR data HWiNFO reads
    /// directly, so this is inferred from two independently-ticking view-models, not a verified fact.
    /// </summary>
    private bool _isPowerLimited;
    public bool IsPowerLimited { get => _isPowerLimited; private set => SetProperty(ref _isPowerLimited, value); }

    private string _powerLimitText = string.Empty;
    public string PowerLimitText { get => _powerLimitText; private set => SetProperty(ref _powerLimitText, value); }

    /// <summary>
    /// #84/#603: throttle-reason breakdown - now a full Thermal/Power/Firmware/Core-parked/None
    /// classification (ThrottleClassificationService.Classify, shared with
    /// EnergyThermalsViewModel's own episode tracking, #604) rather than just the two-way
    /// Thermal/Power/None readout this was before. Still exactly as reliable as before for the
    /// Thermal/Power/Core-parked cases - this app has no access to the vendor-proprietary "limit
    /// reason" MSR data HWiNFO reads directly - except Firmware, which is corroborated by an
    /// authoritative Windows event (#602) rather than a pattern match.
    /// </summary>
    private ThrottleReasonClass _currentThrottleClass = ThrottleReasonClass.None;
    public ThrottleReasonClass CurrentThrottleClass { get => _currentThrottleClass; private set => SetProperty(ref _currentThrottleClass, value); }

    public string ThrottleReason => CurrentThrottleClass switch
    {
        ThrottleReasonClass.Thermal => "Thermal",
        ThrottleReasonClass.Power => "Power",
        ThrottleReasonClass.Firmware => "Firmware",
        ThrottleReasonClass.CoreParked => "Core-parked",
        _ => "None",
    };

    // #603: dwell time per reason class, accumulated across the session on this view-model's own
    // 2s timer, presented as a single stacked bar ("what is actually holding my clocks back")
    // rather than just the instantaneous flags above.
    private readonly Dictionary<ThrottleReasonClass, double> _dwellSeconds = new()
    {
        [ThrottleReasonClass.None] = 0,
        [ThrottleReasonClass.Thermal] = 0,
        [ThrottleReasonClass.Power] = 0,
        [ThrottleReasonClass.Firmware] = 0,
        [ThrottleReasonClass.CoreParked] = 0,
    };
    private DateTime _lastDwellTick = DateTime.Now;

    public ObservableCollection<double> NoneDwellShare { get; } = new() { 100 };
    public ObservableCollection<double> ThermalDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> PowerDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> FirmwareDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> CoreParkedDwellShare { get; } = new() { 0 };
    public ISeries[] ThrottleDwellSeries { get; }
    public Axis[] ThrottleDwellXAxes { get; }
    public Axis[] ThrottleDwellYAxes { get; }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    /// <summary>Pass-through: true only on genuinely hybrid CPUs. The view should hide the
    /// P-core/E-core color distinction entirely when this is false.</summary>
    public bool HasHybridTopology => Performance.HasHybridTopology;

    /// <summary>Pass-through: true only when the system has more than one NUMA node. The view
    /// should render a single flat core grid (no group headers) when this is false.</summary>
    public bool HasMultipleNumaNodes => Performance.HasMultipleNumaNodes;

    /// <summary>
    /// Performance.Cores grouped by NUMA node, each group rendered as its own nested
    /// WrapPanel-hosted ItemsControl in the view. Built manually here (rather than via
    /// ICollectionView.GroupDescriptions + ItemsControl.GroupStyle) - NUMA node is static per
    /// core, so this only needs to rebuild when the Cores collection itself is replaced
    /// (core-count change), not per tick, and a plain nested ItemsControl avoids depending on
    /// WPF's grouping/GroupStyle machinery correctly detecting an externally-grouped
    /// CollectionView, which turned out not to lay out as a wrapping grid in practice.
    /// </summary>
    public ObservableCollection<CoreGroup> CoreGroups { get; } = new();

    public CpuViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals, ProcessesViewModel processes)
    {
        Performance = performance;
        _energyThermals = energyThermals;
        _processes = processes;
        performance.Cores.CollectionChanged += OnCoresCollectionChanged;
        RebuildGroups();

        // #603: single-category stacked bar - one "Session" column, five stacked segments (one
        // per reason class) sized by that class's share of dwell time so far. StackedColumnSeries
        // instances sharing no explicit StackGroup stack together by default.
        ThrottleDwellXAxes = new[]
        {
            new Axis { Labels = new[] { "Session" }, LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = null },
        };
        ThrottleDwellYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MaxLimit = 100,
                Labeler = v => $"{v:0}%",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        ThrottleDwellSeries = new ISeries[]
        {
            new StackedColumnSeries<double> { Values = NoneDwellShare, Name = "None", Fill = new SolidColorPaint(SKColors.Gray.WithAlpha(130)), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = ThermalDwellShare, Name = "Thermal", Fill = new SolidColorPaint(SKColors.OrangeRed), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = PowerDwellShare, Name = "Power", Fill = new SolidColorPaint(SKColors.Goldenrod), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = FirmwareDwellShare, Name = "Firmware", Fill = new SolidColorPaint(SKColors.MediumPurple), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = CoreParkedDwellShare, Name = "Core-parked", Fill = new SolidColorPaint(SKColors.DeepSkyBlue), Stroke = null, MaxBarWidth = 70 },
        };

        _throttleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _throttleTimer.Tick += (_, _) => { RefreshThrottleStatus(); _ = RefreshCoreAffinityAsync(); };
        _throttleTimer.Start();
        _lastDwellTick = DateTime.Now;
        RefreshThrottleStatus();

        // #25/#28/#29/#30: static, so read once in the background rather than adding this to the
        // per-tick timer above.
        _ = Task.Run(() =>
        {
            var features = CpuFeatureService.Read();
            System.Windows.Application.Current?.Dispatcher.Invoke(() => Features = features);
        });
    }

    /// <summary>#24: samples the current top-few CPU processes' threads' ideal processors and
    /// rebuilds the per-core heatmap. Guarded against overlap - the native per-thread scan can
    /// legitimately take longer than 2s on a very busy system, and this must never stack calls.</summary>
    private async Task RefreshCoreAffinityAsync()
    {
        if (_affinityRefreshInFlight) return;
        _affinityRefreshInFlight = true;
        try
        {
            var topPids = _processes.Processes.OrderByDescending(p => p.CpuPercent).Take(6).Select(p => p.Pid).ToList();
            int logicalCount = Performance.Cores.Count;

            var cells = await Task.Run(() =>
            {
                var procs = new List<System.Diagnostics.Process>();
                foreach (var pid in topPids)
                {
                    try { procs.Add(System.Diagnostics.Process.GetProcessById(pid)); }
                    catch { /* process exited between the snapshot above and here - skip it */ }
                }

                try
                {
                    var byCore = CoreAffinityService.ComputeIdealProcessorLoad(procs);
                    var list = new List<CoreAffinityCell>(logicalCount);
                    for (int i = 0; i < logicalCount; i++)
                    {
                        var entries = byCore.TryGetValue(i, out var e) ? e : new List<(string ProcessName, int Pid)>();
                        list.Add(new CoreAffinityCell
                        {
                            CoreIndex = i,
                            ProcessCount = entries.Count,
                            ProcessNames = entries.Count == 0 ? string.Empty : string.Join(", ", entries.Select(x => x.ProcessName).Distinct()),
                        });
                    }
                    return list;
                }
                finally
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { /* best-effort */ }
                    }
                }
            });

            CoreAffinity.Clear();
            foreach (var c in cells) CoreAffinity.Add(c);
        }
        finally
        {
            _affinityRefreshInFlight = false;
        }
    }

    private void RefreshThrottleStatus()
    {
        var temp = _energyThermals.CpuPackageTempC;
        bool hot = temp is { } t && t >= 85;
        bool highLoad = Performance.CpuCurrentPercent >= 60;
        bool belowBase = Performance.CpuVsBasePercent <= -8;

        IsThrottling = hot && highLoad && belowBase;
        ThrottleText = IsThrottling
            ? $"{temp:0}°C and {Performance.CpuVsBasePercent:0}% vs. base clock under load"
            : string.Empty;

        // #35: pinned at (within 3%) its own session-high power draw, below base clock, under
        // load, but NOT also reading hot - the "power ceiling, not thermal ceiling" signature.
        var power = _energyThermals.TotalPackagePowerW;
        var powerMax = _energyThermals.PowerSessionMaxW;
        bool atPowerCeiling = power is { } p && powerMax is { } max && max > 0 && p >= max * 0.97;
        IsPowerLimited = !IsThrottling && !hot && highLoad && belowBase && atPowerCeiling;
        PowerLimitText = IsPowerLimited
            ? $"{power:0.#} W (session high {powerMax:0.#} W) and {Performance.CpuVsBasePercent:0}% vs. base clock under load"
            : string.Empty;

        // #603: full reason-class verdict, using the shared classifier so this agrees with
        // EnergyThermalsViewModel's own episode-tracking classification (#604/#601/#602) - pulls
        // in the thermal-zone throttle% (#601) and firmware-limit-active snapshot (#602)
        // EnergyThermalsViewModel already owns, plus this view-model's own Performance reference
        // for parked-core count.
        var zoneThrottlePercents = _energyThermals.ThermalZones.Where(z => z.ThrottlePercent.HasValue).Select(z => z.ThrottlePercent!.Value).ToList();
        double? maxZoneThrottle = zoneThrottlePercents.Count > 0 ? zoneThrottlePercents.Max() : null;
        CurrentThrottleClass = ThrottleClassificationService.Classify(
            temp, Performance.CpuCurrentPercent, Performance.CpuVsBasePercent,
            power, powerMax, Performance.ParkedCoreCount, Performance.Cores.Count,
            maxZoneThrottle, _energyThermals.FirmwareLimitActive);

        // #603: accumulate dwell time per class since this view-model was constructed.
        var now = DateTime.Now;
        double elapsed = Math.Max(0, (now - _lastDwellTick).TotalSeconds);
        _lastDwellTick = now;
        _dwellSeconds[CurrentThrottleClass] += elapsed;
        UpdateDwellShares();

        OnPropertyChanged(nameof(ThrottleReason));
    }

    /// <summary>#603: recomputes each reason class's share of total dwell time (0-100) and pushes
    /// the new values into the stacked-bar series' backing collections.</summary>
    private void UpdateDwellShares()
    {
        double total = _dwellSeconds.Values.Sum();
        if (total <= 0) return;

        NoneDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.None] / total * 100, 1);
        ThermalDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Thermal] / total * 100, 1);
        PowerDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Power] / total * 100, 1);
        FirmwareDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Firmware] / total * 100, 1);
        CoreParkedDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.CoreParked] / total * 100, 1);
    }

    /// <summary>Repaints the dwell-breakdown chart's axis text/gridlines to match the active theme
    /// family - see PerformanceViewModel.ApplyAxisTheme's remarks; same SkiaSharp-outside-WPF-
    /// resources gap.</summary>
    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        ThrottleDwellXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ThrottleDwellYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ThrottleDwellYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private void OnCoresCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Per-tick updates only mutate existing CoreUsage.Percent in place (no Add/Remove), so
        // this only fires on the initial populate or an actual core-count change - cheap to
        // rebuild fully rather than trying to patch the grouping incrementally.
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Reset)
        {
            RebuildGroups();
        }
    }

    private void RebuildGroups()
    {
        CoreGroups.Clear();
        foreach (var group in Performance.Cores.GroupBy(c => c.NumaNode).OrderBy(g => g.Key))
            CoreGroups.Add(new CoreGroup { NumaNode = group.Key, Cores = group.OrderBy(c => c.Index).ToList() });
    }

    public void Dispose() => _throttleTimer.Stop();
}
