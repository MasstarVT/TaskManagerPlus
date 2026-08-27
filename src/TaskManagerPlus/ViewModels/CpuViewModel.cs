using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

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
    private readonly DispatcherTimer _throttleTimer;

    public PerformanceViewModel Performance { get; }

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

    public CpuViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        Performance = performance;
        _energyThermals = energyThermals;
        performance.Cores.CollectionChanged += OnCoresCollectionChanged;
        RebuildGroups();

        _throttleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _throttleTimer.Tick += (_, _) => RefreshThrottleStatus();
        _throttleTimer.Start();
        RefreshThrottleStatus();
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
