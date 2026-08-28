using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>#983: which of the Evidence Bundle panel's sections is showing - the same
/// single-panel-with-swapped-content shape TroubleshootViewModel's own landing/run/past-runs pages
/// already use, just modeled as an explicit stage enum here since there are four sequential steps
/// (checklist → collecting → optional scrub review → done) rather than a couple of independent
/// sibling pages.</summary>
public enum EvidenceBundleStage
{
    Setup,
    Collecting,
    ScrubReview,
    Done,
}

/// <summary>
/// suggestions.md #981-987: backs the Troubleshoot tab's "Evidence Bundle" panel - #983's opt-out
/// checklist, #981's "Collect everything" run (delegated to EvidenceBundleService, one collector
/// per checklist item, each under its own timeout), #984/#985's opt-in PII scrub with a mandatory
/// review screen before anything is finalized, and #987's independent "Copy forum post" command
/// that needs no full collection run at all.
/// </summary>
public sealed class EvidenceBundleViewModel : ObservableObject
{
    private readonly EvidenceBundleService.CollectContext _ctx;
    private ScrubRuleSet _scrubRuleSet = ScrubRulesService.Load();

    public ObservableCollection<EvidenceBundleItem> Items { get; } = new();
    public ObservableCollection<string> ProgressLines { get; } = new();

    /// <summary>#984: the review list itself - original value → placeholder → occurrence count,
    /// across every scrubbable file in this run. Nothing is written to the zip until the user has
    /// seen this and clicked "Looks good, finish bundle".</summary>
    public ObservableCollection<ScrubReplacementSummary> ScrubResults { get; } = new();

    private EvidenceBundleStage _stage = EvidenceBundleStage.Setup;
    public EvidenceBundleStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsSetupStage));
                OnPropertyChanged(nameof(IsCollectingStage));
                OnPropertyChanged(nameof(IsScrubReviewStage));
                OnPropertyChanged(nameof(IsDoneStage));
                // AsyncRelayCommand (unlike RelayCommand) exposes no RaiseCanExecuteChanged of its
                // own - CommandManager.InvalidateRequerySuggested() is exactly what that method
                // calls under the hood, just applied to every command's CanExecute, not only this one.
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsSetupStage => Stage == EvidenceBundleStage.Setup;
    public bool IsCollectingStage => Stage == EvidenceBundleStage.Collecting;
    public bool IsScrubReviewStage => Stage == EvidenceBundleStage.ScrubReview;
    public bool IsDoneStage => Stage == EvidenceBundleStage.Done;

    private bool _scrubPersonalInfo;
    public bool ScrubPersonalInfo { get => _scrubPersonalInfo; set => SetProperty(ref _scrubPersonalInfo, value); }

    private string _estimatedTotalSizeText = string.Empty;
    public string EstimatedTotalSizeText { get => _estimatedTotalSizeText; private set => SetProperty(ref _estimatedTotalSizeText, value); }

    private string _customRedactionText = string.Empty;
    public string CustomRedactionText
    {
        get => _customRedactionText;
        set { if (SetProperty(ref _customRedactionText, value)) AddCustomRedactionCommand.RaiseCanExecuteChanged(); }
    }

    private string _resultSummaryText = string.Empty;
    public string ResultSummaryText { get => _resultSummaryText; private set => SetProperty(ref _resultSummaryText, value); }

    private string _resultPath = string.Empty;
    public string ResultPath { get => _resultPath; private set => SetProperty(ref _resultPath, value); }

    private string _forumPostStatusText = string.Empty;
    public string ForumPostStatusText { get => _forumPostStatusText; private set => SetProperty(ref _forumPostStatusText, value); }

    public AsyncRelayCommand CollectCommand { get; }
    public RelayCommand CancelScrubCommand { get; }
    public AsyncRelayCommand ConfirmScrubCommand { get; }
    public RelayCommand AddCustomRedactionCommand { get; }
    public RelayCommand StartOverCommand { get; }
    public RelayCommand OpenContainingFolderCommand { get; }
    public AsyncRelayCommand CopyForumPostCommand { get; }

    // In-flight run state - reset by ResetToSetup/CancelScrub.
    private string? _stagingDir;
    private string? _destinationZipPath;
    private EvidenceBundleManifest? _manifest;
    private List<HealthIssue> _findingsSnapshot = new();
    private List<TimelineEvent> _timelineSnapshot = new();
    private List<string> _scrubbableFiles = new();
    private string? _cachedSsid;
    private Dictionary<string, string> _pendingScrubbedText = new();

    public EvidenceBundleViewModel(EvidenceBundleService.CollectContext ctx)
    {
        _ctx = ctx;

        foreach (var item in EvidenceBundleService.BuildCatalog())
        {
            item.Changed += RecomputeEstimatedSize;
            Items.Add(item);
        }
        RecomputeEstimatedSize();

        CollectCommand = new AsyncRelayCommand(CollectAsync, () => IsSetupStage && Items.Any(i => i.IsSelected));
        CancelScrubCommand = new RelayCommand(_ => CancelScrub(), _ => IsScrubReviewStage);
        ConfirmScrubCommand = new AsyncRelayCommand(FinalizeAsync, () => IsScrubReviewStage);
        AddCustomRedactionCommand = new RelayCommand(_ => AddCustomRedaction(), _ => IsScrubReviewStage && CustomRedactionText.Trim().Length >= 2);
        StartOverCommand = new RelayCommand(_ => ResetToSetup());
        OpenContainingFolderCommand = new RelayCommand(_ => OpenContainingFolder(), _ => IsDoneStage && !string.IsNullOrEmpty(_destinationZipPath));
        CopyForumPostCommand = new AsyncRelayCommand(CopyForumPostAsync);
    }

    private void RecomputeEstimatedSize()
    {
        long total = Items.Where(i => i.IsSelected).Sum(i => i.EstimatedSizeBytes);
        EstimatedTotalSizeText = $"Estimated total: ~{Formatting.FormatBytes(total)} (a rough guess, not exact)";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>#981: the whole "Collect everything" flow - picks a destination up front (so
    /// nothing is collected if the user backs out of the save dialog), runs every selected
    /// collector into a temp staging folder, then either goes straight to #986's finalize step or,
    /// if #984's scrub toggle is on, previews the scrub and stops at the review screen.</summary>
    private async Task CollectAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save evidence bundle",
            Filter = "ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".zip",
            FileName = $"TaskManagerPlus-EvidenceBundle-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
        };
        if (dialog.ShowDialog() != true) return;
        _destinationZipPath = dialog.FileName;

        _stagingDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-EvidenceBundle-" + Guid.NewGuid().ToString("N"));
        ProgressLines.Clear();
        ScrubResults.Clear();
        Stage = EvidenceBundleStage.Collecting;

        IProgress<string> progress = new Progress<string>(line =>
        {
            ProgressLines.Add(line);
            while (ProgressLines.Count > 80) ProgressLines.RemoveAt(0);
        });

        try
        {
            _manifest = await EvidenceBundleService.CollectAsync(selected, _stagingDir, _ctx, progress, CancellationToken.None);
            _findingsSnapshot = BuildFindingsSnapshot();
            _timelineSnapshot = BuildTimelineSnapshotCheap();

            if (ScrubPersonalInfo)
            {
                progress.Report("Looking up Wi-Fi SSID (netsh wlan show interfaces)...");
                _cachedSsid = await ScrubRulesService.TryGetCurrentSsidAsync();
                _scrubbableFiles = EvidenceBundleService.GetScrubbableFiles(selected, _manifest, _stagingDir);
                RerunScrubPreview();
                Stage = EvidenceBundleStage.ScrubReview;
            }
            else
            {
                await FinalizeAsync();
            }
        }
        catch (Exception ex)
        {
            ProgressLines.Add($"Collection failed: {ex.Message}");
            CleanupStaging();
            Stage = EvidenceBundleStage.Setup;
        }
    }

    /// <summary>#985: "also redact this text" - appends a CustomLiteral rule to the (persisted)
    /// scrub dictionary and immediately re-runs the in-memory preview so the new rule's matches
    /// show up in the review list before anything is finalized.</summary>
    private void AddCustomRedaction()
    {
        string text = CustomRedactionText.Trim();
        if (text.Length < 2) return;

        _scrubRuleSet.Rules.Add(new ScrubRule
        {
            Id = "custom." + Guid.NewGuid().ToString("N"),
            Label = $"Custom: \"{(text.Length > 28 ? text[..28] + "…" : text)}\"",
            Kind = ScrubRuleKind.CustomLiteral,
            PlaceholderPrefix = "CUSTOM",
            LiteralValue = text,
        });
        ScrubRulesService.Save(_scrubRuleSet);
        CustomRedactionText = string.Empty;
        RerunScrubPreview();
    }

    private void RerunScrubPreview()
    {
        var scrubber = PiiScrubber.Build(_scrubRuleSet, _cachedSsid);
        var (byPath, summaries) = EvidenceBundleService.PreviewScrub(_scrubbableFiles, scrubber);
        _pendingScrubbedText = byPath;
        ScrubResults.Clear();
        foreach (var s in summaries) ScrubResults.Add(s);
    }

    private void CancelScrub()
    {
        CleanupStaging();
        _manifest = null;
        ScrubResults.Clear();
        Stage = EvidenceBundleStage.Setup;
    }

    /// <summary>#986: writes manifest.json + index.html and zips the staging folder to the
    /// already-chosen destination. When reached from the scrub review screen, the confirmed
    /// scrubbed text is written to disk first and the affected manifest entries are re-hashed
    /// (#984's rewrite changes both size and hash) before the manifest itself is serialized.</summary>
    private async Task FinalizeAsync()
    {
        if (_stagingDir is null || _manifest is null || _destinationZipPath is null) return;

        bool wasScrubbed = Stage == EvidenceBundleStage.ScrubReview;
        if (wasScrubbed)
        {
            EvidenceBundleService.ApplyScrubResults(_pendingScrubbedText);
            _manifest.WasScrubbed = true;
            RehashScrubbedEntries();
        }

        Stage = EvidenceBundleStage.Collecting;
        ProgressLines.Add("Writing manifest.json and index.html...");

        var manifest = _manifest;
        var stagingDir = _stagingDir;
        var destinationZipPath = _destinationZipPath;
        var findings = _findingsSnapshot;
        var timeline = _timelineSnapshot;
        var specs = _ctx.SystemSpecs;

        try
        {
            await Task.Run(() =>
            {
                EvidenceBundleService.WriteManifest(manifest, stagingDir);
                var theme = SummarySettingsService.Load().ReportTheme;
                var html = EvidenceBundleService.BuildIndexHtml(manifest, findings, timeline, specs, theme);
                File.WriteAllText(Path.Combine(stagingDir, "index.html"), html);
                EvidenceBundleService.CreateZip(stagingDir, destinationZipPath);
            });

            int successCount = manifest.Entries.Count(e => e.Success);
            int failCount = manifest.Entries.Count(e => !e.Success);
            long totalSize = File.Exists(destinationZipPath) ? new FileInfo(destinationZipPath).Length : 0;
            int redactions = ScrubResults.Sum(s => s.OccurrenceCount);

            ResultSummaryText = $"Bundle saved: {successCount} item(s) collected, {failCount} not collected, {Formatting.FormatBytes(totalSize)} zipped." +
                (wasScrubbed ? $" Personal info was scrubbed ({redactions} replacement(s))." : string.Empty);
            ResultPath = destinationZipPath;
            Stage = EvidenceBundleStage.Done;
        }
        catch (Exception ex)
        {
            ProgressLines.Add($"Couldn't finish the bundle: {ex.Message}");
            Stage = EvidenceBundleStage.Setup;
        }
        finally
        {
            CleanupStaging();
        }
    }

    private void RehashScrubbedEntries()
    {
        if (_manifest is null || _stagingDir is null) return;
        var scrubbedPaths = new HashSet<string>(_pendingScrubbedText.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _manifest.Entries.Where(e => e.Success))
        {
            string fullPath = Path.Combine(_stagingDir, entry.FileName.Replace('/', Path.DirectorySeparatorChar));
            if (!scrubbedPaths.Contains(fullPath) || !File.Exists(fullPath)) continue;
            entry.SizeBytes = new FileInfo(fullPath).Length;
            entry.Sha256 = EvidenceBundleService.ComputeSha256(fullPath);
        }
    }

    private void ResetToSetup()
    {
        ProgressLines.Clear();
        ScrubResults.Clear();
        ResultSummaryText = string.Empty;
        ResultPath = string.Empty;
        _destinationZipPath = null;
        _manifest = null;
        Stage = EvidenceBundleStage.Setup;
    }

    private void OpenContainingFolder()
    {
        if (_destinationZipPath is not { Length: > 0 } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    private void CleanupStaging()
    {
        if (_stagingDir is null) return;
        try { if (Directory.Exists(_stagingDir)) Directory.Delete(_stagingDir, recursive: true); }
        catch { /* best-effort - a leftover temp folder isn't worth failing over */ }
        _stagingDir = null;
    }

    private List<HealthIssue> BuildFindingsSnapshot()
    {
        try
        {
            var bag = RulesEngineService.BuildMetricBag(_ctx.Performance, _ctx.EnergyThermals, _ctx.SystemSpecs, _ctx.Services, _ctx.Processes, out var unavailable);
            var result = _ctx.RulesEngine.Evaluate(bag, unavailable);
            return SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);
        }
        catch
        {
            return new List<HealthIssue>();
        }
    }

    /// <summary>The cheap subset of TimelineService's lanes (no driver-install parsing, which needs
    /// a shell-out) - good enough for index.html's inline table; the full picture (if selected)
    /// still goes into AppData\timeline.json via the AppTimeline collector proper.</summary>
    private static List<TimelineEvent> BuildTimelineSnapshotCheap()
    {
        try
        {
            var events = new List<TimelineEvent>();
            events.AddRange(TimelineService.GetReliabilityCrashEvents());
            events.AddRange(TimelineService.GetServiceFailureEvents());
            events.AddRange(TimelineService.GetWindowsUpdateEvents());
            events.AddRange(TimelineService.GetSoftwareInstallEvents());
            events.AddRange(ThermalEventLogService.ReadAll());
            return events.OrderByDescending(e => e.Timestamp).ToList();
        }
        catch
        {
            return new List<TimelineEvent>();
        }
    }

    /// <summary>#987: builds a scrubbed, forum-ready Markdown block and copies it to the clipboard
    /// - runs entirely independently of the checklist/collection flow above, reusing only cheap,
    /// already-live data (SystemSpecsViewModel, a fresh rules-engine pass, the Services/Processes
    /// tabs' already-polled collections, and TimelineService's Crashes lane), the same "genuinely
    /// shorter than the full report, not that report reformatted" shape CopySummary already
    /// established. Always scrubbed (not gated behind the checklist's opt-in checkbox) - a forum
    /// post is explicitly meant to be pasted somewhere public.</summary>
    private async Task CopyForumPostAsync()
    {
        try
        {
            ForumPostStatusText = "Building forum post...";
            string markdown = await Task.Run(BuildForumPostMarkdown);

            string? ssid = await ScrubRulesService.TryGetCurrentSsidAsync();
            var scrubber = PiiScrubber.Build(_scrubRuleSet, ssid);
            string scrubbed = scrubber.Scrub(markdown);

            System.Windows.Clipboard.SetText(scrubbed);
            int redactions = scrubber.Summaries.Sum(s => s.OccurrenceCount);
            ForumPostStatusText = redactions > 0
                ? $"Copied to clipboard - {redactions} personal-info value(s) redacted."
                : "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            ForumPostStatusText = $"Couldn't build forum post: {ex.Message}";
        }
    }

    private string BuildForumPostMarkdown()
    {
        var bag = RulesEngineService.BuildMetricBag(_ctx.Performance, _ctx.EnergyThermals, _ctx.SystemSpecs, _ctx.Services, _ctx.Processes, out var unavailable);
        var result = _ctx.RulesEngine.Evaluate(bag, unavailable);
        var top = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact).Take(5).ToList();
        var crashes = TimelineService.GetReliabilityCrashEvents().OrderByDescending(e => e.Timestamp).Take(5).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("### System specs");
        sb.AppendLine($"- **OS:** {_ctx.SystemSpecs.OsName} ({_ctx.SystemSpecs.OsDetails})");
        sb.AppendLine($"- **Model:** {_ctx.SystemSpecs.SystemModel}");
        sb.AppendLine($"- **CPU:** {_ctx.SystemSpecs.CpuName} — {_ctx.SystemSpecs.CpuDetails}");
        sb.AppendLine($"- **RAM:** {_ctx.SystemSpecs.RamTotal} ({_ctx.SystemSpecs.RamDetails})");
        sb.AppendLine();

        sb.AppendLine("### Top findings");
        if (top.Count == 0)
        {
            sb.AppendLine("No issues detected.");
        }
        else
        {
            foreach (var f in top)
            {
                string evidence = f.ImpactText is { Length: > 0 } imp ? imp : (f.Evidence.FirstOrDefault()?.Value ?? string.Empty);
                sb.AppendLine($"- **[{f.Severity}]** {f.Title ?? f.Message} — {f.Message}" +
                              (evidence.Length > 0 ? $" _(evidence: {evidence})_" : string.Empty));
            }
        }
        sb.AppendLine();

        sb.AppendLine("### Recent crash timeline");
        if (crashes.Count == 0)
        {
            sb.AppendLine("No crash-like events found in Win32_ReliabilityRecords.");
        }
        else
        {
            foreach (var c in crashes) sb.AppendLine($"- `{c.Timestamp:g}` — {c.Title}: {c.Detail}");
        }
        sb.AppendLine();

        sb.AppendLine("<details><summary>Services (click to expand)</summary>");
        sb.AppendLine();
        sb.AppendLine("| Service | Status | Start type |");
        sb.AppendLine("|---|---|---|");
        foreach (var s in _ctx.Services.Services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"| {s.DisplayName} | {s.Status} | {s.StartType} |");
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        sb.AppendLine("<details><summary>Top processes by memory (click to expand)</summary>");
        sb.AppendLine();
        sb.AppendLine("| Process | PID | Memory | CPU % |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var p in _ctx.Processes.Processes.OrderByDescending(p => p.MemoryBytes).Take(20))
            sb.AppendLine($"| {p.Name} | {p.Pid} | {Formatting.FormatBytes(p.MemoryBytes)} | {p.CpuPercent:0.0} |");
        sb.AppendLine();
        sb.AppendLine("</details>");

        return sb.ToString();
    }
}
