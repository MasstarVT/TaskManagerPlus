namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the CPU tab. Deliberately owns no timer or HardwareMonitorService of its own -
/// it's a thin composition over the single shared PerformanceViewModel sampler (same pattern
/// as SummaryViewModel), since CPU/Memory/Storage/Network all come from one
/// HardwareMonitorService.Sample() call per tick. Splitting each into its own sampler would
/// mean redundant PerformanceCounter instantiation for identical underlying data.
/// </summary>
public sealed class CpuViewModel
{
    public PerformanceViewModel Performance { get; }

    public CpuViewModel(PerformanceViewModel performance)
    {
        Performance = performance;
    }
}
