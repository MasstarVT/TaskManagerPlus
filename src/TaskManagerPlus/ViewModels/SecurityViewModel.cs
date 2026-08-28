using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Round 13, #801: the Security tab's ViewModel - on-demand, matching Startup/SystemSpecs/
/// Stability. Nothing here polls; every section is behind its own explicit "Scan"-style button
/// since registry-tree walks and signature checks are far heavier than this app's live-polled tabs
/// do per tick.
///
/// This chunk (#801-810) lands the Persistence section - AutorunsService's RunOnce/RunOnceEx/
/// RunServices/RunServicesOnce/policy Run keys, Winlogon shell chain, Winlogon Notify,
/// AppInit_DLLs, AppCertDlls - plus the baseline-diff, findings, and report-export plumbing every
/// later section (File trust, Process red flags, Protection status, Exposure, Accounts, Bloatware,
/// Cleanup - see suggestions.md) will build on. Each later chunk is expected to add another
/// ObservableCollection&lt;T&gt; + AsyncRelayCommand pair here, the same "pile it all onto one VM"
/// shape StartupViewModel already uses for Scheduled Tasks / browser extensions / shell extensions.
///
/// "Quick flags, not a verdict": every heuristic behind ScanAutorunsCommand is a pattern-match on
/// otherwise-ambiguous data, never a confirmed malware detection - see SecurityFinding's remarks
/// and SecurityView.xaml's header text.
/// </summary>
public sealed class SecurityViewModel : ObservableObject
{
    public ObservableCollection<AutorunEntry> AutorunEntries { get; } = new();
    public ObservableCollection<SecurityFinding> Findings { get; } = new();

    // #803: populated by CompareToBaselineCommand - cleared whenever a fresh scan runs, since a
    // stale diff against entries that no longer match the grid would be misleading.
    public ObservableCollection<AutorunEntry> BaselineAdded { get; } = new();
    public ObservableCollection<AutorunEntry> BaselineRemoved { get; } = new();
    public ObservableCollection<AutorunEntry> BaselineChanged { get; } = new();

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => SetProperty(ref _isScanning, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private AutorunEntry? _selectedAutorunEntry;
    public AutorunEntry? SelectedAutorunEntry { get => _selectedAutorunEntry; set => SetProperty(ref _selectedAutorunEntry, value); }

    private SecurityFinding? _selectedFinding;
    public SecurityFinding? SelectedFinding { get => _selectedFinding; set => SetProperty(ref _selectedFinding, value); }

    private bool _hasBaseline = AutorunsBaselineService.HasBaseline();
    public bool HasBaseline { get => _hasBaseline; private set => SetProperty(ref _hasBaseline, value); }

    // #805: on by default so a report is safe to post to a help forum without a second pass.
    private bool _redactReport = true;
    public bool RedactReport { get => _redactReport; set => SetProperty(ref _redactReport, value); }

    public AsyncRelayCommand ScanAutorunsCommand { get; }
    public RelayCommand SaveBaselineCommand { get; }
    public RelayCommand CompareToBaselineCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand CopyRegistryPathCommand { get; }
    public RelayCommand OpenContainingFolderCommand { get; }

    public SecurityViewModel()
    {
        ScanAutorunsCommand = new AsyncRelayCommand(ScanAutorunsAsync);
        SaveBaselineCommand = new RelayCommand(SaveBaseline, () => AutorunEntries.Count > 0);
        CompareToBaselineCommand = new RelayCommand(CompareToBaseline, () => AutorunEntries.Count > 0);
        ExportReportCommand = new RelayCommand(_ => ExportReport());
        CopyRegistryPathCommand = new RelayCommand(param => CopyRegistryPath(param as AutorunEntry ?? SelectedAutorunEntry));
        OpenContainingFolderCommand = new RelayCommand(param => OpenContainingFolder(param as AutorunEntry ?? SelectedAutorunEntry));
    }

    private async Task ScanAutorunsAsync()
    {
        IsScanning = true;
        StatusMessage = null;
        try
        {
            var (entries, findings) = await Task.Run(() =>
            {
                var scanned = AutorunsService.Scan(out var flagged);
                return (scanned, flagged);
            });

            AutorunEntries.Clear();
            foreach (var entry in entries) AutorunEntries.Add(entry);

            Findings.Clear();
            foreach (var finding in findings) Findings.Add(finding);

            // A previous baseline diff no longer corresponds to the grid contents just replaced.
            BaselineAdded.Clear();
            BaselineRemoved.Clear();
            BaselineChanged.Clear();

            StatusMessage = $"Scanned {entries.Count} persistence entries - {findings.Count} finding(s) flagged for review.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void SaveBaseline()
    {
        AutorunsBaselineService.SaveBaseline(AutorunEntries);
        HasBaseline = AutorunsBaselineService.HasBaseline();
        StatusMessage = HasBaseline
            ? $"Baseline saved: {AutorunEntries.Count} entries."
            : "Couldn't save the baseline.";
    }

    private void CompareToBaseline()
    {
        var diff = AutorunsBaselineService.Diff(AutorunEntries);

        BaselineAdded.Clear();
        BaselineRemoved.Clear();
        BaselineChanged.Clear();

        if (!diff.HasBaseline)
        {
            StatusMessage = "No baseline saved yet - use \"Save baseline\" first.";
            return;
        }

        foreach (var e in diff.Added) BaselineAdded.Add(e);
        foreach (var e in diff.Removed) BaselineRemoved.Add(e);
        foreach (var e in diff.Changed) BaselineChanged.Add(e);

        StatusMessage = $"Compared to baseline: {diff.Added.Count} added, {diff.Removed.Count} removed, {diff.Changed.Count} changed.";
    }

    private void ExportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export security posture report",
            Filter = "Markdown files (*.md)|*.md|HTML files (*.html)|*.html|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"TaskManagerPlus-Security-Report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            string content = ext switch
            {
                ".html" => SecurityReportService.BuildHtmlReport(AutorunEntries, Findings, RedactReport),
                ".csv" => SecurityReportService.BuildCsvReport(AutorunEntries, Findings, RedactReport),
                _ => SecurityReportService.BuildMarkdownReport(AutorunEntries, Findings, RedactReport),
            };
            File.WriteAllText(dialog.FileName, content);
            StatusMessage = $"Report saved to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save report: {ex.Message}";
        }
    }

    private void CopyRegistryPath(AutorunEntry? entry)
    {
        if (entry is null) return;
        try { System.Windows.Clipboard.SetText(entry.Location); }
        catch { /* best-effort - same as MeterTile/VfdMeter's "Copy value" context menu */ }
    }

    private void OpenContainingFolder(AutorunEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ResolvedPath))
        {
            StatusMessage = "This entry has no resolvable file path.";
            return;
        }

        try
        {
            if (!File.Exists(entry.ResolvedPath))
            {
                StatusMessage = "Target file couldn't be found on disk.";
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.ResolvedPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open containing folder: {ex.Message}";
        }
    }
}
