namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Network tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer.
/// </summary>
public sealed class NetworkViewModel
{
    public PerformanceViewModel Performance { get; }

    public NetworkViewModel(PerformanceViewModel performance)
    {
        Performance = performance;
    }
}
