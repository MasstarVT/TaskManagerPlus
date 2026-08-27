using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
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
    /// #84: throttle reason breakdown - a single "Thermal" / "Power" / "None" readout combining
    /// IsThrottling and IsPowerLimited above into the one question they're both really answering
    /// ("why is this CPU running below base clock right now, if it is"). Not a third, independent
    /// signal - LibreHardwareMonitorLib exposes no CPU "limit reason" API on most consumer
    /// hardware (that's the vendor-proprietary MSR data HWiNFO reads directly, the same gap
    /// IsThrottling/IsPowerLimited's own remarks document), so this is exactly as reliable as
    /// those two heuristics, just presented as one readout instead of two separate flags.
    /// </summary>
    public string ThrottleReason => IsThrottling ? "Thermal" : IsPowerLimited ? "Power" : "None";

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

        _throttleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _throttleTimer.Tick += (_, _) => { RefreshThrottleStatus(); _ = RefreshCoreAffinityAsync(); };
        _throttleTimer.Start();
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

        OnPropertyChanged(nameof(ThrottleReason));
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
