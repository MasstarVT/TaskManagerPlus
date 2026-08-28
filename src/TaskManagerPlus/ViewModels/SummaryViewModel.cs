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

/// <summary>#933: sort key for the Health Check card's finding list - see
/// SummaryViewModel.SortIssues for the composite "Impact" score formula.</summary>
public enum HealthFindingSortMode
{
    Impact,
    Severity,
    Confidence,
    Category,
}

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
    private readonly RulesEngineService _rulesEngine;
    private readonly DispatcherTimer _healthTimer;

    /// <summary>Round 10, #67: exposed publicly (the field above stays for this class's own
    /// existing internal use) so the Summary tab's XAML can bind "time since last crash" and the
    /// #68 stability index directly, without SummaryViewModel needing to re-expose each one as its
    /// own wrapper property.</summary>
    public StabilityViewModel Stability => _stability;

    public PerformanceViewModel Performance { get; }
    public ProcessesViewModel Processes { get; }

    /// <summary>All processes, live-sorted by CPU% descending, for the "Top processes" card.</summary>
    public ICollectionView TopProcesses { get; }

    /// <summary>All processes, live-sorted by the rolling ~10s CPU average descending (#11) -
    /// "what's actually been eating CPU over the last several seconds", a steadier answer than
    /// TopProcesses' instantaneous per-tick reading for a bursty process.</summary>
    public ICollectionView TopProcesses10sAvg { get; }

    public ObservableCollection<HealthIssue> HealthIssues { get; } = new();

    /// <summary>#934: HealthIssues re-shaped into one row per GroupKey (a standalone finding with
    /// no GroupKey is its own singleton row) - what the Health Check card's ItemsControl actually
    /// binds to now. Rebuilt alongside HealthIssues every RefreshHealthIssues pass.</summary>
    public ObservableCollection<HealthFindingGroup> GroupedHealthIssues { get; } = new();

    /// <summary>#937: enabled rules the engine could not meaningfully evaluate this pass because
    /// their condition touched a metric BuildMetricBag marked unavailable - rendered as its own
    /// small grey "couldn't check" list, visibly distinct from both a fired finding and "checked,
    /// clean".</summary>
    public ObservableCollection<CouldNotEvaluateInfo> CouldNotCheckFindings { get; } = new();

    /// <summary>#936: rules that were firing in a recent RefreshHealthIssues pass but aren't in
    /// the current one - seeded from findings-history.jsonl at construction (FindingsHistoryService.
    /// LoadRecentResolved) so "it cleared up" survives an app restart, then appended to live as
    /// resolutions are detected this session.</summary>
    public ObservableCollection<ResolvedFinding> RecentlyResolvedFindings { get; } = new();

    // #924: findings whose rule is currently suppressed (snoozed and not yet expired, or
    // permanently ignored) - kept in their own collection, revealed via IsSuppressedListExpanded,
    // rather than just dropped, so a suppression is easy to find and undo later.
    public ObservableCollection<HealthIssue> SuppressedFindings { get; } = new();

    private bool _isSuppressedListExpanded;
    public bool IsSuppressedListExpanded { get => _isSuppressedListExpanded; set => SetProperty(ref _isSuppressedListExpanded, value); }
    public RelayCommand ToggleSuppressedListCommand { get; }

    /// <summary>#924: "Snooze for 7 days" - the duration is fixed rather than user-configurable
    /// per click, matching the simple "one clear action, not a picker" style of this card's other
    /// buttons (Markdown/HTML report, Copy summary).</summary>
    public RelayCommand SnoozeFindingCommand { get; }

    /// <summary>#924: "Ignore on this machine" - permanent (ExpiresUtc = null) until explicitly
    /// un-suppressed from the SuppressedFindings panel.</summary>
    public RelayCommand IgnoreFindingCommand { get; }

    public RelayCommand UnsuppressFindingCommand { get; }

    // #933: sort order for HealthIssues/GroupedHealthIssues - persisted only in memory for this
    // session (see RefreshHealthIssues/SortIssues for the composite "Impact" score formula).
    public static Array FindingSortModes { get; } = Enum.GetValues(typeof(HealthFindingSortMode));

    private HealthFindingSortMode _findingSortMode = HealthFindingSortMode.Impact;
    public HealthFindingSortMode FindingSortMode
    {
        get => _findingSortMode;
        set { if (SetProperty(ref _findingSortMode, value)) RefreshHealthIssues(); }
    }

    // #935: "Not a problem" feedback, kept purely local (FeedbackService/feedback.jsonl) - a
    // status line plus a one-click "suppress this rule?" follow-up, mirroring this card's other
    // status-text-under-a-button conventions (CopySummaryStatusText, SnapshotStatusText, ...).
    private string? _lastFeedbackRuleId;
    private string _feedbackStatusText = string.Empty;
    public string FeedbackStatusText { get => _feedbackStatusText; private set => SetProperty(ref _feedbackStatusText, value); }
    public RelayCommand NotAProblemCommand { get; }
    public RelayCommand SuppressLastFeedbackRuleCommand { get; }

    // #936: previous pass's fired rule ids (RuleId-bearing findings only - the hand-rolled checks
    // have no stable id to track resolution for) plus their last-known titles, diffed each
    // RefreshHealthIssues pass to detect first-seen/resolved transitions.
    private readonly HashSet<string> _previousFiredRuleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastKnownRuleTitles = new(StringComparer.OrdinalIgnoreCase);

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
        RulesEngineService rulesEngine)
    {
        Performance = performance;
        Processes = processes;
        _services = services;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _network = network;
        _stability = stability;
        _rulesEngine = rulesEngine;

        ToggleSuppressedListCommand = new RelayCommand(_ => IsSuppressedListExpanded = !IsSuppressedListExpanded);
        SnoozeFindingCommand = new RelayCommand(p => SuppressFinding(p, TimeSpan.FromDays(7), "Snoozed for 7 days"));
        IgnoreFindingCommand = new RelayCommand(p => SuppressFinding(p, null, "Ignored on this machine"));
        UnsuppressFindingCommand = new RelayCommand(p =>
        {
            if (p is HealthIssue { RuleId: { Length: > 0 } ruleId })
            {
                _rulesEngine.ClearSuppression(ruleId);
                RefreshHealthIssues();
            }
        });

        NotAProblemCommand = new RelayCommand(p => RecordNotAProblemFeedback(p));
        SuppressLastFeedbackRuleCommand = new RelayCommand(_ => SuppressLastFeedbackRule(), _ => _lastFeedbackRuleId is not null);

        // #936: best-effort load of prior sessions' resolved transitions so "Recently resolved"
        // isn't empty the moment the app starts.
        foreach (var r in FindingsHistoryService.LoadRecentResolved(15)) RecentlyResolvedFindings.Add(r);

        // #921: a rule-pack hot reload (or a rule-editor edit/override/suppression elsewhere)
        // should be reflected on the live Health Check card without waiting for the next 2s tick -
        // may arrive on a FileSystemWatcher thread, so marshal back to the UI thread.
        _rulesEngine.Reloaded += OnRulesEngineReloaded;

        GenerateReportCommand = new RelayCommand(_ => GenerateReport());
        GenerateHtmlReportCommand = new RelayCommand(_ => GenerateHtmlReport());
        SaveSnapshotCommand = new RelayCommand(_ => SaveSnapshot());
        CompareSnapshotCommand = new RelayCommand(_ => CompareSnapshot());
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
    /// edge-triggered so a sustained excursion produces one toast, not one every 2s tick. #963:
    /// each firing is also persisted to alerts-history.jsonl (via AlertDeliveryService, the same
    /// path a rule-engine finding's alert goes through in RecordFindingsHistory below), respecting
    /// #964's quiet hours.</summary>
    private void CheckThresholdAlerts()
    {
        CheckOne("builtin.threshold.cpu", CpuAlertEnabled, Performance.CpuCurrentPercent, CpuAlertThreshold, ref _cpuAlerted,
            "CPU usage threshold", v => $"CPU usage is {v:0}% (threshold {CpuAlertThreshold:0}%)");

        CheckOne("builtin.threshold.memory", MemoryAlertEnabled, Performance.RamPercent, MemoryAlertThreshold, ref _memoryAlerted,
            "Memory usage threshold", v => $"Memory usage is {v:0}% (threshold {MemoryAlertThreshold:0}%)");

        if (_energyThermals.CpuPackageTempC is { } temp)
        {
            CheckOne("builtin.threshold.temperature", TempAlertEnabled, temp, TempAlertThreshold, ref _tempAlerted,
                "CPU temperature threshold", v => $"CPU temperature is {v:0}°C (threshold {TempAlertThreshold:0}°C)");
        }
    }

    private static void CheckOne(string alertId, bool enabled, double value, double threshold, ref bool alerted, string title, Func<double, string> message)
    {
        if (!enabled) { alerted = false; return; }

        if (value >= threshold)
        {
            if (!alerted)
            {
                alerted = true;
                string text = message(value);
                // #963/#964: these three fixed thresholds have no Rule behind them to carry a
                // channel/escalation setting - always Toast as the default channel, still subject
                // to quiet-hours suppression (log line always written) and never escalated (no
                // EscalateAfterRepeats/EscalateWindowSeconds to escalate against).
                AlertDeliveryService.Deliver(alertId, title, text, RuleSeverity.High, AlertChannel.Toast,
                    escalateAfterRepeats: null, escalateWindowSeconds: null);
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
    /// services, and startup items to a JSON file the user picks.</summary>
    private void SaveSnapshot()
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

        try
        {
            SnapshotService.Save(SnapshotService.Capture(CaptureIdleCpuTempOrNull()), dialog.FileName);
            SnapshotStatusText = $"Snapshot saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            SnapshotStatusText = $"Couldn't save snapshot: {ex.Message}";
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
    /// system's current state.</summary>
    private void CompareSnapshot()
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

        var current = SnapshotService.Capture(CaptureIdleCpuTempOrNull());
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

    private void RefreshHealthIssues()
    {
        // #916-919: the bulk of what used to be a hardcoded if-chain here now lives as JSON rule
        // definitions evaluated by RulesEngineService against a fresh metric bag (#917) - volume-
        // full, dirty-bit, drive-health, CPU-hot, dead-fan, page-file-full, thrashing, network-
        // errors, failed-services, outdated-drivers, multi-AV, and reboot-pending are all now rule
        // packs (see RulesEngineService.BuiltInPackJson) rather than C# checks. #924's suppressions
        // are already filtered out of ruleResult.Findings (into ruleResult.Suppressed) by the
        // engine itself.
        var bag = RulesEngineService.BuildMetricBag(Performance, _energyThermals, _systemSpecs, _services, Processes, out var unavailableMetrics);
        var ruleResult = _rulesEngine.Evaluate(bag, unavailableMetrics);
        var issues = new List<HealthIssue>(ruleResult.Findings);

        if (!ruleResult.Suppressed.Select(i => i.RuleId).SequenceEqual(SuppressedFindings.Select(i => i.RuleId)))
        {
            SuppressedFindings.Clear();
            foreach (var s in ruleResult.Suppressed) SuppressedFindings.Add(s);
        }

        // #937: rules the engine couldn't meaningfully evaluate this pass - visibly distinct from
        // both a fired finding and "checked, clean".
        if (!ruleResult.CouldNotCheck.Select(c => c.RuleId).SequenceEqual(CouldNotCheckFindings.Select(c => c.RuleId)))
        {
            CouldNotCheckFindings.Clear();
            foreach (var c in ruleResult.CouldNotCheck) CouldNotCheckFindings.Add(c);
        }

        // A few checks stay hand-rolled here rather than becoming rule-pack JSON, because they
        // don't cleanly fit the metric-bag/condition shape (#916): the reboot-pending correlation
        // below reads UI/session-only state (SnapshotDiff, only populated after a manual "Compare"
        // click) that has no business being an ambient metric-bag key, and the Defender/anomaly
        // checks further down need arbitrary statistical computation a static JSON condition can't
        // express.

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
                // #928: this is a correlation between two already-independent signals, not a
                // verified cause - HealthIssue.Confidence defaults to 100 ("likely") when unset,
                // which would overstate a heuristic like this one.
                Confidence = 55,
            });
        }

        // #66: Windows Defender real-time scan activity heuristic - no new sampling needed, this
        // just reads the CPU% the Processes tab is already polling for MsMpEng.exe. Sustained high
        // CPU on the Defender engine process is the common, otherwise invisible "why is my PC slow
        // right now" answer an active scan gives - a heuristic, not a verified "scan in progress"
        // API (Windows exposes none), the same tier as the process signature check.
        var defender = Processes.Processes.FirstOrDefault(p => p.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase));
        if (defender is not null && defender.CpuPercent >= 20)
            issues.Add(new HealthIssue { Message = $"Windows Defender may be actively scanning (MsMpEng at {defender.CpuPercent:0}% CPU)", IsCritical = false, Confidence = 60 });

        // #67: anomaly highlighting - flags CPU/RAM/Disk usage that's a statistical outlier vs.
        // its own last-minute history, even without a fixed threshold. Requires both a meaningful
        // raw jump (>=20 points) AND a real statistical deviation (>=3 std dev past a small floor)
        // so a metric that's merely a little above its own noisy baseline isn't flagged.
        CheckAnomaly(issues, "CPU", Performance.CpuHistory, Performance.CpuCurrentPercent);
        CheckAnomaly(issues, "Memory", Performance.RamHistory, Performance.RamPercent);
        CheckAnomaly(issues, "Disk activity", Performance.DiskHistory, Performance.DiskPercent);

        // #933: sort by the selected criterion before anything below renders/diffs the list.
        issues = SortIssues(issues, FindingSortMode);

        // Replace in place only when the content actually changed, so the UI doesn't flicker/
        // lose scroll position on every 2s tick when nothing is different.
        if (!issues.Select(i => i.Message).SequenceEqual(HealthIssues.Select(i => i.Message)))
        {
            HealthIssues.Clear();
            foreach (var issue in issues) HealthIssues.Add(issue);
        }

        // #934: re-shape the same sorted list into grouped rows for the card's ItemsControl.
        RebuildGroupedHealthIssues(issues);

        // #936: diff this pass's fired rule ids against the previous pass's to detect first-seen/
        // resolved transitions - edge-triggered only (see FindingsHistoryEntry's remarks on why
        // "still-firing" is never logged on every 2s tick a chronic finding keeps firing).
        RecordFindingsHistory(issues);
    }

    /// <summary>#933: orders `issues` by the selected sort mode. "Impact" is a composite proxy -
    /// a true numeric "impact" isn't available for most findings, so this combines the rule
    /// author's own severity judgment with the rule's confidence, plus a small tie-breaking boost
    /// for a finding that can honestly back itself with a concrete ImpactText (#932):
    ///   score = severityWeight(severity) * (confidence / 100.0) * (hasImpactText ? 1.15 : 1.0)
    ///   severityWeight: Info=1, Low=2, Medium=3, High=4
    /// "Severity"/"Confidence" sort by that field directly (tie-broken by the other); "Category"
    /// groups alphabetically by Rule.Category, empty/hand-rolled findings sorting last.</summary>
    private static List<HealthIssue> SortIssues(List<HealthIssue> issues, HealthFindingSortMode mode) => mode switch
    {
        HealthFindingSortMode.Severity => issues.OrderByDescending(SeverityWeight).ThenByDescending(i => i.Confidence).ToList(),
        HealthFindingSortMode.Confidence => issues.OrderByDescending(i => i.Confidence).ThenByDescending(SeverityWeight).ToList(),
        HealthFindingSortMode.Category => issues
            .OrderBy(i => string.IsNullOrEmpty(i.Category) ? 1 : 0)
            .ThenBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Title ?? i.Message, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        _ => issues.OrderByDescending(ImpactScore).ToList(),
    };

    private static int SeverityWeight(HealthIssue issue) => issue.Severity switch
    {
        RuleSeverity.Info => 1,
        RuleSeverity.Low => 2,
        RuleSeverity.Medium => 3,
        RuleSeverity.High => 4,
        _ => 1,
    };

    private static double ImpactScore(HealthIssue issue)
    {
        double confidenceFactor = Math.Clamp(issue.Confidence, 0, 100) / 100.0;
        double impactBoost = string.IsNullOrEmpty(issue.ImpactText) ? 1.0 : 1.15;
        return SeverityWeight(issue) * confidenceFactor * impactBoost;
    }

    /// <summary>#934: collapses `sortedIssues` into one row per GroupKey - a standalone finding
    /// with no GroupKey (or the only finding currently holding one) is its own singleton row, so
    /// the card's DataTemplate can treat every row uniformly (HealthFindingGroup.IsSingle).
    /// Preserves `sortedIssues`' own order (a group's position is wherever its first member fell
    /// in the sort).</summary>
    private void RebuildGroupedHealthIssues(List<HealthIssue> sortedIssues)
    {
        var groups = new List<HealthFindingGroup>();
        var byKey = new Dictionary<string, HealthFindingGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in sortedIssues)
        {
            if (issue.GroupKey is { Length: > 0 } key)
            {
                if (byKey.TryGetValue(key, out var existing))
                {
                    existing.Findings.Add(issue);
                    continue;
                }
                var group = new HealthFindingGroup { GroupKey = key, Findings = { issue } };
                byKey[key] = group;
                groups.Add(group);
            }
            else
            {
                groups.Add(new HealthFindingGroup { GroupKey = null, Findings = { issue } });
            }
        }

        bool changed = groups.Count != GroupedHealthIssues.Count ||
            groups.Zip(GroupedHealthIssues, (a, b) => a.Findings.Select(f => f.Message).SequenceEqual(b.Findings.Select(f => f.Message))).Any(same => !same);
        if (changed)
        {
            GroupedHealthIssues.Clear();
            foreach (var g in groups) GroupedHealthIssues.Add(g);
        }
    }

    /// <summary>#936: diffs `issues`' RuleId-bearing findings against the previous pass's set,
    /// appending an edge-triggered "first-seen"/"resolved" line to findings-history.jsonl for
    /// each transition and updating RecentlyResolvedFindings live for the latter.</summary>
    private void RecordFindingsHistory(List<HealthIssue> issues)
    {
        var currentFiredIds = new HashSet<string>(
            issues.Where(i => i.RuleId is { Length: > 0 }).Select(i => i.RuleId!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var id in currentFiredIds)
        {
            var issue = issues.First(i => string.Equals(i.RuleId, id, StringComparison.OrdinalIgnoreCase));
            string title = issue.Title ?? issue.Message;
            _lastKnownRuleTitles[id] = title;
            if (!_previousFiredRuleIds.Contains(id))
            {
                FindingsHistoryService.Append(new FindingsHistoryEntry { RuleId = id, Title = title, Transition = "first-seen" });

                // #963/#964/#965: a newly-fired rule-engine finding is exactly the same "first-
                // seen" signal #963 wants persisted as an alert - deliver it through the rule's own
                // AlertChannel/escalation settings (RulesEngineService.Rules carries the loaded
                // Rule, including any #923 severity override already applied as EffectiveSeverity).
                var loaded = _rulesEngine.Rules.FirstOrDefault(r => string.Equals(r.Rule.Id, id, StringComparison.OrdinalIgnoreCase));
                if (loaded is not null)
                {
                    AlertDeliveryService.Deliver(id, title, issue.Message, loaded.EffectiveSeverity, loaded.Rule.AlertChannel,
                        loaded.Rule.EscalateAfterRepeats, loaded.Rule.EscalateWindowSeconds);
                }
            }
        }

        foreach (var id in _previousFiredRuleIds)
        {
            if (currentFiredIds.Contains(id)) continue;

            string title = _lastKnownRuleTitles.TryGetValue(id, out var t) ? t : id;
            var resolvedAt = DateTime.UtcNow;
            FindingsHistoryService.Append(new FindingsHistoryEntry { RuleId = id, Title = title, Transition = "resolved" });
            RecentlyResolvedFindings.Insert(0, new ResolvedFinding { RuleId = id, Title = title, ResolvedAtUtc = resolvedAt });
            while (RecentlyResolvedFindings.Count > 15) RecentlyResolvedFindings.RemoveAt(RecentlyResolvedFindings.Count - 1);
        }

        _previousFiredRuleIds.Clear();
        foreach (var id in currentFiredIds) _previousFiredRuleIds.Add(id);
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
                // #928: a real statistical deviation, but "unusual" isn't "a problem" - kept below
                // the default 100 ("likely") so it renders honestly as "possible".
                Confidence = 65,
                // #932: an honest, already-computed impact figure - how far above the recent
                // typical this reading is.
                ImpactText = $"{current - mean:0}pt above typical {label} usage",
            });
        }
    }

    /// <summary>#924: suppresses the rule behind `parameter` (a HealthIssue from HealthIssues, via
    /// the Health Check card's Snooze/Ignore buttons) for `duration` (null = permanent), then
    /// re-evaluates immediately so the card reflects the change without waiting for the next tick.
    /// A no-op for the hand-rolled findings that carry no RuleId (the Defender/anomaly/reboot-
    /// correlation checks) - those don't have a rule to suppress by id.</summary>
    private void SuppressFinding(object? parameter, TimeSpan? duration, string reason)
    {
        if (parameter is not HealthIssue { RuleId: { Length: > 0 } ruleId }) return;
        _rulesEngine.Suppress(ruleId, reason, duration is null ? null : DateTime.UtcNow.Add(duration.Value));
        RefreshHealthIssues();
    }

    /// <summary>#935: records a "not a problem" click to feedback.jsonl (purely local - see
    /// FeedbackService's remarks) and offers a one-click "suppress this rule?" follow-up via
    /// FeedbackStatusText/SuppressLastFeedbackRuleCommand. A no-op for the hand-rolled findings
    /// that carry no RuleId, same as SuppressFinding above.</summary>
    private void RecordNotAProblemFeedback(object? parameter)
    {
        if (parameter is not HealthIssue { RuleId: { Length: > 0 } ruleId } issue) return;

        FeedbackService.Append(new FeedbackEntry
        {
            RuleId = ruleId,
            MetricValuesAtTime = issue.Evidence.ToDictionary(e => e.Label, e => e.Value),
        });

        _lastFeedbackRuleId = ruleId;
        FeedbackStatusText = $"Feedback recorded for \"{issue.Title ?? issue.Message}\" - stored locally, never sent anywhere.";
        SuppressLastFeedbackRuleCommand.RaiseCanExecuteChanged();
    }

    private void SuppressLastFeedbackRule()
    {
        if (_lastFeedbackRuleId is not { Length: > 0 } ruleId) return;
        _rulesEngine.Suppress(ruleId, "Marked \"not a problem\"", DateTime.UtcNow.AddDays(7));
        _lastFeedbackRuleId = null;
        FeedbackStatusText = "Rule suppressed for 7 days.";
        SuppressLastFeedbackRuleCommand.RaiseCanExecuteChanged();
        RefreshHealthIssues();
    }

    private void OnRulesEngineReloaded()
    {
        // Always post through the dispatcher (see RulesEditorViewModel.OnEngineReloaded's remarks
        // on why this avoids a reentrant collection update when Reloaded was raised synchronously
        // from an override/suppression edit).
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(RefreshHealthIssues);
    }

    public void Dispose()
    {
        _healthTimer.Stop();
        _rulesEngine.Reloaded -= OnRulesEngineReloaded;
    }
}
