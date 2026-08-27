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

    // #97: the same report, as a single self-contained HTML file with embedded inline-SVG
    // sparkline charts - no CSV/Excel round-trip needed for someone helping troubleshoot
    // remotely to see the shape of the last minute's CPU/RAM/Disk activity, not just numbers.
    public RelayCommand GenerateHtmlReportCommand { get; }

    // #93/#94: baseline capture + "what changed" comparison - one save/compare pair covers both
    // suggestions, since a saved baseline IS the comparison point for a later diff. See
    // SnapshotService's remarks.
    public RelayCommand SaveSnapshotCommand { get; }
    public RelayCommand CompareSnapshotCommand { get; }

    private SnapshotDiff? _snapshotDiff;
    public SnapshotDiff? SnapshotDiff { get => _snapshotDiff; private set => SetProperty(ref _snapshotDiff, value); }

    private string _snapshotStatusText = string.Empty;
    public string SnapshotStatusText { get => _snapshotStatusText; private set => SetProperty(ref _snapshotStatusText, value); }

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
        GenerateHtmlReportCommand = new RelayCommand(_ => GenerateHtmlReport());
        SaveSnapshotCommand = new RelayCommand(_ => SaveSnapshot());
        CompareSnapshotCommand = new RelayCommand(_ => CompareSnapshot());

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

    /// <summary>#97: the HTML twin of GenerateReport() above - same underlying data, rendered as
    /// one self-contained .html file (inline &lt;style&gt;, inline SVG charts, no external
    /// references) that opens directly in any browser with no CSV/Excel round-trip.</summary>
    private void GenerateHtmlReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Generate HTML diagnostic report",
            Filter = "HTML files (*.html)|*.html|All files (*.*)|*.*",
            DefaultExt = ".html",
            FileName = $"TaskManagerPlus-Report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.html",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildReportHtml());
        }
        catch
        {
            // Best-effort - a failed write shouldn't crash the app; the user can just retry.
        }
    }

    private string BuildReportHtml()
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("<!doctype html><html><head><meta charset=\"utf-8\">");
        Line($"<title>Task Manager Plus report - {Esc(DateTime.Now.ToString("F"))}</title>");
        Line("<style>" +
             "body{font-family:Segoe UI,Arial,sans-serif;background:#1c1c1f;color:#e4e4e7;max-width:900px;margin:32px auto;padding:0 16px}" +
             "h1{font-size:20px}h2{font-size:15px;border-bottom:1px solid #3a3a42;padding-bottom:6px;margin-top:28px}" +
             "table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:4px 8px;text-align:left;border-bottom:1px solid #2c2c33}" +
             ".crit{color:#f26d6d}.warn{color:#e8b23c}.ok{color:#4fd18b}.muted{color:#9a9aa2;font-size:12px}" +
             "svg{background:#242429;border-radius:6px}</style></head><body>");

        Line($"<h1>Task Manager Plus diagnostic report</h1><p class=\"muted\">Generated {Esc(DateTime.Now.ToString("F"))}</p>");

        Line("<h2>System</h2><table>");
        Line($"<tr><td>OS</td><td>{Esc(_systemSpecs.OsName)} ({Esc(_systemSpecs.OsDetails)})</td></tr>");
        Line($"<tr><td>Model</td><td>{Esc(_systemSpecs.SystemModel)}</td></tr>");
        Line($"<tr><td>CPU</td><td>{Esc(_systemSpecs.CpuName)} — {Esc(_systemSpecs.CpuDetails)}</td></tr>");
        Line($"<tr><td>RAM</td><td>{Esc(_systemSpecs.RamTotal)} ({Esc(_systemSpecs.RamDetails)})</td></tr>");
        Line("</table>");

        Line("<h2>Current load (last minute)</h2>");
        Line($"<p>CPU: {Performance.CpuCurrentPercent:0.0}% @ {Performance.CpuCurrentClockGhz:0.00} GHz</p>");
        Line(Sparkline(Performance.CpuHistory, "#3C9EE8"));
        Line($"<p>Memory: {Performance.RamUsedGb:0.0} / {Performance.RamTotalGb:0.0} GB ({Performance.RamPercent:0.0}%)</p>");
        Line(Sparkline(Performance.RamHistory, "#9B7FE0"));
        Line($"<p>Disk activity: {Performance.DiskPercent:0.0}%</p>");
        Line(Sparkline(Performance.DiskHistory, "#E8A23C"));
        if (_energyThermals.CpuPackageTempC is { } temp) Line($"<p>CPU package temperature: {temp:0.#}°C</p>");
        if (_energyThermals.TotalPackagePowerW is { } power) Line($"<p>CPU package power draw: {power:0.#} W</p>");

        Line("<h2>Health Check</h2>");
        if (HealthIssues.Count == 0)
        {
            Line("<p class=\"ok\">No issues detected.</p>");
        }
        else
        {
            Line("<ul>");
            foreach (var issue in HealthIssues)
                Line($"<li class=\"{(issue.IsCritical ? "crit" : "warn")}\">{(issue.IsCritical ? "Critical" : "Warning")}: {Esc(issue.Message)}</li>");
            Line("</ul>");
        }

        Line("<h2>Recent stability</h2><table>");
        Line($"<tr><td>Last unexpected shutdown</td><td>{Esc(_stability.LastUnexpectedShutdownText)}</td></tr>");
        Line($"<tr><td>Time since last crash</td><td>{Esc(_stability.TimeSinceLastCrashText)}</td></tr>");
        Line($"<tr><td>GPU driver resets (30d)</td><td>{_stability.TdrEventCount}</td></tr>");
        Line("</table>");

        Line("<h2>Top CPU processes</h2><table><tr><th>Process</th><th>PID</th><th>CPU %</th><th>Memory</th></tr>");
        foreach (var p in Processes.Processes.OrderByDescending(p => p.CpuPercent).Take(10))
            Line($"<tr><td>{Esc(p.Name)}</td><td>{p.Pid}</td><td>{p.CpuPercent:0.0}</td><td>{Esc(Formatting.FormatBytes(p.MemoryBytes))}</td></tr>");
        Line("</table>");

        Line("</body></html>");
        return sb.ToString();
    }

    /// <summary>Renders one history buffer (0-100 range, 60 samples) as a small inline SVG
    /// polyline - no chart library, just a hand-built path so the file stays a single
    /// self-contained .html with no external script/CSS reference.</summary>
    private static string Sparkline(IEnumerable<double> values, string color)
    {
        var list = values.ToList();
        if (list.Count < 2) return string.Empty;

        const int width = 600, height = 60;
        var points = list.Select((v, i) =>
        {
            double x = i / (double)(list.Count - 1) * width;
            double y = height - Math.Clamp(v, 0, 100) / 100.0 * height;
            return $"{x:0.#},{y:0.#}";
        });
        return $"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" height=\"{height}\">" +
               $"<polyline points=\"{string.Join(' ', points)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" /></svg>";
    }

    /// <summary>#93: "record how my PC looks when healthy" - captures installed software,
    /// services, and startup items to a JSON file the user picks.</summary>
    private void SaveSnapshot()
    {
        var snapshotsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Snapshots");
        try { Directory.CreateDirectory(snapshotsDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            Title = "Save system snapshot",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"TaskManagerPlus-Snapshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json",
            InitialDirectory = snapshotsDir,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SnapshotService.Save(SnapshotService.Capture(), dialog.FileName);
            SnapshotStatusText = $"Snapshot saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            SnapshotStatusText = $"Couldn't save snapshot: {ex.Message}";
        }
    }

    /// <summary>#94: "what changed" - loads a previously saved baseline and diffs it against the
    /// system's current state.</summary>
    private void CompareSnapshot()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Compare against a saved snapshot",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Snapshots"),
        };
        if (dialog.ShowDialog() != true) return;

        var baseline = SnapshotService.Load(dialog.FileName);
        if (baseline is null)
        {
            SnapshotStatusText = "Couldn't read that snapshot file.";
            SnapshotDiff = null;
            return;
        }

        var diff = SnapshotService.Diff(baseline, SnapshotService.Capture());
        SnapshotDiff = diff;
        SnapshotStatusText = diff.HasChanges
            ? $"Compared against {baseline.CapturedAt:g} - changes found."
            : $"Compared against {baseline.CapturedAt:g} - no changes found.";
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
