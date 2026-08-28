using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>Live usage percentage for a single logical CPU core, used by the per-core tile grid.</summary>
public sealed class CoreUsage : ObservableObject
{
    public int Index { get; init; }

    /// <summary>Round 8 #26: the other logical core index sharing this one's physical core (SMT/
    /// Hyper-Threading sibling), -1 when this core has no sibling (SMT off, or a non-hybrid
    /// single-thread-per-core CPU). Folded directly into Label below rather than a separate
    /// SubText tag, since SubText is already spoken for by the Parked/P-core/E-core tags.</summary>
    public int SiblingIndex { get; init; } = -1;

    public string Label => SiblingIndex >= 0 ? $"CPU {Index} ↔{SiblingIndex}" : $"CPU {Index}";

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

    /// <summary>#630: this core's "OS requested" minus "silicon delivered" clock, in percentage
    /// points of rated max frequency ("% Processor Performance" minus "% of Maximum Frequency") -
    /// a persistent positive gap means the OS is asking for more than the silicon is actually
    /// delivering. Null when either underlying perf counter wasn't available on this Windows/CPU
    /// generation - never fabricated.</summary>
    private double? _frequencyGapPoints;
    public double? FrequencyGapPoints { get => _frequencyGapPoints; set => SetProperty(ref _frequencyGapPoints, value); }
}
