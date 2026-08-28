using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the GPU tab (#53-56). Unlike CPU/Memory/Storage/Network (thin wrappers over the shared
/// PerformanceViewModel/HardwareMonitorService sampler), this owns its own GpuMonitorService and
/// DispatcherTimer - GPU Engine/GPU Adapter Memory perf-counter enumeration is a genuinely separate,
/// heavier data source (dynamic instance discovery every tick, not a fixed counter array), the same
/// "doesn't fit the shared sampler" reasoning EnergyThermalsViewModel already documents for its own
/// independent LibreHardwareMonitorLib poll.
/// </summary>
public sealed class GpuViewModel : ObservableObject, IDisposable
{
    private readonly GpuMonitorService _service = new();
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;

    public bool IsAvailable => _service.IsAvailable;

    public ObservableCollection<GpuAdapterIdentity> InstalledAdapters { get; } = new();
    public ObservableCollection<GpuAdapterSnapshot> LiveAdapters { get; } = new();

    /// <summary>#56: "which app is using it" - the same per-process GPU% Round 4 already samples
    /// (ProcessMonitorService.ReadGpuUsageByPid), just re-presented live-sorted here rather than
    /// sampled a second time - the same "second ICollectionView over one shared collection" pattern
    /// MemoryViewModel.TopMemoryProcesses already established.</summary>
    public ICollectionView TopGpuProcesses { get; }

    public GpuViewModel(ProcessesViewModel processes)
    {
        foreach (var a in _service.Adapters) InstalledAdapters.Add(a);

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.GpuPercent));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.GpuPercent), ListSortDirection.Descending));
        TopGpuProcesses = view;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!IsAvailable) return;
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => _service.Sample());
            LiveAdapters.Clear();
            foreach (var a in snapshot) LiveAdapters.Add(a);
        }
        catch
        {
            // Best-effort - a failed sample shouldn't crash the timer loop.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _service.Dispose();
    }
}
