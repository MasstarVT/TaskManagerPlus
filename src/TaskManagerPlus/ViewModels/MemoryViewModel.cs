namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Memory tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer.
/// </summary>
public sealed class MemoryViewModel
{
    public PerformanceViewModel Performance { get; }

    public MemoryViewModel(PerformanceViewModel performance)
    {
        Performance = performance;
    }
}
