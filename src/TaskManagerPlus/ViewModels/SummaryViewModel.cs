using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Summary dashboard page. Mostly a thin composition over the Performance/Processes
/// view-models MainViewModel already polls, the same live data just re-presented as a mosaic of
/// small widgets - the one exception is the Health Check card (#64), which owns a lightweight
/// timer of its own to recompute a rule-based issue list. That timer does no I/O of its own: every
/// rule just reads state already live on other view-models, so it's cheap enough to not need to
/// ride the 1s shared sampler tick.
/// </summary>
public sealed class SummaryViewModel : ObservableObject, IDisposable
{
    private readonly ServicesViewModel _services;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly NetworkViewModel _network;
    private readonly StabilityViewModel _stability;
    private readonly DispatcherTimer _healthTimer;

    public PerformanceViewModel Performance { get; }
    public ProcessesViewModel Processes { get; }

    /// <summary>All processes, live-sorted by CPU% descending, for the "Top processes" card.</summary>
    public ICollectionView TopProcesses { get; }

    /// <summary>All processes, live-sorted by the rolling ~10s CPU average descending (#11) -
    /// "what's actually been eating CPU over the last several seconds", a steadier answer than
    /// TopProcesses' instantaneous per-tick reading for a bursty process.</summary>
    public ICollectionView TopProcesses10sAvg { get; }

    public ObservableCollection<HealthIssue> HealthIssues { get; } = new();

    // #72: configurable threshold alerts - a toast fires once when a metric crosses above its
    // threshold (edge-triggered via the _xAlerted flags below, reset when the metric drops back
    // under, so one sustained excursion doesn't spam a toast every 2s tick).
    private readonly AlertThresholds _alertThresholds = AlertThresholdsService.Load();
    private bool _cpuAlerted, _memoryAlerted, _tempAlerted;

    public bool CpuAlertEnabled { get => _alertThresholds.CpuEnabled; set { _alertThresholds.CpuEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double CpuAlertThreshold { get => _alertThresholds.CpuPercent; set { _alertThresholds.CpuPercent = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public bool MemoryAlertEnabled { get => _alertThresholds.MemoryEnabled; set { _alertThresholds.MemoryEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double MemoryAlertThreshold { get => _alertThresholds.MemoryPercent; set { _alertThresholds.MemoryPercent = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public bool TempAlertEnabled { get => _alertThresholds.TempEnabled; set { _alertThresholds.TempEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double TempAlertThreshold { get => _alertThresholds.TempC; set { _alertThresholds.TempC = value; OnPropertyChanged(); PersistAlertThresholds(); } }

    private void PersistAlertThresholds() => AlertThresholdsService.Save(_alertThresholds);

    // #73: one-click diagnostic report bundling specs, recent stability events, a sensor
    // snapshot, and top resource consumers into a single shareable markdown file.
    public RelayCommand GenerateReportCommand { get; }

    public SummaryViewModel(PerformanceViewModel performance, ProcessesViewModel processes,
        ServicesViewModel services, EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs, NetworkViewModel network, StabilityViewModel stability)
    {
        Performance = performance;
        Processes = processes;
        _services = services;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _network = network;
        _stability = stability;

        GenerateReportCommand = new RelayCommand(_ => GenerateReport());

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent), ListSortDirection.Descending));
        TopProcesses = view;

        var view10s = new CollectionViewSource { Source = processes.Processes }.View;
        if (view10s is ICollectionViewLiveShaping liveShaping10s && liveShaping10s.CanChangeLiveSorting)
        {
            liveShaping10s.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent10sAvg));
            liveShaping10s.IsLiveSorting = true;
        }
        view10s.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent10sAvg), ListSortDirection.Descending));
        TopProcesses10sAvg = view10s;

        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) => { RefreshHealthIssues(); CheckThresholdAlerts(); };
        _healthTimer.Start();
        RefreshHealthIssues();
    }

    /// <summary>#72: fires a toast the moment a metric crosses above its configured threshold -
    /// edge-triggered so a sustained excursion produces one toast, not one every 2s tick.</summary>
    private void CheckThresholdAlerts()
    {
        CheckOne(CpuAlertEnabled, Performance.CpuCurrentPercent, CpuAlertThreshold, ref _cpuAlerted,
            "CPU usage threshold", v => $"CPU usage is {v:0}% (threshold {CpuAlertThreshold:0}%)");

        CheckOne(MemoryAlertEnabled, Performance.RamPercent, MemoryAlertThreshold, ref _memoryAlerted,
            "Memory usage threshold", v => $"Memory usage is {v:0}% (threshold {MemoryAlertThreshold:0}%)");

        if (_energyThermals.CpuPackageTempC is { } temp)
        {
            CheckOne(TempAlertEnabled, temp, TempAlertThreshold, ref _tempAlerted,
                "CPU temperature threshold", v => $"CPU temperature is {v:0}°C (threshold {TempAlertThreshold:0}°C)");
        }
    }

    private static void CheckOne(bool enabled, double value, double threshold, ref bool alerted, string title, Func<double, string> message)
    {
        if (!enabled) { alerted = false; return; }

        if (value >= threshold)
        {
            if (!alerted)
            {
                alerted = true;
                ToastService.Show(title, message(value), isCritical: true);
            }
        }
        else
        {
            alerted = false;
        }
    }

    /// <summary>#73: bundles system specs, recent stability events, a sensor snapshot, and top
    /// resource consumers into one shareable markdown file - the same kind of report a forum post
    /// or support ticket would otherwise need assembled by hand from several different tabs.</summary>
    private void GenerateReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Generate diagnostic report",
            Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"TaskManagerPlus-Report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildReportMarkdown());
        }
        catch
        {
            // Best-effort - a failed write shouldn't crash the app; the user can just retry.
        }
    }

    private string BuildReportMarkdown()
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line($"# Task Manager Plus diagnostic report");
        Line($"Generated {DateTime.Now:F}");
        Line();

        Line("## System");
        Line($"- OS: {_systemSpecs.OsName} ({_systemSpecs.OsDetails})");
        Line($"- Model: {_systemSpecs.SystemModel}");
        Line($"- CPU: {_systemSpecs.CpuName} — {_systemSpecs.CpuDetails}");
        Line($"- RAM: {_systemSpecs.RamTotal} ({_systemSpecs.RamDetails})");
        Line();

        Line("## Current load");
        Line($"- CPU: {Performance.CpuCurrentPercent:0.0}% @ {Performance.CpuCurrentClockGhz:0.00} GHz");
        Line($"- Memory: {Performance.RamUsedGb:0.0} / {Performance.RamTotalGb:0.0} GB ({Performance.RamPercent:0.0}%)");
        Line($"- Disk activity: {Performance.DiskPercent:0.0}%");
        Line($"- Network: ↓{Formatting.FormatByteRate(Performance.NetworkReceiveBps)} ↑{Formatting.FormatByteRate(Performance.NetworkSendBps)}");
        if (_energyThermals.CpuPackageTempC is { } temp) Line($"- CPU package temperature: {temp:0.#}°C");
        if (_energyThermals.TotalPackagePowerW is { } power) Line($"- CPU package power draw: {power:0.#} W");
        Line();

        Line("## Health Check");
        if (HealthIssues.Count == 0)
        {
            Line("No issues detected.");
        }
        else
        {
            foreach (var issue in HealthIssues) Line($"- {(issue.IsCritical ? "**Critical**" : "Warning")}: {issue.Message}");
        }
        Line();

        Line("## Recent stability events");
        Line($"- Last unexpected shutdown: {_stability.LastUnexpectedShutdownText}");
        Line($"- Time since last crash: {_stability.TimeSinceLastCrashText}");
        Line($"- GPU driver resets (TDR) in the last 30 days: {_stability.TdrEventCount}");
        if (_stability.RecentEvents.Count > 0)
        {
            Line();
            Line("| Time | Log | Provider | Event ID | Message |");
            Line("|---|---|---|---|---|");
            foreach (var e in _stability.RecentEvents.Take(15))
            {
                var message = e.Message.Replace("\n", " ").Replace("\r", "").Replace("|", "\\|");
                if (message.Length > 120) message = message[..120] + "…";
                Line($"| {e.TimeCreated:g} | {e.LogName} | {e.ProviderName} | {e.EventId} | {message} |");
            }
        }
        Line();

        Line("## Top CPU processes");
        Line("| Process | PID | CPU % | Memory |");
        Line("|---|---|---|---|");
        foreach (var p in Processes.Processes.OrderByDescending(p => p.CpuPercent).Take(10))
            Line($"| {p.Name} | {p.Pid} | {p.CpuPercent:0.0} | {Formatting.FormatBytes(p.MemoryBytes)} |");
        Line();

        Line("## Top memory processes");
        Line("| Process | PID | Memory | CPU % |");
        Line("|---|---|---|---|");
        foreach (var p in Processes.Processes.OrderByDescending(p => p.MemoryBytes).Take(10))
            Line($"| {p.Name} | {p.Pid} | {Formatting.FormatBytes(p.MemoryBytes)} | {p.CpuPercent:0.0} |");

        return sb.ToString();
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

        // #66: Windows Defender real-time scan activity heuristic - no new sampling needed, this
        // just reads the CPU% the Processes tab is already polling for MsMpEng.exe. Sustained high
        // CPU on the Defender engine process is the common, otherwise invisible "why is my PC slow
        // right now" answer an active scan gives - a heuristic, not a verified "scan in progress"
        // API (Windows exposes none), the same tier as the process signature check.
        var defender = Processes.Processes.FirstOrDefault(p => p.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase));
        if (defender is not null && defender.CpuPercent >= 20)
            issues.Add(new HealthIssue { Message = $"Windows Defender may be actively scanning (MsMpEng at {defender.CpuPercent:0}% CPU)", IsCritical = false });

        // #67: anomaly highlighting - flags CPU/RAM/Disk usage that's a statistical outlier vs.
        // its own last-minute history, even without a fixed threshold. Requires both a meaningful
        // raw jump (>=20 points) AND a real statistical deviation (>=3 std dev past a small floor)
        // so a metric that's merely a little above its own noisy baseline isn't flagged.
        CheckAnomaly(issues, "CPU", Performance.CpuHistory, Performance.CpuCurrentPercent);
        CheckAnomaly(issues, "Memory", Performance.RamHistory, Performance.RamPercent);
        CheckAnomaly(issues, "Disk activity", Performance.DiskHistory, Performance.DiskPercent);

        // Replace in place only when the content actually changed, so the UI doesn't flicker/
        // lose scroll position on every 2s tick when nothing is different.
        if (!issues.Select(i => i.Message).SequenceEqual(HealthIssues.Select(i => i.Message)))
        {
            HealthIssues.Clear();
            foreach (var issue in issues) HealthIssues.Add(issue);
        }
    }

    /// <summary>#67: flags `current` as an outlier vs. the mean/stddev of `history` (which already
    /// includes `current` as its most recent sample - PerformanceViewModel pushes each tick's
    /// value before this timer's own tick runs). A minimum stddev floor keeps a near-flat history
    /// from making every tiny blip look like a 3-sigma event.</summary>
    private static void CheckAnomaly(List<HealthIssue> issues, string label, ObservableCollection<double> history, double current)
    {
        if (history.Count < 30) return; // not enough history yet for a meaningful baseline

        var baseline = history.Take(history.Count - 1).ToArray(); // exclude current from its own baseline
        if (baseline.Length < 10) return;

        double mean = baseline.Average();
        double variance = baseline.Sum(v => (v - mean) * (v - mean)) / baseline.Length;
        double std = Math.Max(Math.Sqrt(variance), 3.0); // floor so a flat history doesn't over-trigger

        if (current - mean >= 20 && current - mean >= 3 * std)
        {
            issues.Add(new HealthIssue
            {
                Message = $"{label} usage is unusually high vs. the last minute (now {current:0}%, typically {mean:0}%)",
                IsCritical = false,
            });
        }
    }

    public void Dispose() => _healthTimer.Stop();
}
