using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>Live usage percentage for a single logical CPU core, used by the per-core tile grid.</summary>
public sealed class CoreUsage : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"CPU {Index}";

    private double _percent;
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
}
