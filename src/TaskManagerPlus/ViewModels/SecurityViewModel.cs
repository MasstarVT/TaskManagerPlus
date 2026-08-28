using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
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

    /// <summary>Round 16, #857: results of the lsass.exe handle-watch scan - see
    /// LsassHandleWatchService. "A short list to eyeball, not an alert."</summary>
    public ObservableCollection<LsassHandleWatchService.Finding> LsassHandleFindings { get; } = new();

    /// <summary>Round 16, #858: unsigned/non-Microsoft-signed processes with a wildcard-listening or
    /// established-outbound TCP connection - see UnsignedNetworkActivityService.</summary>
    public ObservableCollection<UnsignedNetworkActivityService.Finding> UnsignedNetworkFindings { get; } = new();

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

    // #841: on-demand hashing - computed only on click, never during a scan/poll. Held as four
    // separate strings (rather than one formatted block) so the view can show/copy them
    // independently; HashResultPath doubles as "is there a result to show at all".
    private bool _isHashing;
    public bool IsHashing { get => _isHashing; private set => SetProperty(ref _isHashing, value); }

    private string? _hashResultPath;
    public string? HashResultPath { get => _hashResultPath; private set => SetProperty(ref _hashResultPath, value); }

    private string? _hashResultSha256;
    public string? HashResultSha256 { get => _hashResultSha256; private set => SetProperty(ref _hashResultSha256, value); }

    private string? _hashResultMd5;
    public string? HashResultMd5 { get => _hashResultMd5; private set => SetProperty(ref _hashResultMd5, value); }

    private string? _hashResultSha1;
    public string? HashResultSha1 { get => _hashResultSha1; private set => SetProperty(ref _hashResultSha1, value); }

    // #841: "Hash all flagged items" - one copyable text block, "<sha256>  <path>" per line.
    private string? _hashAllFlaggedResult;
    public string? HashAllFlaggedResult { get => _hashAllFlaggedResult; private set => SetProperty(ref _hashAllFlaggedResult, value); }

    // #843: Mark of the Web / Zone.Identifier for the selected entry.
    private string? _motwResultText;
    public string? MotwResultText { get => _motwResultText; private set => SetProperty(ref _motwResultText, value); }

    // #838: on-demand, offline-tolerant revocation check for the selected entry - kept separate
    // from AddFileTrustFindings (which runs automatically at the end of every Scan) since
    // revocation checking can make a network call; see SignatureCheckService.TryCheckRevocation.
    private bool _isCheckingRevocation;
    public bool IsCheckingRevocation { get => _isCheckingRevocation; private set => SetProperty(ref _isCheckingRevocation, value); }

    // #845: separate on-demand pass, not part of ScanAutorunsAsync above - see
    // AutorunsService.CheckWritePermissions's remarks on why.
    private bool _isCheckingWritePermissions;
    public bool IsCheckingWritePermissions { get => _isCheckingWritePermissions; private set => SetProperty(ref _isCheckingWritePermissions, value); }

    // #857: on-demand lsass.exe handle-watch scan.
    private bool _isScanningLsassHandles;
    public bool IsScanningLsassHandles { get => _isScanningLsassHandles; private set => SetProperty(ref _isScanningLsassHandles, value); }

    private string? _lsassHandleScanStatus;
    public string? LsassHandleScanStatus { get => _lsassHandleScanStatus; private set => SetProperty(ref _lsassHandleScanStatus, value); }

    // #858: on-demand unsigned-process-with-network-activity scan.
    private bool _isScanningNetworkActivity;
    public bool IsScanningNetworkActivity { get => _isScanningNetworkActivity; private set => SetProperty(ref _isScanningNetworkActivity, value); }

    private string? _networkActivityScanStatus;
    public string? NetworkActivityScanStatus { get => _networkActivityScanStatus; private set => SetProperty(ref _networkActivityScanStatus, value); }

    public AsyncRelayCommand ScanAutorunsCommand { get; }
    public RelayCommand SaveBaselineCommand { get; }
    public RelayCommand CompareToBaselineCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand CopyRegistryPathCommand { get; }
    public RelayCommand OpenContainingFolderCommand { get; }

    public AsyncRelayCommand HashSelectedCommand { get; }
    public AsyncRelayCommand HashAllFlaggedCommand { get; }
    public RelayCommand CopyHashResultCommand { get; }
    public RelayCommand CopyHashAllFlaggedCommand { get; }
    public RelayCommand LookUpHashCommand { get; }
    public RelayCommand CheckMarkOfTheWebCommand { get; }
    public AsyncRelayCommand CheckRevocationCommand { get; }
    public AsyncRelayCommand CheckWritePermissionsCommand { get; }

    /// <summary>Round 16, #857.</summary>
    public AsyncRelayCommand ScanLsassHandlesCommand { get; }
    /// <summary>Round 16, #858.</summary>
    public AsyncRelayCommand ScanUnsignedNetworkActivityCommand { get; }

    public SecurityViewModel()
    {
        ScanAutorunsCommand = new AsyncRelayCommand(ScanAutorunsAsync);
        SaveBaselineCommand = new RelayCommand(SaveBaseline, () => AutorunEntries.Count > 0);
        CompareToBaselineCommand = new RelayCommand(CompareToBaseline, () => AutorunEntries.Count > 0);
        ExportReportCommand = new RelayCommand(_ => ExportReport());
        CopyRegistryPathCommand = new RelayCommand(param => CopyRegistryPath(param as AutorunEntry ?? SelectedAutorunEntry));
        OpenContainingFolderCommand = new RelayCommand(param => OpenContainingFolder(param as AutorunEntry ?? SelectedAutorunEntry));

        HashSelectedCommand = new AsyncRelayCommand(param => HashSelectedAsync(param as AutorunEntry ?? SelectedAutorunEntry));
        HashAllFlaggedCommand = new AsyncRelayCommand(HashAllFlaggedAsync);
        CopyHashResultCommand = new RelayCommand(CopyHashResult, () => HashResultSha256 is not null);
        CopyHashAllFlaggedCommand = new RelayCommand(() => CopyToClipboard(HashAllFlaggedResult), () => HashAllFlaggedResult is not null);
        LookUpHashCommand = new RelayCommand(LookUpHash, () => HashResultSha256 is not null);
        CheckMarkOfTheWebCommand = new RelayCommand(param => CheckMarkOfTheWeb(param as AutorunEntry ?? SelectedAutorunEntry));
        CheckRevocationCommand = new AsyncRelayCommand(param => CheckRevocationAsync(param as AutorunEntry ?? SelectedAutorunEntry));
        CheckWritePermissionsCommand = new AsyncRelayCommand(CheckWritePermissionsAsync, () => AutorunEntries.Count > 0);

        ScanLsassHandlesCommand = new AsyncRelayCommand(ScanLsassHandlesAsync);
        ScanUnsignedNetworkActivityCommand = new AsyncRelayCommand(ScanUnsignedNetworkActivityAsync);
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

    // #841: on-demand hashing - SHA-256/MD5/SHA-1 computed only on click, never during a scan.
    private async Task HashSelectedAsync(AutorunEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ResolvedPath))
        {
            StatusMessage = "Select a persistence entry with a resolvable file path first.";
            return;
        }
        if (!File.Exists(entry.ResolvedPath))
        {
            StatusMessage = "Target file couldn't be found on disk.";
            return;
        }

        IsHashing = true;
        HashResultPath = null;
        HashResultSha256 = HashResultMd5 = HashResultSha1 = null;
        try
        {
            var result = await Task.Run(() => FileHashService.ComputeHashes(entry.ResolvedPath));
            HashResultPath = result.Path;
            HashResultSha256 = result.Sha256;
            HashResultMd5 = result.Md5;
            HashResultSha1 = result.Sha1;
            StatusMessage = $"Hashed {Path.GetFileName(entry.ResolvedPath)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't hash file: {ex.Message}";
        }
        finally
        {
            IsHashing = false;
        }
    }

    /// <summary>#841: "Hash all flagged items" - runs SHA-256 over every distinct RelatedEntry
    /// path among the current Findings, producing one copyable "hash  path" text block.</summary>
    private async Task HashAllFlaggedAsync()
    {
        var paths = Findings
            .Select(f => f.RelatedEntry?.ResolvedPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            StatusMessage = "No flagged findings with a resolvable file path to hash.";
            return;
        }

        IsHashing = true;
        try
        {
            var text = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var path in paths)
                {
                    try
                    {
                        var result = FileHashService.ComputeHashes(path);
                        sb.AppendLine($"{result.Sha256}  {result.Path}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"(couldn't hash) {path}: {ex.Message}");
                    }
                }
                return sb.ToString();
            });

            HashAllFlaggedResult = text.TrimEnd();
            StatusMessage = $"Hashed {paths.Count} flagged file(s).";
        }
        finally
        {
            IsHashing = false;
        }
    }

    private void CopyHashResult()
    {
        if (HashResultSha256 is null) return;
        CopyToClipboard($"{HashResultPath}\r\nSHA-256: {HashResultSha256}\r\nSHA-1: {HashResultSha1}\r\nMD5: {HashResultMd5}");
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* best-effort - same as CopyRegistryPath above */ }
    }

    /// <summary>#842: opens a browser search for the already-computed SHA-256 - never uploads the
    /// file itself, never contacts anything automatically. Only reachable after #841 has already
    /// computed a hash, and only on an explicit click.</summary>
    private void LookUpHash()
    {
        if (string.IsNullOrWhiteSpace(HashResultSha256)) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.virustotal.com/gui/search/{HashResultSha256}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open browser: {ex.Message}";
        }
    }

    /// <summary>#843: Mark of the Web / Zone.Identifier for the selected entry's file - a small
    /// alternate-data-stream text read, cheap enough to run synchronously on click.</summary>
    private void CheckMarkOfTheWeb(AutorunEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ResolvedPath))
        {
            StatusMessage = "Select a persistence entry with a resolvable file path first.";
            return;
        }

        var info = ZoneIdentifierService.Read(entry.ResolvedPath);
        if (!info.Found)
        {
            MotwResultText = "No Mark of the Web found - not downloaded from the internet (or the file is on a non-NTFS volume).";
            return;
        }

        var lines = new List<string> { $"Zone: {info.ZoneDescription ?? $"Unknown (ZoneId {info.ZoneId})"}" };
        if (!string.IsNullOrWhiteSpace(info.HostUrl)) lines.Add($"Source: {info.HostUrl}");
        if (!string.IsNullOrWhiteSpace(info.ReferrerUrl)) lines.Add($"Referrer: {info.ReferrerUrl}");
        MotwResultText = string.Join(Environment.NewLine, lines);
    }

    /// <summary>#838: on-demand, offline-tolerant revocation check for one entry - not run
    /// automatically during Scan (see AddFileTrustFindings's remarks on why revocation checking
    /// stays out of that automatic pass). "Couldn't check" (offline/no timestamp/timed out) is
    /// reported plainly, never silently treated as "not revoked".</summary>
    private async Task CheckRevocationAsync(AutorunEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ResolvedPath))
        {
            StatusMessage = "Select a persistence entry with a resolvable file path first.";
            return;
        }
        if (!File.Exists(entry.ResolvedPath))
        {
            StatusMessage = "Target file couldn't be found on disk.";
            return;
        }

        IsCheckingRevocation = true;
        try
        {
            var (couldCheck, revoked) = await Task.Run(() => SignatureCheckService.TryCheckRevocation(entry.ResolvedPath, TimeSpan.FromSeconds(8)));
            if (!couldCheck)
            {
                StatusMessage = $"Couldn't check revocation for {entry.Name} (offline, no timestamp, or the check timed out) - this is NOT the same as \"not revoked\".";
                return;
            }

            if (revoked)
            {
                StatusMessage = $"Revocation check: {entry.Name}'s certificate is reported REVOKED.";
                Findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.High,
                    Title = $"Revoked certificate: {entry.Name}",
                    Reason = $"\"{entry.ResolvedPath}\"'s signing certificate is reported revoked by an online revocation check. Quick flag, not a verdict - always confirm independently before acting.",
                    Path = entry.ResolvedPath,
                    WhatDisablingDoes = "A revoked certificate means the issuer no longer vouches for it - treat this file with real suspicion if you don't recognize it.",
                    RelatedEntry = entry,
                });
            }
            else
            {
                StatusMessage = $"Revocation check: {entry.Name}'s certificate is not revoked.";
            }
        }
        finally
        {
            IsCheckingRevocation = false;
        }
    }

    /// <summary>#845: separate on-demand pass over the currently-scanned entries - see
    /// AutorunsService.CheckWritePermissions's remarks on why this isn't folded into ScanAutorunsAsync.</summary>
    private async Task CheckWritePermissionsAsync()
    {
        if (AutorunEntries.Count == 0)
        {
            StatusMessage = "Scan for persistence entries first.";
            return;
        }

        IsCheckingWritePermissions = true;
        try
        {
            var snapshot = AutorunEntries.ToList();
            var newFindings = await Task.Run(() => AutorunsService.CheckWritePermissions(snapshot));
            foreach (var finding in newFindings) Findings.Add(finding);
            StatusMessage = newFindings.Count > 0
                ? $"Write-permission check: {newFindings.Count} finding(s) added."
                : "Write-permission check: no weak ACLs found among the current scan's targets.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Write-permission check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingWritePermissions = false;
        }
    }

    /// <summary>Round 16, #857: on-demand "who's holding a handle to lsass.exe" scan - see
    /// LsassHandleWatchService. Framed as a short list to eyeball, not an alert - legitimate holders
    /// (AV/EDR, Defender, other system processes) are common and expected.</summary>
    private async Task ScanLsassHandlesAsync()
    {
        IsScanningLsassHandles = true;
        LsassHandleScanStatus = null;
        try
        {
            var (findings, error) = await Task.Run(() => LsassHandleWatchService.Scan());

            LsassHandleFindings.Clear();
            foreach (var finding in findings) LsassHandleFindings.Add(finding);

            LsassHandleScanStatus = error ?? $"{findings.Count} process(es) hold a handle to lsass.exe - eyeball this list, it's not an alert.";
        }
        catch (Exception ex)
        {
            LsassHandleScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningLsassHandles = false;
        }
    }

    /// <summary>Round 16, #858: on-demand unsigned/non-Microsoft-signed-process-with-network-activity
    /// scan - see UnsignedNetworkActivityService. TCP-only (see that service's remarks).</summary>
    private async Task ScanUnsignedNetworkActivityAsync()
    {
        IsScanningNetworkActivity = true;
        NetworkActivityScanStatus = null;
        try
        {
            var findings = await Task.Run(() => UnsignedNetworkActivityService.Scan());

            UnsignedNetworkFindings.Clear();
            foreach (var finding in findings) UnsignedNetworkFindings.Add(finding);

            NetworkActivityScanStatus = $"{findings.Count} unsigned/non-Microsoft-signed process(es) with network activity (TCP only). Quick flag, not a verdict.";
        }
        catch (Exception ex)
        {
            NetworkActivityScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningNetworkActivity = false;
        }
    }
}
