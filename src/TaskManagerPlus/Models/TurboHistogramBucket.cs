using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>One bucket of the CPU tab's turbo-boost time-at-frequency histogram (Round 8 #27) -
/// what percentage of this session's samples fell into this base-clock-relative range. See
/// PerformanceViewModel's TurboHistogram remarks for the bucket boundaries.</summary>
public sealed class TurboHistogramBucket : ObservableObject
{
    public string Label { get; init; } = string.Empty;

    private double _percent;
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
}
