using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One rule's firing status transition across a #955 change window (#958) - "Cleared"
/// (fired at "Start change", not at "Finish change"), "Still firing" (fired at both), or "New
/// since the change started" (fired only at "Finish change").</summary>
public sealed record RuleFindingTransition(string RuleId, string Title, string Status);

/// <summary>
/// #950-958: backs the Troubleshoot tab's "Baselines" sub-page - the same sibling-panel pattern
/// TimelineViewModel already establishes (reachable from the landing page, its own "Back" button).
/// Owns:
///   - #950/#957: opt-in full-baseline capture (WinSAT scores, last boot duration, idle CPU/RAM/
///     disk-latency, all idle-gated), via BaselineService.
///   - #951: regression detection against the oldest saved baseline, rendered as a card.
///   - #952: an opt-in automatic weekly capture, gated on a rolling "sustained idle" tracker fed
///     from the same PerformanceViewModel instance every other tab already polls.
///   - #953: a hardware fingerprint on every baseline, surfaced as a loud warning whenever two
///     compared baselines don't match.
///   - #954: a trend chart across every saved baseline, following PerformanceViewModel.LineOf's
///     glow/core pairing technique.
///   - #955/#956/#958: a "Start change" / "Finish change" before/after window that captures two
///     baselines, diffs them (reusing SnapshotService.Diff), re-checks the rule set fired at
///     "Start change", and can export the whole thing as a Markdown/HTML report.
/// </summary>
public sealed class BaselineViewModel : ObservableObject, IDisposable
{
    private readonly PerformanceViewModel _performance;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly ServicesViewModel _services;
    private readonly ProcessesViewModel _processes;
    private readonly RulesEngineService _rulesEngine;

    private readonly BaselineSettings _settings;
    private readonly IdleRollingTracker _idleTracker;
    private readonly DispatcherTimer _idleTimer;

    /// <summary>Every saved baseline, oldest first - what #951's regression comparison and #954's
    /// trend chart both want directly.</summary>
    public ObservableCollection<PerformanceBaseline> Baselines { get; } = new();

    /// <summary>Same set, newest first - what the "captured baselines" list in the UI shows.</summary>
    public ObservableCollection<PerformanceBaseline> RecentBaselines { get; } = new();

    private bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; private set => SetProperty(ref _isCapturing, value); }

    private string? _captureStatusText;
    public string? CaptureStatusText { get => _captureStatusText; private set => SetProperty(ref _captureStatusText, value); }

    private string _newBaselineLabel = string.Empty;
    public string NewBaselineLabel { get => _newBaselineLabel; set => SetProperty(ref _newBaselineLabel, value); }

    /// <summary>#952/#957: true once CPU has read at/under the idle threshold for the required
    /// number of consecutive samples - shown in the UI so the user understands why an automatic
    /// capture hasn't fired yet, or why a manual capture right now would be flagged non-idle.</summary>
    public bool IsCurrentlyIdle => _idleTracker.IsSustainedIdle;

    public AsyncRelayCommand CaptureBaselineCommand { get; }

    // ----- #952: automatic weekly capture settings ------------------------------------------

    public bool AutoCaptureEnabled
    {
        get => _settings.AutoCaptureEnabled;
        set
        {
            if (_settings.AutoCaptureEnabled == value) return;
            _settings.AutoCaptureEnabled = value;
            BaselineSettingsService.Save(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoCaptureStatusText));
        }
    }

    public int MaxBaselinesKept
    {
        get => _settings.MaxBaselinesKept;
        set
        {
            int clamped = Math.Clamp(value, 1, 100);
            if (_settings.MaxBaselinesKept == clamped) return;
            _settings.MaxBaselinesKept = clamped;
            BaselineSettingsService.Save(_settings);
            OnPropertyChanged();
            BaselineService.PruneToMax(clamped);
            LoadBaselines();
        }
    }

    public string AutoCaptureStatusText
    {
        get
        {
            if (!AutoCaptureEnabled) return "Automatic weekly capture is off.";
            if (_settings.LastAutoCaptureUtc is { } last)
            {
                var next = last.AddDays(_settings.IntervalDays);
                return next <= DateTime.UtcNow
                    ? "Due now - will capture the next time this PC is idle for a few minutes."
                    : $"Next automatic capture on or after {next.ToLocalTime():d}, once this PC has been idle for a few minutes.";
            }
            return "Will capture the first time this PC is idle for a few minutes.";
        }
    }

    // ----- #951: regression vs. the oldest saved baseline ------------------------------------

    public ObservableCollection<BaselineMetricComparison> RegressionComparisons { get; } = new();

    private string _regressionHeaderText = string.Empty;
    public string RegressionHeaderText { get => _regressionHeaderText; private set => SetProperty(ref _regressionHeaderText, value); }

    private bool _showRegressionFingerprintWarning;
    public bool ShowRegressionFingerprintWarning { get => _showRegressionFingerprintWarning; private set => SetProperty(ref _showRegressionFingerprintWarning, value); }

    private string _regressionFingerprintWarningText = string.Empty;
    public string RegressionFingerprintWarningText { get => _regressionFingerprintWarningText; private set => SetProperty(ref _regressionFingerprintWarningText, value); }

    // ----- #954: trend chart -----------------------------------------------------------------

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    public ISeries[] TrendSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] TrendXAxes { get; }
    public Axis[] TrendYAxes { get; }

    // ----- #955: "wrap a change" before/after window ------------------------------------------

    private PerformanceBaseline? _changeBeforeBaseline;
    private PerformanceBaseline? _changeAfterBaseline;
    private Dictionary<string, string> _changeBeforeFiredRuleIds = new(StringComparer.OrdinalIgnoreCase);

    private bool _isChangeWindowOpen;
    public bool IsChangeWindowOpen { get => _isChangeWindowOpen; private set => SetProperty(ref _isChangeWindowOpen, value); }

    private string _changeLabelInput = string.Empty;
    public string ChangeLabelInput { get => _changeLabelInput; set => SetProperty(ref _changeLabelInput, value); }

    private string _activeChangeLabel = string.Empty;
    public string ActiveChangeLabel { get => _activeChangeLabel; private set => SetProperty(ref _activeChangeLabel, value); }

    private DateTime? _changeStartedAt;
    public DateTime? ChangeStartedAt { get => _changeStartedAt; private set => SetProperty(ref _changeStartedAt, value); }

    private SnapshotDiff? _changeDiff;
    public SnapshotDiff? ChangeDiff { get => _changeDiff; private set => SetProperty(ref _changeDiff, value); }

    public ObservableCollection<BaselineMetricComparison> ChangePerformanceDiff { get; } = new();
    public ObservableCollection<RuleFindingTransition> ChangeRuleTransitions { get; } = new();

    private bool _changeHasFingerprintMismatch;
    public bool ChangeHasFingerprintMismatch { get => _changeHasFingerprintMismatch; private set => SetProperty(ref _changeHasFingerprintMismatch, value); }

    private string _changeSummaryText = string.Empty;
    public string ChangeSummaryText { get => _changeSummaryText; private set => SetProperty(ref _changeSummaryText, value); }

    public AsyncRelayCommand StartChangeCommand { get; }
    public AsyncRelayCommand FinishChangeCommand { get; }
    public RelayCommand GenerateChangeReportCommand { get; }

    // ----- suggestions.md #994: known-good comparison against an imported reference profile -----

    private PerformanceBaseline? _referenceProfile;
    public PerformanceBaseline? ReferenceProfile { get => _referenceProfile; private set => SetProperty(ref _referenceProfile, value); }

    public bool HasReferenceProfile => ReferenceProfile is not null;

    private HardwareFingerprint? _currentFingerprint;
    public HardwareFingerprint? CurrentFingerprint { get => _currentFingerprint; private set => SetProperty(ref _currentFingerprint, value); }

    /// <summary>True when the imported profile's hardware fingerprint doesn't match this machine's
    /// - rendered as an explicit "different machines" note, not a failure, since #994's task notes
    /// are explicit every difference here is framed as "different, not necessarily wrong."</summary>
    private bool _referenceFingerprintMismatch;
    public bool ReferenceFingerprintMismatch { get => _referenceFingerprintMismatch; private set => SetProperty(ref _referenceFingerprintMismatch, value); }

    public ObservableCollection<BaselineMetricComparison> ReferencePerformanceComparison { get; } = new();

    private SnapshotDiff? _referenceDiff;
    public SnapshotDiff? ReferenceDiff { get => _referenceDiff; private set => SetProperty(ref _referenceDiff, value); }

    private string _referenceStatusText = "No reference profile imported yet. Import an exported baseline (Baselines panel \"Capture a baseline\" on a known-good machine, then bring that file here) to compare it against this machine.";
    public string ReferenceStatusText { get => _referenceStatusText; private set => SetProperty(ref _referenceStatusText, value); }

    public AsyncRelayCommand ImportReferenceProfileCommand { get; }

    /// <summary>suggestions.md #1000: Ctrl+Shift+C on the Baselines panel - copies a Markdown
    /// summary of the currently captured baselines and the oldest-vs-newest regression comparison
    /// to the clipboard.</summary>
    public RelayCommand CopyMarkdownCommand { get; }

    public BaselineViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs, ServicesViewModel services, ProcessesViewModel processes,
        RulesEngineService rulesEngine)
    {
        _performance = performance;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _services = services;
        _processes = processes;
        _rulesEngine = rulesEngine;
        _settings = BaselineSettingsService.Load();

        // #952/#957: ~5 minutes of sustained idle at a 10s tick cadence before an automatic
        // capture fires, or a capture's idle-gated fields are trusted for a trend/regression
        // comparison.
        _idleTracker = new IdleRollingTracker(samplesRequired: 30);

        TrendXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 15,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        TrendYAxes = new[]
        {
            // Primary axis: boot time (s) / idle CPU% / idle RAM committed (GB) - roughly
            // comparable 0-100ish ranges, sharing one axis keeps the chart simple.
            new Axis
            {
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
            // Secondary axis (right edge): WinSAT disk score, a 1-10ish scale that would otherwise
            // read as a flat line near zero against the primary axis' range.
            new Axis
            {
                Position = AxisPosition.End,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };

        CaptureBaselineCommand = new AsyncRelayCommand(async () =>
        {
            string? label = string.IsNullOrWhiteSpace(NewBaselineLabel) ? null : NewBaselineLabel.Trim();
            await CaptureAndSaveAsync(auto: false, label);
            NewBaselineLabel = string.Empty;
        }, () => !IsCapturing);

        StartChangeCommand = new AsyncRelayCommand(StartChangeAsync, () => !IsChangeWindowOpen && !IsCapturing);
        FinishChangeCommand = new AsyncRelayCommand(FinishChangeAsync, () => IsChangeWindowOpen && !IsCapturing);
        GenerateChangeReportCommand = new RelayCommand(_ => GenerateChangeReport(), _ => _changeAfterBaseline is not null);
        ImportReferenceProfileCommand = new AsyncRelayCommand(ImportReferenceProfileAsync);
        CopyMarkdownCommand = new RelayCommand(_ =>
        {
            try { System.Windows.Clipboard.SetText(BuildBaselinesMarkdown()); }
            catch { /* best-effort - a clipboard write can legitimately fail */ }
        });

        LoadBaselines();

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += (_, _) => OnIdleTick();
        _idleTimer.Start();
    }

    private void OnIdleTick()
    {
        _idleTracker.Record(_performance.CpuCurrentPercent);
        OnPropertyChanged(nameof(IsCurrentlyIdle));
        TryFireAutomaticCapture();
    }

    /// <summary>#952: fires the scheduled capture only once conditions are actually met (opted in,
    /// sustained idle, due, and nothing else already in flight) - never blocks or interrupts a
    /// manual capture/change window in progress.</summary>
    private void TryFireAutomaticCapture()
    {
        if (!AutoCaptureEnabled || IsCapturing || IsChangeWindowOpen) return;
        if (!_idleTracker.IsSustainedIdle) return;

        if (_settings.LastAutoCaptureUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(_settings.IntervalDays))
            return;

        _ = CaptureAndSaveAsync(auto: true, label: null);
    }

    /// <summary>Shared by the manual capture button, the automatic scheduler, and #955's Start/
    /// Finish change actions - captures, saves, prunes, and reloads the baseline list. Never
    /// refuses a manual/change-window capture for lacking idle conditions (#957) - only the
    /// automatic path (TryFireAutomaticCapture, above) gates on IsSustainedIdle before even calling
    /// this.</summary>
    private async Task<PerformanceBaseline?> CaptureAndSaveAsync(bool auto, string? label)
    {
        if (IsCapturing) return null;
        IsCapturing = true;
        CaptureStatusText = auto
            ? "Capturing automatic baseline..."
            : "Capturing baseline... (WinSAT may take about a minute if it hasn't run on this PC before)";
        try
        {
            bool isIdle = _idleTracker.IsSustainedIdle;
            var baseline = await BaselineService.CaptureAsync(
                _systemSpecs, _performance.RamTotalGb, _performance.CpuCurrentPercent, _performance.CommittedGb,
                isIdle, allowWinSatRun: !auto, label, wasAutomatic: auto, CancellationToken.None);

            BaselineService.Save(baseline);
            if (auto)
            {
                _settings.LastAutoCaptureUtc = DateTime.UtcNow;
                BaselineSettingsService.Save(_settings);
                OnPropertyChanged(nameof(AutoCaptureStatusText));
            }
            BaselineService.PruneToMax(_settings.MaxBaselinesKept);
            LoadBaselines();

            CaptureStatusText = isIdle
                ? $"Baseline captured {baseline.CapturedAt:g}."
                : $"Baseline captured {baseline.CapturedAt:g} - captured under load, idle metrics flagged as not comparable.";
            return baseline;
        }
        catch (Exception ex)
        {
            CaptureStatusText = $"Couldn't capture a baseline: {ex.Message}";
            return null;
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private void LoadBaselines()
    {
        var all = BaselineService.LoadAll(); // ascending by CapturedAt
        Baselines.Clear();
        foreach (var b in all) Baselines.Add(b);

        RecentBaselines.Clear();
        for (int i = all.Count - 1; i >= 0; i--) RecentBaselines.Add(all[i]);

        RecomputeRegression();
        RecomputeTrendChart();
    }

    /// <summary>#951: "this PC got slower" - compares the latest baseline against the oldest one
    /// still on disk, reporting each metric's percentage change with elapsed time. #953: warns
    /// loudly (ShowRegressionFingerprintWarning) when the two captures' hardware fingerprints
    /// don't match, since a regression number across a hardware change is misleading.</summary>
    private void RecomputeRegression()
    {
        RegressionComparisons.Clear();
        ShowRegressionFingerprintWarning = false;
        RegressionFingerprintWarningText = string.Empty;

        if (Baselines.Count == 0) { RegressionHeaderText = "No baselines captured yet."; return; }
        if (Baselines.Count == 1) { RegressionHeaderText = "Capture at least one more baseline to see a regression comparison."; return; }

        var oldest = Baselines[0];
        var latest = Baselines[^1];
        var elapsed = latest.CapturedAt - oldest.CapturedAt;
        RegressionHeaderText = $"Compared against your {oldest.CapturedAt:d} baseline ({FormatElapsed(elapsed)} ago)";

        if (!oldest.Fingerprint.MatchesHardware(latest.Fingerprint))
        {
            ShowRegressionFingerprintWarning = true;
            RegressionFingerprintWarningText = "Hardware has changed since that baseline (CPU, RAM, disk, or GPU differ) - the comparison below may not be meaningful.";
        }

        foreach (var c in BaselineService.CompareMetrics(oldest, latest))
            if (!string.IsNullOrEmpty(c.SummaryText)) RegressionComparisons.Add(c);
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{span.TotalDays:0} day{(span.TotalDays >= 2 ? "s" : "")}";
        if (span.TotalHours >= 1) return $"{span.TotalHours:0} hour{(span.TotalHours >= 2 ? "s" : "")}";
        return $"{Math.Max(1, span.TotalMinutes):0} minute{(span.TotalMinutes >= 2 ? "s" : "")}";
    }

    /// <summary>#954: charts boot time / idle CPU% / idle RAM committed / WinSAT disk score across
    /// every saved baseline, following PerformanceViewModel.LineOf's glow/core pairing technique -
    /// rebuilt from scratch on every reload (baselines are sparse, on-disk captures, not a
    /// streaming buffer, so there's no in-place ObservableCollection to mutate like the live
    /// charts do). double.NaN marks a gap (that baseline lacks the metric) rather than a
    /// fabricated 0 - LiveCharts2 renders a NaN sample as a break in the line.</summary>
    private void RecomputeTrendChart()
    {
        var ordered = Baselines.ToList();
        string[] labels = ordered.Select(b => b.CapturedAt.ToString("M/d")).ToArray();

        var (bootGlow, bootCore) = LineOf(
            ordered.Select(b => b.LastBootDurationMs is { } ms ? ms / 1000.0 : double.NaN).ToArray(),
            SKColors.DeepSkyBlue, "Boot time (s)");
        var (cpuGlow, cpuCore) = LineOf(
            ordered.Select(b => b.WasIdleAtCapture && b.IdleCpuPercent is { } c ? c : double.NaN).ToArray(),
            SKColors.MediumPurple, "Idle CPU %");
        var (ramGlow, ramCore) = LineOf(
            ordered.Select(b => b.WasIdleAtCapture && b.IdleRamCommittedGb is { } r ? r : double.NaN).ToArray(),
            SKColors.OrangeRed, "Idle RAM committed (GB)");
        var (diskGlow, diskCore) = LineOf(
            ordered.Select(b => b.WinSatDiskScore ?? double.NaN).ToArray(),
            SKColors.LimeGreen, "WinSAT disk score", yAxisIndex: 1);

        TrendSeries = new ISeries[] { bootGlow, bootCore, cpuGlow, cpuCore, ramGlow, ramCore, diskGlow, diskCore };
        TrendXAxes[0].Labels = labels;
        OnPropertyChanged(nameof(TrendSeries));
    }

    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 6f;

    private static (LineSeries<double> Glow, LineSeries<double> Core) LineOf(double[] values, SKColor color, string name, int yAxisIndex = 0)
    {
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), GlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.2,
            IsHoverable = false,
            IsVisibleAtLegend = false,
            ScalesYAt = yAxisIndex,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, CoreStrokeWidth),
            Fill = null,
            // Unlike the live rolling charts (dense, no per-point markers needed), each baseline
            // point here is a whole separate capture worth being able to see individually.
            GeometryStroke = new SolidColorPaint(color, 1.5f),
            GeometrySize = 6,
            LineSmoothness = 0.2,
            ScalesYAt = yAxisIndex,
        };
        return (glow, core);
    }

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks.</summary>
    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        TrendXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        TrendYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        TrendYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        TrendYAxes[1].LabelsPaint = new SolidColorPaint(textSk);
    }

    // ----- #955: change window ----------------------------------------------------------------

    private async Task StartChangeAsync()
    {
        if (IsChangeWindowOpen || IsCapturing) return;

        string label = string.IsNullOrWhiteSpace(ChangeLabelInput) ? "(unlabeled change)" : ChangeLabelInput.Trim();
        var before = await CaptureAndSaveAsync(auto: false, label: $"Before: {label}");
        if (before is null) return;

        _changeBeforeBaseline = before;
        _changeAfterBaseline = null;
        _changeBeforeFiredRuleIds = SnapshotFiredRuleIds();

        ActiveChangeLabel = label;
        ChangeStartedAt = DateTime.Now;
        ChangeDiff = null;
        ChangePerformanceDiff.Clear();
        ChangeRuleTransitions.Clear();
        ChangeHasFingerprintMismatch = false;
        ChangeSummaryText = string.Empty;
        IsChangeWindowOpen = true;
    }

    private async Task FinishChangeAsync()
    {
        if (!IsChangeWindowOpen || _changeBeforeBaseline is null) return;

        var after = await CaptureAndSaveAsync(auto: false, label: $"After: {ActiveChangeLabel}");
        if (after is null) return;

        var before = _changeBeforeBaseline;
        _changeAfterBaseline = after;

        // #955/#950: reuses SnapshotService.Diff directly on the two baselines' embedded
        // SystemSnapshot - the exact same software/services/startup diff SummaryViewModel's
        // existing baseline-vs-current/A-vs-B flows already produce.
        ChangeDiff = SnapshotService.Diff(before.Snapshot, after.Snapshot);

        ChangePerformanceDiff.Clear();
        foreach (var c in BaselineService.CompareMetrics(before, after))
            if (!string.IsNullOrEmpty(c.SummaryText)) ChangePerformanceDiff.Add(c);

        ChangeHasFingerprintMismatch = !before.Fingerprint.MatchesHardware(after.Fingerprint);

        // #958: re-run the exact rule set that was firing at "Start change" and report which
        // cleared, which are still firing, and which are newly firing since then.
        var afterFired = SnapshotFiredRuleIds();
        ChangeRuleTransitions.Clear();
        foreach (var (ruleId, title) in _changeBeforeFiredRuleIds)
            ChangeRuleTransitions.Add(new RuleFindingTransition(ruleId, title, afterFired.ContainsKey(ruleId) ? "Still firing" : "Cleared"));
        foreach (var (ruleId, title) in afterFired)
            if (!_changeBeforeFiredRuleIds.ContainsKey(ruleId))
                ChangeRuleTransitions.Add(new RuleFindingTransition(ruleId, title, "New since the change started"));

        IsChangeWindowOpen = false;
        ChangeSummaryText = BuildChangeSummaryText();
    }

    /// <summary>#958: the rule ids/titles currently firing, via the same RulesEngineService/
    /// metric-bag plumbing SummaryViewModel.RefreshHealthIssues uses for the live Health Check
    /// card - a one-off snapshot rather than a live subscription, since this is only ever taken at
    /// "Start change" and "Finish change".</summary>
    private Dictionary<string, string> SnapshotFiredRuleIds()
    {
        var bag = RulesEngineService.BuildMetricBag(_performance, _energyThermals, _systemSpecs, _services, _processes, out var unavailable);
        var result = _rulesEngine.Evaluate(bag, unavailable);
        return result.Findings
            .Where(f => f.RuleId is { Length: > 0 })
            .ToDictionary(f => f.RuleId!, f => f.Title ?? f.Message, StringComparer.OrdinalIgnoreCase);
    }

    private string BuildChangeSummaryText()
    {
        if (_changeBeforeBaseline is null || _changeAfterBaseline is null) return string.Empty;
        var sb = new StringBuilder();
        sb.Append($"Change: {ActiveChangeLabel}\n");
        sb.Append($"{_changeBeforeBaseline.CapturedAt:g} → {_changeAfterBaseline.CapturedAt:g}\n");

        if (ChangeDiff is { HasChanges: true } diff)
        {
            sb.Append($"Software: +{diff.SoftwareAdded.Count} / -{diff.SoftwareRemoved.Count}, ");
            sb.Append($"Services: +{diff.ServicesAdded.Count} / -{diff.ServicesRemoved.Count}, ");
            sb.Append($"Startup: +{diff.StartupAdded.Count} / -{diff.StartupRemoved.Count}\n");
        }
        else
        {
            sb.Append("No installed-software/service/startup changes detected.\n");
        }

        int cleared = ChangeRuleTransitions.Count(t => t.Status == "Cleared");
        int stillFiring = ChangeRuleTransitions.Count(t => t.Status == "Still firing");
        if (cleared > 0 || stillFiring > 0)
            sb.Append($"Findings: {cleared} cleared, {stillFiring} still firing\n");

        return sb.ToString();
    }

    // ----- #956: before/after report export -----------------------------------------------------

    /// <summary>#956: a dedicated Markdown/HTML export for a completed change window - SaveFileDialog
    /// picks the format by extension, the same pattern TimelineViewModel.ExportRange already uses.</summary>
    private void GenerateChangeReport()
    {
        if (_changeBeforeBaseline is null || _changeAfterBaseline is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export before/after change report",
            Filter = "Markdown files (*.md)|*.md|HTML files (*.html)|*.html|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"TaskManagerPlus-Change-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            bool html = Path.GetExtension(dialog.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, html ? BuildChangeReportHtml() : BuildChangeReportMarkdown());
            CaptureStatusText = $"Report saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            CaptureStatusText = $"Couldn't save the report: {ex.Message}";
        }
    }

    private string BuildChangeReportMarkdown()
    {
        var before = _changeBeforeBaseline!;
        var after = _changeAfterBaseline!;
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# Task Manager Plus - before/after change report");
        Line($"Change: {ActiveChangeLabel}");
        Line($"Before: {before.CapturedAt:F}");
        Line($"After: {after.CapturedAt:F}");
        Line();

        if (ChangeHasFingerprintMismatch)
        {
            Line("> **Warning: hardware changed between the before and after capture (CPU, RAM, disk, or GPU differ) - the numbers below may not be a fair comparison.**");
            Line();
        }

        Line("## What changed (installed software / services / startup)");
        if (ChangeDiff is { HasChanges: true } diff)
        {
            void ListSection(string title, List<string> items)
            {
                if (items.Count == 0) return;
                Line($"**{title}:**");
                foreach (var i in items) Line($"- {i}");
                Line();
            }
            ListSection("Software added", diff.SoftwareAdded);
            ListSection("Software removed", diff.SoftwareRemoved);
            ListSection("Services added", diff.ServicesAdded);
            ListSection("Services removed", diff.ServicesRemoved);
            ListSection("Startup items added", diff.StartupAdded);
            ListSection("Startup items removed", diff.StartupRemoved);
        }
        else
        {
            Line("No installed-software/service/startup changes detected.");
        }
        Line();

        Line("## Performance");
        if (ChangePerformanceDiff.Count == 0)
            Line("No comparable performance figures (idle conditions weren't met at one/both captures, or WinSAT hasn't run on this machine).");
        else
            foreach (var c in ChangePerformanceDiff) Line($"- {c.SummaryText}");
        Line();

        Line("## Findings that were firing at \"Start change\"");
        if (ChangeRuleTransitions.Count == 0)
            Line("No Health Check findings were firing when the change window started.");
        else
            foreach (var t in ChangeRuleTransitions) Line($"- {t.Title}: **{t.Status}**");

        return sb.ToString();
    }

    private string BuildChangeReportHtml()
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var before = _changeBeforeBaseline!;
        var after = _changeAfterBaseline!;
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("<!doctype html><html><head><meta charset=\"utf-8\">");
        Line($"<title>Change report - {Esc(ActiveChangeLabel)}</title>");
        Line("<style>" +
             "body{font-family:Segoe UI,Arial,sans-serif;background:#1c1c1f;color:#e4e4e7;max-width:900px;margin:32px auto;padding:0 16px}" +
             "h1{font-size:20px}h2{font-size:15px;border-bottom:1px solid #3a3a42;padding-bottom:6px;margin-top:28px}" +
             "li{margin:2px 0}.warn{color:#e8b23c}.crit{color:#f26d6d;font-weight:600}</style></head><body>");

        Line($"<h1>Before/after change report</h1><p>Change: {Esc(ActiveChangeLabel)}</p>");
        Line($"<p>Before: {Esc(before.CapturedAt.ToString("F"))} &rarr; After: {Esc(after.CapturedAt.ToString("F"))}</p>");

        if (ChangeHasFingerprintMismatch)
            Line("<p class=\"crit\">Warning: hardware changed between the before and after capture (CPU, RAM, disk, or GPU differ) - the numbers below may not be a fair comparison.</p>");

        Line("<h2>What changed</h2>");
        if (ChangeDiff is { HasChanges: true } diff)
        {
            void ListSection(string title, List<string> items)
            {
                if (items.Count == 0) return;
                Line($"<p><b>{Esc(title)}</b></p><ul>");
                foreach (var i in items) Line($"<li>{Esc(i)}</li>");
                Line("</ul>");
            }
            ListSection("Software added", diff.SoftwareAdded);
            ListSection("Software removed", diff.SoftwareRemoved);
            ListSection("Services added", diff.ServicesAdded);
            ListSection("Services removed", diff.ServicesRemoved);
            ListSection("Startup items added", diff.StartupAdded);
            ListSection("Startup items removed", diff.StartupRemoved);
        }
        else
        {
            Line("<p>No installed-software/service/startup changes detected.</p>");
        }

        Line("<h2>Performance</h2>");
        if (ChangePerformanceDiff.Count == 0)
        {
            Line("<p>No comparable performance figures.</p>");
        }
        else
        {
            Line("<ul>");
            foreach (var c in ChangePerformanceDiff) Line($"<li class=\"{(c.IsRegression ? "warn" : "")}\">{Esc(c.SummaryText)}</li>");
            Line("</ul>");
        }

        Line("<h2>Findings that were firing at \"Start change\"</h2>");
        if (ChangeRuleTransitions.Count == 0)
        {
            Line("<p>None.</p>");
        }
        else
        {
            Line("<ul>");
            foreach (var t in ChangeRuleTransitions) Line($"<li>{Esc(t.Title)}: <b>{Esc(t.Status)}</b></li>");
            Line("</ul>");
        }

        Line("</body></html>");
        return sb.ToString();
    }

    /// <summary>suggestions.md #1000: the Baselines panel's Ctrl+Shift+C Markdown export - the
    /// regression headline plus every captured baseline's headline figures, newest first.</summary>
    private string BuildBaselinesMarkdown()
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# Task Manager Plus baselines");
        Line(RegressionHeaderText);
        if (RegressionComparisons.Count > 0)
        {
            Line();
            foreach (var c in RegressionComparisons) Line($"- {c.SummaryText}");
        }
        Line();
        Line("## Captured baselines");
        if (RecentBaselines.Count == 0)
        {
            Line("None captured yet.");
        }
        else
        {
            Line("| Captured | Label | Boot (s) | Idle CPU % | Idle RAM (GB) | WinSAT disk |");
            Line("|---|---|---|---|---|---|");
            foreach (var b in RecentBaselines)
            {
                string boot = b.LastBootDurationMs is { } ms ? (ms / 1000.0).ToString("0") : "n/a";
                string idleCpu = b.WasIdleAtCapture && b.IdleCpuPercent is { } c ? c.ToString("0.#") : "n/a";
                string idleRam = b.WasIdleAtCapture && b.IdleRamCommittedGb is { } r ? r.ToString("0.#") : "n/a";
                string disk = b.WinSatDiskScore?.ToString("0.#") ?? "n/a";
                Line($"| {b.CapturedAt:g} | {b.Label ?? "(routine)"} | {boot} | {idleCpu} | {idleRam} | {disk} |");
            }
        }
        return sb.ToString();
    }

    // ----- suggestions.md #994: known-good comparison against an imported reference profile -----

    /// <summary>Imports a previously exported PerformanceBaseline JSON (from a known-good machine)
    /// and diffs it against the CURRENT machine: installed software/services/startup
    /// (SnapshotService.Diff, reused verbatim - the same engine #955's before/after change window
    /// already uses), performance figures where this machine has at least one local baseline to
    /// compare against (BaselineService.CompareMetrics, same reuse), and both machines' hardware
    /// fingerprints side by side. Every difference is framed as "different, not necessarily wrong"
    /// in the UI (BaselineView.xaml's copy), never a pass/fail check.</summary>
    private async Task ImportReferenceProfileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a reference profile (an exported baseline .json)",
            Filter = "Baseline files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var imported = BaselineService.LoadFromFile(dialog.FileName);
        if (imported is null)
        {
            ReferenceStatusText = $"Couldn't read \"{Path.GetFileName(dialog.FileName)}\" as a baseline file.";
            return;
        }

        ReferenceProfile = imported;
        OnPropertyChanged(nameof(HasReferenceProfile));

        // Current machine's software/services/startup, captured fresh (not from a saved baseline -
        // #994 asks to compare against the CURRENT machine, not a stale local capture).
        //
        // Genuinely awaited, on an AsyncRelayCommand - the same shape SummaryViewModel's
        // SaveSnapshotCommand/CompareSnapshotCommand already use for this exact call, and for the
        // same reason CaptureAsync was made async in the first place (its driver-inventory and
        // driver-store reads take seconds). This previously blocked the UI thread on
        // GetAwaiter().GetResult(): CaptureAsync awaits Task.Run-backed work that can never
        // complete synchronously, so it always suspended capturing WPF's dispatcher context, then
        // its continuation was posted back to the very thread already blocked waiting for it -
        // a guaranteed, permanent hang of the whole app on Import.
        var currentSnapshot = await SnapshotService.CaptureAsync();
        ReferenceDiff = SnapshotService.Diff(imported.Snapshot, currentSnapshot);

        CurrentFingerprint = BaselineService.BuildCurrentFingerprint(_systemSpecs, _performance.RamTotalGb);
        ReferenceFingerprintMismatch = !imported.Fingerprint.MatchesHardware(CurrentFingerprint);

        ReferencePerformanceComparison.Clear();
        if (Baselines.Count > 0)
        {
            // No local baseline captured on THIS machine to compare performance figures against -
            // the software/services/startup diff and the fingerprint comparison above still work
            // fine without one; only the performance-figures section has nothing to show.
            var latestLocal = Baselines[^1];
            foreach (var c in BaselineService.CompareMetrics(imported, latestLocal))
                if (!string.IsNullOrEmpty(c.SummaryText)) ReferencePerformanceComparison.Add(c);
        }

        ReferenceStatusText = $"Comparing against a reference profile captured {imported.CapturedAt:g}" +
            (string.IsNullOrEmpty(imported.Label) ? string.Empty : $" ({imported.Label})") +
            (Baselines.Count == 0 ? " - capture a baseline on this machine too to compare performance figures." : string.Empty);
    }

    public void Dispose()
    {
        _idleTimer.Stop();
    }
}
