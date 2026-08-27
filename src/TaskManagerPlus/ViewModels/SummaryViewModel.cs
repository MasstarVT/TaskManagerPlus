using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Summary dashboard page. Mostly a thin composition over the Performance/Processes
/// view-models MainViewModel already polls, the same live data just re-presented as a mosaic of
/// small widgets - the one exception is the Health Check card (#64), which owns a lightweight
/// timer of its own to recompute a rule-based issue list. That timer does no I/O of its own: every
/// rule just reads state already live on other view-models, so it's cheap enough to not need to
/// ride the 1s shared sampler tick.
/// </summary>
public sealed class SummaryViewModel : IDisposable
{
    private readonly ServicesViewModel _services;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly NetworkViewModel _network;
    private readonly DispatcherTimer _healthTimer;

    public PerformanceViewModel Performance { get; }
    public ProcessesViewModel Processes { get; }

    /// <summary>All processes, live-sorted by CPU% descending, for the "Top processes" card.</summary>
    public ICollectionView TopProcesses { get; }

    public ObservableCollection<HealthIssue> HealthIssues { get; } = new();

    public SummaryViewModel(PerformanceViewModel performance, ProcessesViewModel processes,
        ServicesViewModel services, EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs, NetworkViewModel network)
    {
        Performance = performance;
        Processes = processes;
        _services = services;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _network = network;

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent), ListSortDirection.Descending));
        TopProcesses = view;

        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) => RefreshHealthIssues();
        _healthTimer.Start();
        RefreshHealthIssues();
    }

    /// <summary>CPU package temperature past this point reads as "running hot" for the Health
    /// Check card - a rough, conservative threshold (most desktop/laptop CPUs throttle well
    /// above this), not a precise per-model limit.</summary>
    private const double HotCpuTempC = 90.0;

    private void RefreshHealthIssues()
    {
        var issues = new List<HealthIssue>();

        foreach (var volume in _systemSpecs.Volumes)
        {
            if (volume.PercentUsed >= 90)
                issues.Add(new HealthIssue { Message = $"{volume.Primary} is {volume.PercentUsed:0}% full", IsCritical = volume.PercentUsed >= 97 });
            if (volume.IsDirty)
                issues.Add(new HealthIssue { Message = $"{volume.Primary} needs a chkdsk pass (dirty bit set)", IsCritical = true });
        }

        foreach (var disk in _systemSpecs.Disks)
        {
            if (disk.IsHealthWarning)
                issues.Add(new HealthIssue { Message = $"Drive health warning: {disk.Primary} ({disk.HealthText})", IsCritical = true });
        }

        if (_energyThermals.CpuPackageTempC is { } cpuTemp && cpuTemp >= HotCpuTempC)
            issues.Add(new HealthIssue { Message = $"CPU running hot ({cpuTemp:0}°C)", IsCritical = cpuTemp >= 100 });

        if (_energyThermals.DeadFanDetected)
            issues.Add(new HealthIssue { Message = $"Possible stopped fan: {_energyThermals.DeadFanName}", IsCritical = true });

        if (Performance.PageFilePercent >= 90)
            issues.Add(new HealthIssue { Message = $"Page file is {Performance.PageFilePercent:0}% full", IsCritical = false });

        if (Performance.HasNetworkErrors)
            issues.Add(new HealthIssue { Message = "Network adapter errors detected", IsCritical = false });

        int failedServices = _services.Services.Count(s => s.HasFailedToStart);
        if (failedServices > 0)
            issues.Add(new HealthIssue { Message = $"{failedServices} service{(failedServices == 1 ? "" : "s")} failed to start", IsCritical = false });

        if (_systemSpecs.OutdatedDrivers.Count > 0)
            issues.Add(new HealthIssue { Message = $"{_systemSpecs.OutdatedDrivers.Count} driver{(_systemSpecs.OutdatedDrivers.Count == 1 ? "" : "s")} may need updating", IsCritical = false });

        if (_systemSpecs.MultipleActiveAvWarning)
            issues.Add(new HealthIssue { Message = "Multiple antivirus products look active", IsCritical = false });

        // Replace in place only when the content actually changed, so the UI doesn't flicker/
        // lose scroll position on every 2s tick when nothing is different.
        if (!issues.Select(i => i.Message).SequenceEqual(HealthIssues.Select(i => i.Message)))
        {
            HealthIssues.Clear();
            foreach (var issue in issues) HealthIssues.Add(issue);
        }
    }

    public void Dispose() => _healthTimer.Stop();
}
