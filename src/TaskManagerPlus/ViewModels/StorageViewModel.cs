namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Storage tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer.
/// </summary>
public sealed class StorageViewModel
{
    public PerformanceViewModel Performance { get; }

    public StorageViewModel(PerformanceViewModel performance)
    {
        Performance = performance;
    }
}
