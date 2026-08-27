using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>Live usage percentage for a single logical CPU core, used by the per-core tile grid.</summary>
public sealed class CoreUsage : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"CPU {Index}";

    /// <summary>NUMA node this logical core belongs to, from CpuTopologyService. 0 when the
    /// system has a single node (the common case) or when topology couldn't be determined.</summary>
    public int NumaNode { get; init; }

    /// <summary>True for performance cores on a hybrid CPU. Only meaningful when the owning
    /// PerformanceViewModel's HasHybridTopology is true - ignore otherwise (every core defaults
    /// to true, i.e. "not efficiency-tinted", on non-hybrid CPUs).</summary>
    public bool IsPCore { get; init; } = true;

    private double _percent;
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }

    /// <summary>True when Windows has parked this logical core to save power (#78) - a common,
    /// otherwise invisible reason only some cores appear to be doing anything under light load.</summary>
    private bool _isParked;
    public bool IsParked { get => _isParked; set => SetProperty(ref _isParked, value); }
}
