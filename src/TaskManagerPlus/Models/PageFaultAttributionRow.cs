namespace TaskManagerPlus.Models;

/// <summary>#433: one process's page-fault-rate ranking from a 30-second "Find what's paging"
/// sampling pass (repeated Process\Page Faults/sec reads via ProcessPerfCounterService) - see
/// MemoryViewModel.ScanPageFaultsAsync. Windows exposes no per-process *hard*-fault counter, only
/// total (soft+hard) Page Faults/sec, so this is a proxy used to see which processes were driving
/// paging activity during the window, not a literal per-process hard-fault count.</summary>
public sealed class PageFaultAttributionRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double AvgPageFaultsPerSec { get; init; }
    public double PeakPageFaultsPerSec { get; init; }
}
