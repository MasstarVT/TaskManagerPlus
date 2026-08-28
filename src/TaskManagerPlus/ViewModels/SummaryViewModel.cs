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
    private readonly ResponsivenessViewModel _responsiveness;
    private readonly StorageViewModel _storage;
    private readonly ProcessHistoryService _processHistory;
    private readonly DispatcherTimer _healthTimer;

    /// <summary>Round 10, #67: exposed publicly (the field above stays for this class's own
    /// existing internal use) so the Summary tab's XAML can bind "time since last crash" and the
    /// #68 stability index directly, without SummaryViewModel needing to re-expose each one as its
    /// own wrapper property.</summary>
    public StabilityViewModel Stability => _stability;

    /// <summary>#295: exposed the same way Stability is above, so SummaryView.xaml can bind
    /// {Binding Responsiveness.SystemScore.*} directly for the system-responsiveness-index tile
    /// rather than SummaryViewModel re-deriving or re-exposing the score itself - ResponsivenessViewModel
    /// already owns computing it (SampleLight, every light tick).</summary>
    public ResponsivenessViewModel Responsiveness => _responsiveness;

    public PerformanceViewModel Performance { get; }
    public ProcessesViewModel Processes { get; }

    /// <summary>All processes, live-sorted by CPU% descending, for the "Top processes" card.</summary>
    public ICollectionView TopProcesses { get; }

    /// <summary>All processes, live-sorted by the rolling ~10s CPU average descending (#11) -
    /// "what's actually been eating CPU over the last several seconds", a steadier answer than
    /// TopProcesses' instantaneous per-tick reading for a bursty process.</summary>
    public ICollectionView TopProcesses10sAvg { get; }

    public ObservableCollection<HealthIssue> HealthIssues { get; } = new();

    // Round 14, #328: compact mirror of the Storage tab's per-drive health-verdict list -
    // recomputed on the same 2s health-timer tick RefreshHealthIssues already runs on, since
    // DriveHealthVerdict is a plain mutable class (not independently observable) rather than a
    // full ObservableObject.
    private string _storageHealthSummaryText = "Checking...";
    public string StorageHealthSummaryText { get => _storageHealthSummaryText; private set => SetProperty(ref _storageHealthSummaryText, value); }

    private bool _storageHealthHasReplace;
    public bool StorageHealthHasReplace { get => _storageHealthHasReplace; private set => SetProperty(ref _storageHealthHasReplace, value); }

    private bool _storageHealthHasWatch;
    public bool StorageHealthHasWatch { get => _storageHealthHasWatch; private set => SetProperty(ref _storageHealthHasWatch, value); }

    // Round 11, #69: hideable/reorderable dashboard tiles - see DashboardTileConfig's remarks for
    // why this is up/down reordering within a fixed two-column layout rather than freeform
    // drag-and-drop. Left/right mirror the Summary tab's existing two-column Grid (CPU/Memory
    // charts on the left; processes/disk/network/system tiles on the right) - which column a tile
    // belongs to is a structural page-layout choice, not something the user reassigns, only
    // visibility and order-within-column are.
    private static readonly (string Id, string Name, bool Left)[] TileCatalog =
    {
        ("cpu", "CPU Overview", true),
        ("memory", "Memory Utilization", true),
        ("topcpu", "Top CPU processes", false),
        ("topcpu10s", "Top CPU (10s avg)", false),
        ("disk", "Disk", false),
        ("storagehealth", "Drive health", false),
        ("network", "Network", false),
        ("system", "System", false),
    };

    public ObservableCollection<DashboardTileViewModel> LeftTiles { get; } = new();
    public ObservableCollection<DashboardTileViewModel> RightTiles { get; } = new();

    private bool _isTileCustomizerOpen;
    public bool IsTileCustomizerOpen { get => _isTileCustomizerOpen; set => SetProperty(ref _isTileCustomizerOpen, value); }
    public RelayCommand ToggleTileCustomizerCommand { get; }

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

    // #414: leak-specific threshold alerts, configured in the Settings drawer alongside the
    // CPU/Memory/temp thresholds above - same AlertThresholds.json file, same edge-triggered
    // toast pattern (CheckLeakGrowthAlerts/CheckLeakHandleCountAlerts below), just keyed by image
    // name (growth) or pid (handle count) instead of a single system-wide value.
    private readonly HashSet<string> _leakGrowthAlertedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _leakHandleAlertedPids = new();

    public bool LeakGrowthAlertEnabled { get => _alertThresholds.LeakGrowthEnabled; set { _alertThresholds.LeakGrowthEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double LeakGrowthAlertMb { get => _alertThresholds.LeakGrowthMb; set { _alertThresholds.LeakGrowthMb = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double LeakGrowthAlertMinutes { get => _alertThresholds.LeakGrowthMinutes; set { _alertThresholds.LeakGrowthMinutes = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public bool LeakHandleCountAlertEnabled { get => _alertThresholds.LeakHandleCountEnabled; set { _alertThresholds.LeakHandleCountEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); } }
    public double LeakHandleCountAlertThreshold { get => _alertThresholds.LeakHandleCountThreshold; set { _alertThresholds.LeakHandleCountThreshold = value; OnPropertyChanged(); PersistAlertThresholds(); } }

    /// <summary>Round 17, #355: re-reads alerts.json and writes only this VM's own fields back onto
    /// it, rather than blindly overwriting the whole file with this VM's (possibly stale) in-memory
    /// copy - StorageViewModel now also persists to the same file (its own FreeSpace* fields), so a
    /// naive whole-object save here could otherwise clobber a concurrent edit made through the
    /// Storage tab's own threshold controls. See StorageViewModel.PersistAlertThresholds for its
    /// half of this same merge-on-save fix. #414's leak thresholds are this VM's own fields too, so
    /// they're merged back the same way as Cpu/Memory/Temp rather than saved separately.</summary>
    private void PersistAlertThresholds()
    {
        var onDisk = AlertThresholdsService.Load();
        onDisk.CpuEnabled = _alertThresholds.CpuEnabled;
        onDisk.CpuPercent = _alertThresholds.CpuPercent;
        onDisk.MemoryEnabled = _alertThresholds.MemoryEnabled;
        onDisk.MemoryPercent = _alertThresholds.MemoryPercent;
        onDisk.TempEnabled = _alertThresholds.TempEnabled;
        onDisk.TempC = _alertThresholds.TempC;
        onDisk.LeakGrowthEnabled = _alertThresholds.LeakGrowthEnabled;
        onDisk.LeakGrowthMb = _alertThresholds.LeakGrowthMb;
        onDisk.LeakGrowthMinutes = _alertThresholds.LeakGrowthMinutes;
        onDisk.LeakHandleCountEnabled = _alertThresholds.LeakHandleCountEnabled;
        onDisk.LeakHandleCountThreshold = _alertThresholds.LeakHandleCountThreshold;
        AlertThresholdsService.Save(onDisk);
    }

    // #73: one-click diagnostic report bundling specs, recent stability events, a sensor
    // snapshot, and top resource consumers into a single shareable markdown file.
    public RelayCommand GenerateReportCommand { get; }

    // #97: the same report, as a single self-contained HTML file with embedded inline-SVG
    // sparkline charts - no CSV/Excel round-trip needed for someone helping troubleshoot
    // remotely to see the shape of the last minute's CPU/RAM/Disk activity, not just numbers.
    public RelayCommand GenerateHtmlReportCommand { get; }

    // #93/#94: baseline capture + "what changed" comparison - one save/compare pair covers both
    // suggestions, since a saved baseline IS the comparison point for a later diff. See
    // SnapshotService's remarks. AsyncRelayCommand rather than RelayCommand since #486 extended
    // SnapshotService.Capture into an async CaptureAsync (driver inventory/driver store
    // enumeration genuinely takes a few seconds, unlike the rest of what a snapshot captures).
    public AsyncRelayCommand SaveSnapshotCommand { get; }
    public AsyncRelayCommand CompareSnapshotCommand { get; }

    private bool _isCapturingSnapshot;
    /// <summary>#486: true while SaveSnapshotCommand/CompareSnapshotCommand's own CaptureAsync
    /// call is running - drives the two buttons' "Saving.../Comparing..." text, the same busy-flag
    /// shape every on-demand Load button elsewhere in this app already uses.</summary>
    public bool IsCapturingSnapshot { get => _isCapturingSnapshot; private set => SetProperty(ref _isCapturingSnapshot, value); }

    // Round 12, #99: clipboard-friendly one-line(ish) system summary - distinct from
    // GenerateReport/GenerateHtmlReport above (a full multi-section file), this is a handful of
    // lines meant to be pasted straight into a chat message or a forum/support-ticket reply
    // without opening or attaching a file at all.
    public RelayCommand CopySummaryCommand { get; }

    private string _copySummaryStatusText = string.Empty;
    public string CopySummaryStatusText { get => _copySummaryStatusText; private set => SetProperty(ref _copySummaryStatusText, value); }

    private SnapshotDiff? _snapshotDiff;
    public SnapshotDiff? SnapshotDiff { get => _snapshotDiff; private set => SetProperty(ref _snapshotDiff, value); }

    private string _snapshotStatusText = string.Empty;
    public string SnapshotStatusText { get => _snapshotStatusText; private set => SetProperty(ref _snapshotStatusText, value); }

    // Round 12, #94: idle-temperature trend vs. the baseline snapshot - a rough thermal-paste-age
    // proxy, computed alongside the existing baseline-vs-current diff (see SnapshotDiff above)
    // rather than as a separate feature, since it reuses exactly the same "load baseline, compare
    // to now" flow SaveSnapshot/CompareSnapshot already drive.
    private string _idleTempTrendText = string.Empty;
    public string IdleTempTrendText { get => _idleTempTrendText; private set => SetProperty(ref _idleTempTrendText, value); }

    private string _idleTempTrendAbText = string.Empty;
    public string IdleTempTrendAbText { get => _idleTempTrendAbText; private set => SetProperty(ref _idleTempTrendAbText, value); }

    // Round 11, #71: baseline-vs-baseline - diff two previously-saved snapshot files against each
    // other, rather than always comparing a saved baseline to the live system. Reuses
    // SnapshotService.Diff verbatim (it already just takes two SystemSnapshot objects; nothing
    // about it assumes the second one is "current"), so this is purely a second load/compare flow
    // with its own state, kept separate from SnapshotDiff/SnapshotStatusText above so the two
    // comparison modes don't clobber each other's results.
    private SystemSnapshot? _snapshotA;
    private SystemSnapshot? _snapshotB;

    private string _snapshotAName = string.Empty;
    public string SnapshotAName { get => _snapshotAName; private set => SetProperty(ref _snapshotAName, value); }

    private string _snapshotBName = string.Empty;
    public string SnapshotBName { get => _snapshotBName; private set => SetProperty(ref _snapshotBName, value); }

    private SnapshotDiff? _snapshotAbDiff;
    public SnapshotDiff? SnapshotAbDiff { get => _snapshotAbDiff; private set => SetProperty(ref _snapshotAbDiff, value); }

    private string _snapshotAbStatusText = string.Empty;
    public string SnapshotAbStatusText { get => _snapshotAbStatusText; private set => SetProperty(ref _snapshotAbStatusText, value); }

    public RelayCommand LoadSnapshotACommand { get; }
    public RelayCommand LoadSnapshotBCommand { get; }
    public RelayCommand CompareSnapshotsAbCommand { get; }

    // Round 11, #70: "generate report on exit" - see SummarySettings' remarks.
    private readonly SummarySettings _summarySettings = SummarySettingsService.Load();
    public bool GenerateReportOnExit
    {
        get => _summarySettings.GenerateReportOnExit;
        set
        {
            if (_summarySettings.GenerateReportOnExit == value) return;
            _summarySettings.GenerateReportOnExit = value;
            SummarySettingsService.Save(_summarySettings);
            OnPropertyChanged();
        }
    }

    public SummaryViewModel(PerformanceViewModel performance, ProcessesViewModel processes,
        ServicesViewModel services, EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs, NetworkViewModel network, StabilityViewModel stability,
        ResponsivenessViewModel responsiveness, StorageViewModel storage, ProcessHistoryService processHistory)
    {
        Performance = performance;
        Processes = processes;
        _services = services;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _network = network;
        _stability = stability;
        _responsiveness = responsiveness;
        _storage = storage;
        _processHistory = processHistory;

        GenerateReportCommand = new RelayCommand(_ => GenerateReport());
        GenerateHtmlReportCommand = new RelayCommand(_ => GenerateHtmlReport());
        SaveSnapshotCommand = new AsyncRelayCommand(SaveSnapshotAsync);
        CompareSnapshotCommand = new AsyncRelayCommand(CompareSnapshotAsync);
        CopySummaryCommand = new RelayCommand(_ => CopySummary());
        LoadSnapshotACommand = new RelayCommand(_ => LoadSnapshotAb(isA: true));
        LoadSnapshotBCommand = new RelayCommand(_ => LoadSnapshotAb(isA: false));
        CompareSnapshotsAbCommand = new RelayCommand(_ => CompareSnapshotsAb(), _ => _snapshotA is not null && _snapshotB is not null);
        ToggleTileCustomizerCommand = new RelayCommand(_ => IsTileCustomizerOpen = !IsTileCustomizerOpen);

        BuildTiles();

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

        CheckLeakGrowthAlerts();
        CheckLeakHandleCountAlerts();
    }

    /// <summary>#414: "any process grows more than X MB over Y minutes" - projects the #402
    /// per-image-name private-bytes slope (already computed by ProcessHistoryService on every
    /// Processes-tab tick) over the configured window, the same straight-line extrapolation #415's
    /// growth summary uses. Edge-triggered per image name (not per pid, matching the leak-watch/
    /// leak-slope columns, which are also tracked by name) - a fit-confidence floor keeps a noisy,
    /// barely-positive slope from tripping the alert just because it happens to cross the raw MB
    /// figure.</summary>
    private const double LeakGrowthMinRSquaredToAlert = 0.5;

    private void CheckLeakGrowthAlerts()
    {
        if (!LeakGrowthAlertEnabled) { _leakGrowthAlertedNames.Clear(); return; }

        var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Processes.Processes)
        {
            if (string.IsNullOrEmpty(row.Name)) continue;
            liveNames.Add(row.Name);

            double projectedGrowthMb = row.LeakSlopeMbPerHour * (LeakGrowthAlertMinutes / 60.0);
            bool exceeds = row.LeakRSquared >= LeakGrowthMinRSquaredToAlert && projectedGrowthMb >= LeakGrowthAlertMb;

            if (exceeds)
            {
                if (_leakGrowthAlertedNames.Add(row.Name))
                    ToastService.Show("Leak growth threshold",
                        $"{row.Name} has grown roughly {projectedGrowthMb:0} MB over the last {LeakGrowthAlertMinutes:0} minutes (threshold {LeakGrowthAlertMb:0} MB) - a projection from its current growth rate, not a confirmed leak.",
                        isCritical: true);
            }
            else
            {
                _leakGrowthAlertedNames.Remove(row.Name);
            }
        }
        _leakGrowthAlertedNames.RemoveWhere(n => !liveNames.Contains(n));
    }

    /// <summary>#414: "any process exceeds N handles" - a flat ceiling independent of the
    /// slope-based #403 handle-leak heuristic, so a process that jumps straight to a huge handle
    /// count (rather than climbing steadily enough to fit a regression) still gets caught.
    /// Edge-triggered per pid, matching CheckGdiUserQuotaAlerts' shape in ProcessesViewModel.</summary>
    private void CheckLeakHandleCountAlerts()
    {
        if (!LeakHandleCountAlertEnabled) { _leakHandleAlertedPids.Clear(); return; }

        var livePids = new HashSet<int>();
        foreach (var row in Processes.Processes)
        {
            livePids.Add(row.Pid);
            if (row.HandleCount >= LeakHandleCountAlertThreshold)
            {
                if (_leakHandleAlertedPids.Add(row.Pid))
                    ToastService.Show("Handle count threshold",
                        $"{row.Name} (PID {row.Pid}) has {row.HandleCount:N0} open handles (threshold {LeakHandleCountAlertThreshold:0}).",
                        isCritical: true);
            }
            else
            {
                _leakHandleAlertedPids.Remove(row.Pid);
            }
        }
        _leakHandleAlertedPids.RemoveWhere(pid => !livePids.Contains(pid));
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
        Line();

        AppendLeakEvidenceMarkdown(Line);

        return sb.ToString();
    }

    /// <summary>#407: "Memory leak evidence" report section - built entirely from the recorded,
    /// cross-restart per-image-name history (#401/#402/#403/#405), not a live snapshot, so it
    /// reflects the trend observed over whatever window this app has actually been running and
    /// watching, not just the instant the report was generated.</summary>
    private void AppendLeakEvidenceMarkdown(Action<string> line)
    {
        line("## Memory leak evidence");
        line("Derived from recorded per-process history (private bytes/handles/threads), not a live snapshot. " +
             "A steady climb over the observation window is a quick flag worth a second look, not a confirmed leak.");
        line("");

        var top = _processHistory.GetTopGrowthSummaries(8);
        if (top.Count == 0)
        {
            line("Not enough history recorded yet.");
            return;
        }

        line("| Process | Private bytes slope (MB/hr) | R² | Handle slope (/hr) | Handle R² | Thread slope (/hr) | Observation window |");
        line("|---|---|---|---|---|---|---|");
        foreach (var s in top)
        {
            string window = $"{s.FirstSampleUtc.ToLocalTime():g} to {s.LastSampleUtc.ToLocalTime():g} ({s.SampleCount} samples)";
            line($"| {s.ImageName} | {s.PrivateBytesSlopeMbPerHour:0.00} | {s.PrivateBytesRSquared:0.00} | " +
                 $"{s.HandleSlopePerHour:0.0} | {s.HandleRSquared:0.00} | {s.ThreadSlopePerHour:0.0} | {window} |");
        }
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
        // #700: Esc/style-block/Sparkline are now the shared DiagnosticReportFormatting helper -
        // extracted so StressTestReportService's run reports render through the same "existing
        // HTML reporting system" instead of a second, unrelated writer. Esc kept as a local alias
        // so every call site below stays unchanged.
        static string Esc(string s) => DiagnosticReportFormatting.HtmlEscape(s);

        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line(DiagnosticReportFormatting.HtmlDocumentOpen($"Task Manager Plus report - {DateTime.Now:F}"));

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

        AppendLeakEvidenceHtml(Line, Esc);

        Line("</body></html>");
        return sb.ToString();
    }

    /// <summary>#407: HTML twin of AppendLeakEvidenceMarkdown above - same underlying recorded
    /// history, rendered as a table matching the rest of this report's style.</summary>
    private void AppendLeakEvidenceHtml(Action<string> line, Func<string, string> esc)
    {
        line("<h2>Memory leak evidence</h2>");
        line("<p class=\"muted\">Derived from recorded per-process history (private bytes/handles/threads), not a live snapshot. " +
             "A steady climb over the observation window is a quick flag worth a second look, not a confirmed leak.</p>");

        var top = _processHistory.GetTopGrowthSummaries(8);
        if (top.Count == 0)
        {
            line("<p>Not enough history recorded yet.</p>");
            return;
        }

        line("<table><tr><th>Process</th><th>Private bytes slope (MB/hr)</th><th>R²</th><th>Handle slope (/hr)</th>" +
             "<th>Handle R²</th><th>Thread slope (/hr)</th><th>Observation window</th></tr>");
        foreach (var s in top)
        {
            string window = $"{esc(s.FirstSampleUtc.ToLocalTime().ToString("g"))} to {esc(s.LastSampleUtc.ToLocalTime().ToString("g"))} ({s.SampleCount} samples)";
            line($"<tr><td>{esc(s.ImageName)}</td><td>{s.PrivateBytesSlopeMbPerHour:0.00}</td><td>{s.PrivateBytesRSquared:0.00}</td>" +
                 $"<td>{s.HandleSlopePerHour:0.0}</td><td>{s.HandleRSquared:0.00}</td><td>{s.ThreadSlopePerHour:0.0}</td><td>{window}</td></tr>");
        }
        line("</table>");
    }

    /// <summary>Renders one history buffer (0-100 range, 60 samples) as a small inline SVG
    /// polyline - now a thin wrapper over the shared DiagnosticReportFormatting.Sparkline (#700),
    /// kept as a same-named local method so every call site above stays unchanged.</summary>
    private static string Sparkline(IEnumerable<double> values, string color)
        => DiagnosticReportFormatting.Sparkline(values, color);

    /// <summary>Round 12, #99: a handful of plain-text lines - OS, CPU, RAM, GPU, uptime, and a
    /// couple of headline health numbers - built for pasting directly into a chat message or a
    /// forum/support-ticket reply, genuinely shorter than the full Markdown/HTML report rather
    /// than that report's content simply reformatted as text.</summary>
    private void CopySummary()
    {
        var sb = new StringBuilder();
        sb.Append("Task Manager Plus summary — ").Append(DateTime.Now.ToString("g")).Append('\n');
        sb.Append("OS: ").Append(_systemSpecs.OsName).Append('\n');
        sb.Append("CPU: ").Append(_systemSpecs.CpuName).Append('\n');
        sb.Append("RAM: ").Append(_systemSpecs.RamTotal).Append('\n');
        if (_systemSpecs.Gpus.FirstOrDefault()?.Primary is { Length: > 0 } gpuName)
            sb.Append("GPU: ").Append(gpuName).Append('\n');
        sb.Append("Uptime: ").Append(Performance.Uptime).Append('\n');
        sb.Append($"Current load: CPU {Performance.CpuCurrentPercent:0}%, RAM {Performance.RamPercent:0}%, Disk {Performance.DiskPercent:0}%\n");
        if (_energyThermals.CpuPackageTempC is { } temp) sb.Append($"CPU temp: {temp:0.#}°C\n");
        sb.Append(HealthIssues.Count == 0
            ? "Health Check: no issues detected"
            : $"Health Check: {HealthIssues.Count} issue{(HealthIssues.Count == 1 ? "" : "s")} ({string.Join("; ", HealthIssues.Take(3).Select(i => i.Message))}{(HealthIssues.Count > 3 ? "; ..." : string.Empty)})");

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            CopySummaryStatusText = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            CopySummaryStatusText = $"Couldn't copy: {ex.Message}";
        }
    }

    /// <summary>#93: "record how my PC looks when healthy" - captures installed software,
    /// services, startup items, and (#486) driver inventory/driver store contents to a JSON file
    /// the user picks.</summary>
    private async Task SaveSnapshotAsync()
    {
        var snapshotsDir = AppPaths.GetPath("Snapshots");
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

        IsCapturingSnapshot = true;
        SnapshotStatusText = "Capturing snapshot (including driver inventory/driver store - this can take a few seconds)...";
        try
        {
            var snapshot = await SnapshotService.CaptureAsync(CaptureIdleCpuTempOrNull());
            SnapshotService.Save(snapshot, dialog.FileName);
            SnapshotStatusText = $"Snapshot saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            SnapshotStatusText = $"Couldn't save snapshot: {ex.Message}";
        }
        finally
        {
            IsCapturingSnapshot = false;
        }
    }

    /// <summary>Round 12, #94: only records a CPU temperature into the snapshot when the system
    /// looks genuinely idle at capture time (a low CPU% reading, the same rough signal the rest
    /// of this app uses for "idle" - e.g. the throttle heuristic's own "under load" check) - a
    /// baseline temperature captured mid-benchmark would make a meaningless trend comparison
    /// later, so this deliberately leaves it null (and the trend text just doesn't appear) rather
    /// than save a misleading number.</summary>
    private const double IdleCpuPercentThreshold = 15.0;

    private double? CaptureIdleCpuTempOrNull() =>
        Performance.CpuCurrentPercent <= IdleCpuPercentThreshold ? _energyThermals.CpuPackageTempC : null;

    /// <summary>Round 12, #94: renders the idle-temp comparison between two snapshots (baseline
    /// vs. current, or A vs. B) - null on either side (not idle at capture, or no sensor) just
    /// means no trend line shows, framed explicitly as a rough proxy, never a diagnosis.</summary>
    private static string BuildIdleTempTrendText(double? before, double? after)
    {
        if (before is not { } b || after is not { } a) return string.Empty;

        double delta = a - b;
        string direction = delta > 0.5 ? "up" : delta < -0.5 ? "down" : "about the same as";
        return $"Idle CPU temperature is {direction} {Math.Abs(delta):0.#}°C vs. the baseline ({b:0.#}°C → {a:0.#}°C) - " +
               "a rough thermal-paste-age proxy, not a diagnosis; room temperature and dust also affect this.";
    }

    /// <summary>#94: "what changed" - loads a previously saved baseline and diffs it against the
    /// system's current state (including, since #486, driver inventory/driver store contents).</summary>
    private async Task CompareSnapshotAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Compare against a saved snapshot",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Snapshots"),
        };
        if (dialog.ShowDialog() != true) return;

        var baseline = SnapshotService.Load(dialog.FileName);
        if (baseline is null)
        {
            SnapshotStatusText = "Couldn't read that snapshot file.";
            SnapshotDiff = null;
            return;
        }

        IsCapturingSnapshot = true;
        SnapshotStatusText = "Capturing the current system's state to compare (including driver inventory/driver store - this can take a few seconds)...";
        try
        {
            var current = await SnapshotService.CaptureAsync(CaptureIdleCpuTempOrNull());
            var diff = SnapshotService.Diff(baseline, current);
            SnapshotDiff = diff;
            SnapshotStatusText = diff.HasChanges
                ? $"Compared against {baseline.CapturedAt:g} - changes found."
                : $"Compared against {baseline.CapturedAt:g} - no changes found.";

            IdleTempTrendText = BuildIdleTempTrendText(baseline.IdleCpuTempC, current.IdleCpuTempC);

            // Round 12, #98: cross-references this same diff against the reboot-pending flag
            // SystemSpecsService.ReadRebootPending already computes (Round 11) - a derived rollup,
            // no new registry reads, just correlating two outputs SummaryViewModel already has. Lives
            // as its own Health Check line (RefreshHealthIssues, below) rather than duplicated logic
            // here, since that's the one place already re-evaluated on a timer whenever RebootPending
            // could change (a Windows Update finishing installing in the background, for instance).
            RefreshHealthIssues();
        }
        finally
        {
            IsCapturingSnapshot = false;
        }
    }

    /// <summary>Round 11, #69: builds LeftTiles/RightTiles from TileCatalog, applying whatever
    /// visibility/order was previously saved (matched by Id - a tile added in a later app version
    /// with no saved entry just appends at the end, visible by default, the same graceful
    /// "unknown/missing degrades to sane default" shape every other persisted-settings file in
    /// this app already follows).</summary>
    private void BuildTiles()
    {
        var saved = DashboardLayoutService.Load().ToDictionary(t => t.Id);
        LeftTiles.Clear();
        RightTiles.Clear();

        int nextOrder = 0;
        foreach (var (id, name, left) in TileCatalog)
        {
            var cfg = saved.TryGetValue(id, out var c) ? c : new DashboardTileConfig { Id = id, IsVisible = true, Order = nextOrder };
            nextOrder = Math.Max(nextOrder, cfg.Order) + 1;

            var list = left ? LeftTiles : RightTiles;
            var capturedId = id;
            var tile = new DashboardTileViewModel(id, name, cfg.IsVisible, cfg.Order,
                moveUp: () => MoveTile(capturedId, list, -1),
                moveDown: () => MoveTile(capturedId, list, +1));
            list.Add(tile);
        }

        foreach (var list in new[] { LeftTiles, RightTiles })
        {
            var ordered = list.OrderBy(t => t.Order).ToList();
            list.Clear();
            foreach (var t in ordered) list.Add(t);
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
        }

        // Wired up only after the initial load above, so restoring saved visibility doesn't
        // trigger a redundant persist.
        foreach (var t in LeftTiles.Concat(RightTiles)) t.Changed += PersistTiles;
    }

    private void MoveTile(string id, ObservableCollection<DashboardTileViewModel> list, int direction)
    {
        int i = -1;
        for (int k = 0; k < list.Count; k++) if (list[k].Id == id) { i = k; break; }
        int j = i + direction;
        if (i < 0 || j < 0 || j >= list.Count) return;

        list.Move(i, j);
        for (int k = 0; k < list.Count; k++) list[k].Order = k;
        PersistTiles();
    }

    private void PersistTiles()
    {
        var all = LeftTiles.Concat(RightTiles)
            .Select(t => new DashboardTileConfig { Id = t.Id, IsVisible = t.IsVisible, Order = t.Order })
            .ToList();
        DashboardLayoutService.Save(all);
    }

    /// <summary>Round 11, #71: loads a snapshot file into slot A or B for the baseline-vs-baseline
    /// comparison below - completely independent of SaveSnapshot/CompareSnapshot's own
    /// baseline-vs-current flow above (neither reads the other's state).</summary>
    private void LoadSnapshotAb(bool isA)
    {
        var dialog = new OpenFileDialog
        {
            Title = isA ? "Load snapshot A" : "Load snapshot B",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Snapshots"),
        };
        if (dialog.ShowDialog() != true) return;

        var snapshot = SnapshotService.Load(dialog.FileName);
        if (snapshot is null)
        {
            SnapshotAbStatusText = "Couldn't read that snapshot file.";
            return;
        }

        if (isA) { _snapshotA = snapshot; SnapshotAName = Path.GetFileName(dialog.FileName); }
        else { _snapshotB = snapshot; SnapshotBName = Path.GetFileName(dialog.FileName); }

        SnapshotAbDiff = null;
        SnapshotAbStatusText = _snapshotA is not null && _snapshotB is not null
            ? "Both snapshots loaded - click Compare."
            : "Load the other snapshot, then click Compare.";
        CompareSnapshotsAbCommand.RaiseCanExecuteChanged();
    }

    private void CompareSnapshotsAb()
    {
        if (_snapshotA is null || _snapshotB is null) return;

        var diff = SnapshotService.Diff(_snapshotA, _snapshotB);
        SnapshotAbDiff = diff;
        SnapshotAbStatusText = diff.HasChanges
            ? $"{SnapshotAName} → {SnapshotBName}: changes found."
            : $"{SnapshotAName} → {SnapshotBName}: no changes found.";

        // #94: same idle-temp trend as the baseline-vs-current flow above, just against whichever
        // two snapshots were loaded into slots A/B.
        IdleTempTrendAbText = BuildIdleTempTrendText(_snapshotA.IdleCpuTempC, _snapshotB.IdleCpuTempC);
    }

    /// <summary>Round 11, #70: called from MainWindow's Closing handler - a no-op unless the user
    /// opted in via the Settings drawer. Writes silently (no SaveFileDialog, unlike the manual
    /// report button) to a fixed, timestamped path under %AppData%, since popping a file dialog
    /// during shutdown would block the app from actually closing until the user responds to it.</summary>
    public void GenerateReportOnExitIfEnabled()
    {
        if (!GenerateReportOnExit) return;

        try
        {
            var dir = AppPaths.GetPath("Reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"TaskManagerPlus-Report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md");
            File.WriteAllText(path, BuildReportMarkdown());
        }
        catch
        {
            // Best-effort - shutdown shouldn't be blocked or crashed by a failed report write.
        }
    }

    /// <summary>CPU package temperature past this point reads as "running hot" for the Health
    /// Check card - a rough, conservative threshold (most desktop/laptop CPUs throttle well
    /// above this), not a precise per-model limit.</summary>
    private const double HotCpuTempC = 90.0;

    private void RefreshHealthIssues()
    {
        var issues = new List<HealthIssue>();

        // #72 (Round 11): "volume nearly full" was already a Health Check rule from an earlier
        // round - this is just confirming/documenting it here rather than adding a duplicate.
        // The System tab's Volumes card itself tints its progress bar red starting at 85%
        // (PercentToBrushConverter); this rule intentionally stays a little more conservative
        // (90%/97%) since a Health Check entry is a standing item on the dashboard, not just a
        // color hint on a progress bar the user has to be looking at.
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

        // Round 13, #314/#317: NVMe critical-warning bits and media-error count, mirrored from the
        // Storage tab's on-demand NVMe health-log read (#313). Only fires once that read has
        // actually happened for an NVMe disk (ShowNvmeHealth) - like the SMART-derived
        // disk.IsHealthWarning rule above, this reflects the last on-demand read rather than a
        // live poll, since the underlying IOCTL round trip isn't cheap enough for the 2s timer.
        if (_storage.ShowNvmeHealth)
        {
            foreach (var warning in _storage.NvmeCriticalWarnings.Where(w => w.IsSet))
                issues.Add(new HealthIssue { Message = $"NVMe: {warning.Label}", IsCritical = true });

            if (_storage.NvmeMediaErrorsPresent)
                issues.Add(new HealthIssue { Message = "NVMe media and data integrity errors reported", IsCritical = true });
        }

        // Round 14, #324: persistent alert for a drive that started predicting failure while the
        // app was open - separate from the disk.IsHealthWarning rule above, which only reflects the
        // one-time System-tab inventory sweep taken at startup.
        foreach (var alert in _storage.DriveFailureAlerts) issues.Add(alert);

        // Round 18, #373: storahci/stornvme/iaStorAC controller-reset events (storport event 129) -
        // the classic signature of a controller/drive that stopped responding, a common cause of
        // whole-system freezes. Reads _storage.ControllerResetEvents directly, the same "reflects
        // whatever the last on-demand read found, empty until that read has actually happened"
        // shape #314/#317 already use for NvmeCriticalWarnings/NvmeMediaErrorsPresent above -
        // populated by the Storage tab's "Check now" unified event-timeline scan (#370), not a live
        // poll (an event-log query isn't cheap enough for the 2s health timer).
        var recentReset = _storage.ControllerResetEvents
            .Where(e => e.TimeCreated >= DateTime.Now.AddHours(-48))
            .OrderByDescending(e => e.TimeCreated)
            .FirstOrDefault();
        if (recentReset is not null)
            issues.Add(new HealthIssue
            {
                Message = $"Storage controller reset detected on {recentReset.DeviceText} at {recentReset.TimeCreated:g} - possible controller/drive freeze",
                IsCritical = true,
            });

        if (_energyThermals.CpuPackageTempC is { } cpuTemp && cpuTemp >= HotCpuTempC)
            issues.Add(new HealthIssue { Message = $"CPU running hot ({cpuTemp:0}°C)", IsCritical = cpuTemp >= 100 });

        if (_energyThermals.DeadFanDetected)
            issues.Add(new HealthIssue { Message = $"Possible stopped fan: {_energyThermals.DeadFanName}", IsCritical = true });

        if (Performance.PageFilePercent >= 90)
            issues.Add(new HealthIssue { Message = $"Page file is {Performance.PageFilePercent:0}% full", IsCritical = false });

        // #420: nonpaged pool exhaustion is a hard bugcheck risk, not just a slowdown - see
        // PerformanceViewModel.IsNonpagedPoolWarning/PoolLimitsService for what the limit is
        // (a documented registry override when set, a clearly-labeled RAM-based estimate otherwise).
        if (Performance.IsNonpagedPoolWarning)
            issues.Add(new HealthIssue
            {
                Message = $"Nonpaged pool usage is high: {Performance.PoolNonpagedGb:0.00} GB of {(Performance.PoolNonpagedLimitIsEstimate ? "an estimated " : "")}{Performance.PoolNonpagedLimitGb:0.0} GB ({Performance.PoolNonpagedPercent:0}%) - a leaking driver risks a hard crash, not just a slowdown",
                IsCritical = Performance.PoolNonpagedPercent >= 95,
            });

        // Round 8 #41: swap-thrash - sustained heavy paging (hard faults) together with very
        // little free RAM is a much stronger "the system is thrashing" signal than either figure
        // alone; either one by itself happens routinely under ordinary load (a hard-fault burst
        // during a big file load, or briefly low available RAM after opening several apps).
        if (Performance.HardFaultsPerSec >= 500 && Performance.RamAvailablePercent < 10)
            issues.Add(new HealthIssue
            {
                Message = $"Possible memory thrashing: {Performance.HardFaultsPerSec:0} hard faults/sec with only {Performance.RamAvailablePercent:0}% RAM available",
                IsCritical = true,
            });

        if (Performance.HasNetworkErrors)
            issues.Add(new HealthIssue { Message = "Network adapter errors detected", IsCritical = false });

        int failedServices = _services.Services.Count(s => s.HasFailedToStart);
        if (failedServices > 0)
            issues.Add(new HealthIssue { Message = $"{failedServices} service{(failedServices == 1 ? "" : "s")} failed to start", IsCritical = false });

        if (_systemSpecs.OutdatedDrivers.Count > 0)
            issues.Add(new HealthIssue { Message = $"{_systemSpecs.OutdatedDrivers.Count} driver{(_systemSpecs.OutdatedDrivers.Count == 1 ? "" : "s")} may need updating", IsCritical = false });

        if (_systemSpecs.MultipleActiveAvWarning)
            issues.Add(new HealthIssue { Message = "Multiple antivirus products look active", IsCritical = false });

        // Round 19, item 82: "Verifier is currently enabled" warning, also read directly off
        // StabilityViewModel's own already-computed VerifierNagDue/VerifierEnabledDurationText
        // (no new shell-out on this 2-second tick) - the prominent banner with the one-click reset
        // lives on the Stability tab itself; this is just the same finding re-surfaced here so a
        // forgotten diagnostic session shows up without having to know to check that tab.
        if (_stability.VerifierNagDue)
            issues.Add(new HealthIssue
            {
                Message = $"Driver Verifier is still on and slowing this PC down - {_stability.VerifierEnabledDurationText} Reset it from the Stability tab if you're done diagnosing.",
                IsCritical = false,
            });

        // #451: RAM health rollup (mismatched DIMMs, ECC-corrected errors, memory diagnostic
        // result, XMP state, channel population, memory-related bugchecks) - see
        // MemoryDiagnosticsService.BuildRamHealth / SystemSpecsViewModel.RamHealthVerdictText.
        // Never critical on its own here - it's an aggregation of several individually-informational
        // signals, not a confirmed hardware failure.
        if (_systemSpecs.RamHealthIsWarning)
            issues.Add(new HealthIssue { Message = $"RAM health check: {string.Join("; ", _systemSpecs.RamHealthFindings.Take(2))}", IsCritical = false });

        // Round 11, #73: Windows Update/servicing reboot pending - see
        // SystemSpecsService.ReadRebootPending for which registry keys are checked.
        if (_systemSpecs.RebootPending)
            issues.Add(new HealthIssue { Message = "A restart is pending to finish installing updates", IsCritical = false });

        // Round 12, #98: "which of my changes since the baseline are still pending that reboot" -
        // a derived rollup, no new registry reads: just correlates SnapshotDiff (already computed
        // by CompareSnapshot, above) against the same RebootPending flag the rule right above this
        // one already reads. Only fires once a baseline comparison has actually been run this
        // session (SnapshotDiff is null until then) and only when it found real changes - a
        // reboot-pending flag with no baseline comparison on hand says nothing about *which*
        // change caused it, so this rule stays silent rather than implying a link it can't show.
        if (_systemSpecs.RebootPending && SnapshotDiff is { HasChanges: true } diffForReboot)
        {
            int changedCount = diffForReboot.SoftwareAdded.Count + diffForReboot.SoftwareRemoved.Count +
                diffForReboot.ServicesAdded.Count + diffForReboot.ServicesRemoved.Count +
                diffForReboot.StartupAdded.Count + diffForReboot.StartupRemoved.Count;
            issues.Add(new HealthIssue
            {
                Message = $"{changedCount} change{(changedCount == 1 ? "" : "s")} since your last baseline snapshot may be waiting on the pending restart to fully apply",
                IsCritical = false,
            });
        }

        // #66: Windows Defender real-time scan activity heuristic - no new sampling needed, this
        // just reads the CPU% the Processes tab is already polling for MsMpEng.exe. Sustained high
        // CPU on the Defender engine process is the common, otherwise invisible "why is my PC slow
        // right now" answer an active scan gives - a heuristic, not a verified "scan in progress"
        // API (Windows exposes none), the same tier as the process signature check.
        var defender = Processes.Processes.FirstOrDefault(p => p.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase));
        if (defender is not null && defender.CpuPercent >= 20)
            issues.Add(new HealthIssue { Message = $"Windows Defender may be actively scanning (MsMpEng at {defender.CpuPercent:0}% CPU)", IsCritical = false });

        // #455: "N unsigned/test-signed drivers found" - reads the Devices & Drivers tab's own
        // session-lifetime cache (DriverSignatureSummaryState) rather than triggering a scan of its
        // own; that tab is on-demand (CLAUDE.md), so this rule stays silent until the user has
        // actually opened it and run a signature check at least once this session.
        if (DriverSignatureSummaryState.HasScanned && DriverSignatureSummaryState.UnsignedOrTestSignedCount > 0)
        {
            int n = DriverSignatureSummaryState.UnsignedOrTestSignedCount;
            issues.Add(new HealthIssue
            {
                Message = $"{n} unsigned/test-signed driver{(n == 1 ? "" : "s")} found - see the Devices & Drivers tab",
                IsCritical = false,
            });
        }

        // #470: "N devices showing a problem code" - reads the Devices & Drivers tab's own
        // session-lifetime cache (DeviceProblemSummaryState) rather than triggering a device-tree
        // scan of its own, the same "stay silent until the on-demand tab has actually been used
        // this session" shape #455's driver-signature rule above already uses. No tray-alert push
        // here either - #455 is the precedent for this exact category of Health Check entry, and
        // it doesn't push to tray, so this doesn't invent new tray plumbing either.
        if (DeviceProblemSummaryState.HasScanned && DeviceProblemSummaryState.ProblemDeviceCount > 0)
        {
            int n = DeviceProblemSummaryState.ProblemDeviceCount;
            issues.Add(new HealthIssue
            {
                Message = $"{n} device{(n == 1 ? "" : "s")} showing a problem code - see the Devices & Drivers tab's device tree",
                IsCritical = false,
            });
        }

        // #500: "N known-problem driver(s) found" - reads the Devices & Drivers tab's own
        // session-lifetime cache (KnownProblemDriverSummaryState) rather than triggering a scan of
        // its own, the same "stay silent until the on-demand tab has actually been used this
        // session" shape #455/#470's rules above already use. A quick flag, not a verdict - see
        // KnownProblemDriverMatch's remarks.
        if (KnownProblemDriverSummaryState.HasScanned && KnownProblemDriverSummaryState.MatchCount > 0)
        {
            int n = KnownProblemDriverSummaryState.MatchCount;
            issues.Add(new HealthIssue
            {
                Message = $"{n} known-problem driver match{(n == 1 ? "" : "es")} found - see the Devices & Drivers tab (quick flag, not a verdict)",
                IsCritical = false,
            });
        }

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

        RefreshStorageHealthTile();
    }

    /// <summary>Round 14, #328: recomputes the compact Summary-tab mirror of the Storage tab's
    /// per-drive DriveHealthVerdict list - see StorageHealthSummaryText's remarks for why this is
    /// pulled on a timer rather than reactively.</summary>
    private void RefreshStorageHealthTile()
    {
        var verdicts = _storage.DriveHealthVerdicts;
        if (verdicts.Count == 0)
        {
            StorageHealthSummaryText = "No disks detected.";
            StorageHealthHasReplace = false;
            StorageHealthHasWatch = false;
            return;
        }

        int replace = verdicts.Count(v => v.Level == DriveHealthLevel.Replace);
        int watch = verdicts.Count(v => v.Level == DriveHealthLevel.Watch);
        StorageHealthHasReplace = replace > 0;
        StorageHealthHasWatch = watch > 0;

        if (replace == 0 && watch == 0)
        {
            StorageHealthSummaryText = verdicts.Count == 1 ? "Healthy" : $"All {verdicts.Count} disks healthy";
        }
        else
        {
            var parts = new List<string>();
            if (replace > 0) parts.Add($"{replace} Replace");
            if (watch > 0) parts.Add($"{watch} Watch");
            StorageHealthSummaryText = $"{string.Join(", ", parts)} of {verdicts.Count} disk{(verdicts.Count == 1 ? string.Empty : "s")}";
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
