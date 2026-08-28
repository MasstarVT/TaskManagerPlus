using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
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

    // ==================================================================================
    // Round 17, #859-870: "Protection status" section - Defender/third-party AV/ASR
    // posture. See DefenderService for the read logic; everything here is on-demand,
    // grouped the same way as DefenderService's own comment explains (cheap reads under one
    // "Refresh protection status" action, event-log/quarantine/scan actions each behind
    // their own button).
    // ==================================================================================

    private DefenderService.ComputerStatus? _defenderStatus;
    public DefenderService.ComputerStatus? DefenderStatus { get => _defenderStatus; private set => SetProperty(ref _defenderStatus, value); }

    private DefenderService.TamperProtectionStatus? _tamperProtection;
    public DefenderService.TamperProtectionStatus? TamperProtection { get => _tamperProtection; private set => SetProperty(ref _tamperProtection, value); }

    private DefenderService.FeatureToggles? _featureToggles;
    public DefenderService.FeatureToggles? FeatureToggles { get => _featureToggles; private set => SetProperty(ref _featureToggles, value); }

    private DefenderService.PolicyDiagnosis? _policyDiagnosis;
    public DefenderService.PolicyDiagnosis? PolicyDiagnosis { get => _policyDiagnosis; private set => SetProperty(ref _policyDiagnosis, value); }

    public ObservableCollection<DefenderService.ExclusionEntry> ExclusionAudit { get; } = new();
    public ObservableCollection<DefenderService.AsrRuleStatus> AsrRules { get; } = new();

    private bool _isRefreshingProtectionStatus;
    public bool IsRefreshingProtectionStatus { get => _isRefreshingProtectionStatus; private set => SetProperty(ref _isRefreshingProtectionStatus, value); }

    private string? _protectionStatusMessage;
    public string? ProtectionStatusMessage { get => _protectionStatusMessage; private set => SetProperty(ref _protectionStatusMessage, value); }

    // #861: scan history event timeline (separate from the cheap QuickScan*/FullScan* times on
    // DefenderStatus above, since this walks the Operational log).
    public ObservableCollection<DefenderService.DefenderTimelineEvent> ScanHistoryTimeline { get; } = new();
    private bool _isLoadingScanHistory;
    public bool IsLoadingScanHistory { get => _isLoadingScanHistory; private set => SetProperty(ref _isLoadingScanHistory, value); }
    private string? _scanHistoryStatus;
    public string? ScanHistoryStatus { get => _scanHistoryStatus; private set => SetProperty(ref _scanHistoryStatus, value); }

    // #862: threat detection history (WMI detections + event timeline).
    public ObservableCollection<DefenderService.ThreatDetectionRecord> ThreatDetections { get; } = new();
    public ObservableCollection<DefenderService.DefenderTimelineEvent> ThreatEventTimeline { get; } = new();
    private bool _isLoadingThreatHistory;
    public bool IsLoadingThreatHistory { get => _isLoadingThreatHistory; private set => SetProperty(ref _isLoadingThreatHistory, value); }
    private string? _threatHistoryStatus;
    public string? ThreatHistoryStatus { get => _threatHistoryStatus; private set => SetProperty(ref _threatHistoryStatus, value); }

    // #863: quarantine browser.
    public ObservableCollection<DefenderService.QuarantineItem> QuarantineItems { get; } = new();
    private bool _isLoadingQuarantine;
    public bool IsLoadingQuarantine { get => _isLoadingQuarantine; private set => SetProperty(ref _isLoadingQuarantine, value); }
    private string? _quarantineStatus;
    public string? QuarantineStatus { get => _quarantineStatus; private set => SetProperty(ref _quarantineStatus, value); }

    // #867: ASR block/audit event timeline (rule configuration itself lives in AsrRules above,
    // populated by the cheap RefreshProtectionStatusCommand).
    public ObservableCollection<DefenderService.DefenderTimelineEvent> AsrEventTimeline { get; } = new();
    private bool _isLoadingAsrEvents;
    public bool IsLoadingAsrEvents { get => _isLoadingAsrEvents; private set => SetProperty(ref _isLoadingAsrEvents, value); }
    private string? _asrEventStatus;
    public string? AsrEventStatus { get => _asrEventStatus; private set => SetProperty(ref _asrEventStatus, value); }

    // #864: run a scan - streamed output, Cancel support.
    public ObservableCollection<string> ScanOutputLines { get; } = new();
    private Process? _runningScanProcess;
    private bool _isScanRunning;
    public bool IsScanRunning { get => _isScanRunning; private set => SetProperty(ref _isScanRunning, value); }
    private string? _scanRunStatus;
    public string? ScanRunStatus { get => _scanRunStatus; private set => SetProperty(ref _scanRunStatus, value); }

    public AsyncRelayCommand RefreshProtectionStatusCommand { get; }
    public AsyncRelayCommand LoadScanHistoryCommand { get; }
    public AsyncRelayCommand LoadThreatHistoryCommand { get; }
    public AsyncRelayCommand LoadQuarantineCommand { get; }
    public RelayCommand RestoreQuarantineItemCommand { get; }
    public RelayCommand PurgeQuarantineItemCommand { get; }
    public AsyncRelayCommand LoadAsrEventsCommand { get; }
    public RelayCommand RunQuickScanCommand { get; }
    public RelayCommand RunFullScanCommand { get; }
    public RelayCommand RunFolderScanCommand { get; }
    public RelayCommand RunOfflineScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }

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

        RefreshPlatformSecurityCommand = new AsyncRelayCommand(RefreshPlatformSecurityAsync);

        RefreshProtectionStatusCommand = new AsyncRelayCommand(RefreshProtectionStatusAsync);
        LoadScanHistoryCommand = new AsyncRelayCommand(LoadScanHistoryAsync);
        LoadThreatHistoryCommand = new AsyncRelayCommand(LoadThreatHistoryAsync);
        LoadQuarantineCommand = new AsyncRelayCommand(LoadQuarantineAsync);
        RestoreQuarantineItemCommand = new RelayCommand(param => RestoreQuarantineItem(param as DefenderService.QuarantineItem));
        PurgeQuarantineItemCommand = new RelayCommand(param => PurgeQuarantineItem(param as DefenderService.QuarantineItem));
        LoadAsrEventsCommand = new AsyncRelayCommand(LoadAsrEventsAsync);
        RunQuickScanCommand = new RelayCommand(_ => RunScan(DefenderService.DefenderScanType.Quick), _ => !IsScanRunning);
        RunFullScanCommand = new RelayCommand(_ => RunScan(DefenderService.DefenderScanType.Full), _ => !IsScanRunning);
        RunFolderScanCommand = new RelayCommand(_ => RunFolderScan(), _ => !IsScanRunning);
        RunOfflineScanCommand = new RelayCommand(_ => RunOfflineScan(), _ => !IsScanRunning);
        CancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanRunning);

        // Round 19, #881-890: "Network exposure" section.
        RefreshFirewallPostureCommand = new AsyncRelayCommand(RefreshFirewallPostureAsync);
        ScanFirewallRulesCommand = new AsyncRelayCommand(ScanFirewallRulesAsync);
        DisableFirewallRuleCommand = new AsyncRelayCommand(param => DisableFirewallRuleAsync(param as FirewallService.FirewallRuleInfo));
        UndoDisableFirewallRuleCommand = new AsyncRelayCommand(param => UndoDisableFirewallRuleAsync(param as string));
        ScanExposedListenersCommand = new AsyncRelayCommand(ScanExposedListenersAsync);
        RefreshSmbPostureCommand = new AsyncRelayCommand(RefreshSmbPostureAsync);
        ScanSharesCommand = new AsyncRelayCommand(ScanSharesAsync);
        ScanRemoteManagementCommand = new AsyncRelayCommand(ScanRemoteManagementAsync);
        ScanHostsFileCommand = new AsyncRelayCommand(ScanHostsFileAsync);
        OpenHostsFileInNotepadCommand = new RelayCommand(_ => OpenHostsFileInNotepad());
        CheckDnsPostureForFindingsCommand = new AsyncRelayCommand(CheckDnsPostureForFindingsAsync);
        RefreshProxyPostureCommand = new AsyncRelayCommand(RefreshProxyPostureAsync);
        ResetProxyAndWinsockCommand = new AsyncRelayCommand(ResetProxyAndWinsockAsync);
        ScanCertificateStoreCommand = new AsyncRelayCommand(ScanCertificateStoreAsync);
        OpenCertificateManagerCommand = new RelayCommand(_ => OpenCertificateManager());
    }

    /// <summary>#859/#860/#865/#866/#868/#869/#870: everything cheap enough to read on one click -
    /// no event-log walk, no shelling out. See DefenderService's class remarks for why this split
    /// exists.</summary>
    private async Task RefreshProtectionStatusAsync()
    {
        IsRefreshingProtectionStatus = true;
        ProtectionStatusMessage = null;
        try
        {
            // Snapshot on the UI thread first (ObservableCollection isn't thread-safe to enumerate
            // concurrently with a UI-thread mutation) - same discipline CheckWritePermissionsAsync
            // already uses above for the same AutorunEntries collection.
            var persistenceSnapshot = AutorunEntries.Count > 0 ? AutorunEntries.ToList() : null;

            var result = await Task.Run(() =>
            {
                var (status, statusFindings) = DefenderService.ReadComputerStatus();
                var tamper = DefenderService.ReadTamperProtection(status.IsTamperProtected);
                var toggles = DefenderService.ReadFeatureToggles();
                var puaFinding = DefenderService.BuildPuaProtectionFinding(toggles);
                var exclusions = DefenderService.ReadExclusionsExtended();
                var exclusionFindings = DefenderService.BuildExclusionFindings(exclusions);
                var asrRules = DefenderService.ReadAsrRules(out var asrQueryOk);
                var avProducts = DefenderService.ReadAntivirusProducts(out _);
                var policyDiagnosis = DefenderService.DiagnosePolicyState(avProducts);
                var policyFinding = DefenderService.BuildPolicyFinding(policyDiagnosis);
                var duplicateFinding = DefenderService.DiagnoseDuplicatedRealTimeScanners(avProducts, persistenceSnapshot ?? AutorunsService.Scan());

                var allFindings = new List<SecurityFinding>();
                allFindings.AddRange(statusFindings);
                if (puaFinding is not null) allFindings.Add(puaFinding);
                allFindings.AddRange(exclusionFindings);
                allFindings.Add(policyFinding);
                if (duplicateFinding is not null) allFindings.Add(duplicateFinding);

                return (status, tamper, toggles, exclusions, asrRules, asrQueryOk, policyDiagnosis, allFindings);
            });

            DefenderStatus = result.status;
            TamperProtection = result.tamper;
            FeatureToggles = result.toggles;
            PolicyDiagnosis = result.policyDiagnosis;

            ExclusionAudit.Clear();
            foreach (var e in result.exclusions) ExclusionAudit.Add(e);

            AsrRules.Clear();
            foreach (var r in result.asrRules) AsrRules.Add(r);

            foreach (var f in result.allFindings) Findings.Add(f);

            ProtectionStatusMessage = result.status.Available
                ? $"Protection status refreshed - {result.exclusions.Count} exclusion(s), {result.allFindings.Count} new finding(s). ASR rule configuration {(result.asrQueryOk ? "read" : "unavailable")}."
                : $"Couldn't read Defender's computer status: {result.status.QueryError}";
        }
        catch (Exception ex)
        {
            ProtectionStatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshingProtectionStatus = false;
        }
    }

    private async Task LoadScanHistoryAsync()
    {
        IsLoadingScanHistory = true;
        try
        {
            var (events, logAvailable) = await Task.Run(() =>
            {
                var e = DefenderService.ReadScanHistoryEvents(out var ok);
                return (e, ok);
            });

            ScanHistoryTimeline.Clear();
            foreach (var e in events) ScanHistoryTimeline.Add(e);

            ScanHistoryStatus = logAvailable
                ? $"{events.Count} scan event(s) in the last 90 days."
                : "Couldn't read the Defender Operational event log (unavailable, access denied, or the channel isn't enabled).";
        }
        catch (Exception ex)
        {
            ScanHistoryStatus = $"Couldn't load scan history: {ex.Message}";
        }
        finally
        {
            IsLoadingScanHistory = false;
        }
    }

    private async Task LoadThreatHistoryAsync()
    {
        IsLoadingThreatHistory = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var detections = DefenderService.ReadThreatDetectionsWmi(out var wmiOk);
                var events = DefenderService.ReadThreatEvents(out var logOk);
                var findings = DefenderService.BuildThreatFindings(detections, events);
                return (detections, events, findings, wmiOk, logOk);
            });

            ThreatDetections.Clear();
            foreach (var d in result.detections) ThreatDetections.Add(d);

            ThreatEventTimeline.Clear();
            foreach (var e in result.events) ThreatEventTimeline.Add(e);

            foreach (var f in result.findings) Findings.Add(f);

            string logNote = result.logOk ? string.Empty : " (couldn't read the Defender Operational event log)";
            ThreatHistoryStatus = $"{result.detections.Count} WMI detection(s), {result.events.Count} event(s) in the last 90 days{logNote}" +
                (result.findings.Count > 0 ? $" - {result.findings.Count} new finding(s)." : ".");
        }
        catch (Exception ex)
        {
            ThreatHistoryStatus = $"Couldn't load threat history: {ex.Message}";
        }
        finally
        {
            IsLoadingThreatHistory = false;
        }
    }

    private async Task LoadQuarantineAsync()
    {
        IsLoadingQuarantine = true;
        try
        {
            var (items, error) = await Task.Run(() =>
            {
                var i = DefenderService.ListQuarantine(out var err);
                return (i, err);
            });

            QuarantineItems.Clear();
            foreach (var i in items) QuarantineItems.Add(i);

            QuarantineStatus = error ?? $"{items.Count} quarantined item(s).";
        }
        catch (Exception ex)
        {
            QuarantineStatus = $"Couldn't list quarantined items: {ex.Message}";
        }
        finally
        {
            IsLoadingQuarantine = false;
        }
    }

    /// <summary>#863: owner-initiated only - never runs automatically. Confirmation dialog matches
    /// ProcessesViewModel.EndSelected's MessageBox.Show(YesNo, Warning) convention.</summary>
    private void RestoreQuarantineItem(DefenderService.QuarantineItem? item)
    {
        if (item is null) return;
        var confirm = MessageBox.Show(
            $"Restore \"{item.ThreatName}\" to its original location ({item.FilePath})?\nThis brings back a file Defender quarantined - only do this if you're sure it's safe.",
            "Restore quarantined item", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, output) = DefenderService.RestoreQuarantineItem(item.ThreatName);
        QuarantineStatus = success ? $"Restored \"{item.ThreatName}\": {output}" : $"Restore failed for \"{item.ThreatName}\": {output}";
    }

    /// <summary>#863: MpCmdRun has no cleanly-documented per-item permanent-purge switch - implemented
    /// as restore, then delete the restored file, per the item's own allowed interpretation.</summary>
    private void PurgeQuarantineItem(DefenderService.QuarantineItem? item)
    {
        if (item is null) return;
        var confirm = MessageBox.Show(
            $"Permanently purge \"{item.ThreatName}\" ({item.FilePath})?\nMpCmdRun has no direct per-item purge command, so this restores the file and then immediately deletes it - the net effect is the same as a permanent delete, but briefly touches disk as a real file. This cannot be undone.",
            "Purge quarantined item", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, output) = DefenderService.PurgeQuarantineItem(item.ThreatName, item.FilePath);
        QuarantineStatus = success ? $"Purged \"{item.ThreatName}\": {output}" : $"Purge failed for \"{item.ThreatName}\": {output}";
    }

    private async Task LoadAsrEventsAsync()
    {
        IsLoadingAsrEvents = true;
        try
        {
            var (events, logAvailable) = await Task.Run(() =>
            {
                var e = DefenderService.ReadAsrEvents(out var ok);
                return (e, ok);
            });

            AsrEventTimeline.Clear();
            foreach (var e in events) AsrEventTimeline.Add(e);

            AsrEventStatus = logAvailable
                ? $"{events.Count} ASR block/audit event(s) in the last 90 days."
                : "Couldn't read the Defender Operational event log (unavailable, access denied, or the channel isn't enabled).";
        }
        catch (Exception ex)
        {
            AsrEventStatus = $"Couldn't load ASR events: {ex.Message}";
        }
        finally
        {
            IsLoadingAsrEvents = false;
        }
    }

    /// <summary>#864: Quick/Full scan - streamed output line-by-line via
    /// DefenderService.StartStreamingProcess, marshaled to the UI thread the same way
    /// StartupViewModel/NetworkViewModel/StorageViewModel/CpuViewModel already do for cross-thread
    /// ObservableCollection updates.</summary>
    private void RunScan(DefenderService.DefenderScanType type, string? customPath = null)
    {
        if (IsScanRunning) return;

        ScanOutputLines.Clear();
        ScanRunStatus = type switch
        {
            DefenderService.DefenderScanType.Quick => "Running a Quick scan...",
            DefenderService.DefenderScanType.Full => "Running a Full scan - this can take a long time.",
            _ => $"Scanning \"{customPath}\"...",
        };
        IsScanRunning = true;

        try
        {
            string exe = DefenderService.ResolveMpCmdRunPath();
            string args = DefenderService.BuildScanArgs(type, customPath);
            _runningScanProcess = DefenderService.StartStreamingProcess(exe, args, AppendScanOutputLine);
            _runningScanProcess.Exited += (_, _) => OnScanProcessExited();
        }
        catch (Exception ex)
        {
            IsScanRunning = false;
            ScanRunStatus = $"Couldn't start the scan: {ex.Message}";
        }
    }

    /// <summary>#864: "Scan this folder..." - Microsoft.Win32.OpenFolderDialog (.NET 8 WPF), the
    /// same Microsoft.Win32.*Dialog family this ViewModel already uses for Save/OpenFileDialog.</summary>
    private void RunFolderScan()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for Windows Defender to scan" };
        if (dialog.ShowDialog() != true) return;
        RunScan(DefenderService.DefenderScanType.Custom, dialog.FolderName);
    }

    /// <summary>#864: Defender Offline scan - restarts the PC, so this requires an explicit,
    /// prominent confirmation naming that consequence before running Start-MpWDOScan.</summary>
    private void RunOfflineScan()
    {
        if (IsScanRunning) return;

        var confirm = MessageBox.Show(
            "This starts a Windows Defender Offline scan, which RESTARTS THE PC immediately to scan outside of Windows.\n\nSave any open work before continuing. Continue?",
            "Defender Offline scan - this restarts the PC", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        ScanOutputLines.Clear();
        ScanRunStatus = "Starting Defender Offline scan (Start-MpWDOScan) - the PC will restart shortly.";
        IsScanRunning = true;

        try
        {
            var (exe, args) = DefenderService.BuildOfflineScanCommand();
            _runningScanProcess = DefenderService.StartStreamingProcess(exe, args, AppendScanOutputLine);
            _runningScanProcess.Exited += (_, _) => OnScanProcessExited();
        }
        catch (Exception ex)
        {
            IsScanRunning = false;
            ScanRunStatus = $"Couldn't start the offline scan: {ex.Message}";
        }
    }

    private void CancelScan()
    {
        try { _runningScanProcess?.Kill(entireProcessTree: true); }
        catch { /* best-effort - it may have already exited */ }
        ScanRunStatus = "Scan cancelled.";
    }

    private void AppendScanOutputLine(string line)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ScanOutputLines.Add(line);
            // Cap the visible log so a long full scan doesn't grow this collection without bound.
            while (ScanOutputLines.Count > 2000) ScanOutputLines.RemoveAt(0);
        });
    }

    private void OnScanProcessExited()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            int? exitCode = null;
            try { exitCode = _runningScanProcess?.ExitCode; } catch { /* process object disposed/inaccessible */ }
            ScanRunStatus = $"Scan finished (exit code {(exitCode?.ToString() ?? "unknown")}).";
            IsScanRunning = false;
            _runningScanProcess = null;
        });
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

    // ==================================================================================
    // Round 18, #871-880: "Platform security" section - HVCI/VBS detail/LSA protection/Kernel DMA
    // Protection/vulnerable-driver blocklist/boot integrity switches/app control policy presence/
    // extended TPM detail/Secure Boot detail/UAC audit. See PlatformSecurityService for the read
    // logic - everything here is on-demand behind one "Refresh platform security" button, matching
    // this item's own framing (a single Scan/Refresh button, one IsLoading flag, one StatusMessage).
    // ==================================================================================

    private PlatformSecurityService.PlatformSecurityInfo? _platformSecurity;
    public PlatformSecurityService.PlatformSecurityInfo? PlatformSecurity { get => _platformSecurity; private set => SetProperty(ref _platformSecurity, value); }

    private bool _isLoadingPlatformSecurity;
    public bool IsLoadingPlatformSecurity { get => _isLoadingPlatformSecurity; private set => SetProperty(ref _isLoadingPlatformSecurity, value); }

    private string? _platformSecurityStatus;
    public string? PlatformSecurityStatus { get => _platformSecurityStatus; private set => SetProperty(ref _platformSecurityStatus, value); }

    public AsyncRelayCommand RefreshPlatformSecurityCommand { get; }

    /// <summary>#871-880: reads everything in PlatformSecurityService.ReadAll in one shot - every
    /// individual read (registry/WMI/two capped event-log queries/a bcdedit shell-out/an AppLocker
    /// PowerShell call) is cheap/bounded on its own, so unlike Protection status above this doesn't
    /// need to split event-log reads into their own separate buttons.</summary>
    private async Task RefreshPlatformSecurityAsync()
    {
        IsLoadingPlatformSecurity = true;
        PlatformSecurityStatus = null;
        try
        {
            // Snapshot on the UI thread first - same discipline RefreshProtectionStatusAsync above
            // already uses before handing AutorunEntries to a background thread.
            var persistenceSnapshot = AutorunEntries.Count > 0 ? AutorunEntries.ToList() : null;

            var result = await Task.Run(() => PlatformSecurityService.ReadAll(persistenceSnapshot));

            PlatformSecurity = result.Info;
            foreach (var f in result.Findings) Findings.Add(f);

            PlatformSecurityStatus = $"Platform security posture refreshed - {result.Findings.Count} new finding(s).";
        }
        catch (Exception ex)
        {
            PlatformSecurityStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoadingPlatformSecurity = false;
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

    // ==================================================================================
    // Round 19, #881-890: "Network exposure" section - firewall profile posture + rule audit,
    // exposed-listener map, SMB/legacy-protocol posture, share audit, remote-management exposure,
    // hosts-file audit, a DNS-posture finding mirror (the DNS posture card itself lives on the
    // Network tab - see NetworkViewModel/DnsPostureService's remarks), a proxy hijack check +
    // reset action, and certificate store anomalies. Same "quick flag, not a verdict" / on-demand-
    // behind-its-own-button framing as every other section on this tab.
    // ==================================================================================

    // #881: firewall profile posture.
    public ObservableCollection<FirewallService.FirewallProfileInfo> FirewallProfiles { get; } = new();
    public ObservableCollection<FirewallService.AdapterFirewallProfileInfo> AdapterFirewallProfiles { get; } = new();
    private bool _isLoadingFirewallPosture;
    public bool IsLoadingFirewallPosture { get => _isLoadingFirewallPosture; private set => SetProperty(ref _isLoadingFirewallPosture, value); }
    private string? _firewallPostureStatus;
    public string? FirewallPostureStatus { get => _firewallPostureStatus; private set => SetProperty(ref _firewallPostureStatus, value); }
    public AsyncRelayCommand RefreshFirewallPostureCommand { get; }

    // #882: firewall rule audit - per-rule Disable (never delete) + a same-session Undo list. A
    // full persistent action journal is #899's job, not this one.
    public ObservableCollection<FirewallService.FirewallRuleInfo> FirewallRules { get; } = new();
    public ObservableCollection<string> DisabledFirewallRuleNames { get; } = new();
    private bool _isScanningFirewallRules;
    public bool IsScanningFirewallRules { get => _isScanningFirewallRules; private set => SetProperty(ref _isScanningFirewallRules, value); }
    private string? _firewallRuleScanStatus;
    public string? FirewallRuleScanStatus { get => _firewallRuleScanStatus; private set => SetProperty(ref _firewallRuleScanStatus, value); }
    public AsyncRelayCommand ScanFirewallRulesCommand { get; }
    public AsyncRelayCommand DisableFirewallRuleCommand { get; }
    public AsyncRelayCommand UndoDisableFirewallRuleCommand { get; }

    // #883: exposed-listener map - "interesting" (bound to all interfaces) vs. every other
    // listener (loopback-only or a specific bound address - common/expected, shown collapsed).
    public ObservableCollection<ExposedListenerInfo> ExposedListeners { get; } = new();
    public ObservableCollection<ExposedListenerInfo> OtherListeners { get; } = new();
    private bool _isScanningExposedListeners;
    public bool IsScanningExposedListeners { get => _isScanningExposedListeners; private set => SetProperty(ref _isScanningExposedListeners, value); }
    private string? _exposedListenerScanStatus;
    public string? ExposedListenerScanStatus { get => _exposedListenerScanStatus; private set => SetProperty(ref _exposedListenerScanStatus, value); }
    public AsyncRelayCommand ScanExposedListenersCommand { get; }

    // #884: SMB/legacy protocol posture - display-only rows, each carrying its own "why this
    // matters" + exact change instructions (see SmbLegacyProtocolService.Row).
    public ObservableCollection<SmbLegacyProtocolService.Row> SmbLegacyRows { get; } = new();
    private bool _isLoadingSmbPosture;
    public bool IsLoadingSmbPosture { get => _isLoadingSmbPosture; private set => SetProperty(ref _isLoadingSmbPosture, value); }
    private string? _smbPostureStatus;
    public string? SmbPostureStatus { get => _smbPostureStatus; private set => SetProperty(ref _smbPostureStatus, value); }
    public AsyncRelayCommand RefreshSmbPostureCommand { get; }

    // #885: share audit.
    public ObservableCollection<ShareAuditService.ShareInfo> Shares { get; } = new();
    private bool _isScanningShares;
    public bool IsScanningShares { get => _isScanningShares; private set => SetProperty(ref _isScanningShares, value); }
    private string? _shareScanStatus;
    public string? ShareScanStatus { get => _shareScanStatus; private set => SetProperty(ref _shareScanStatus, value); }
    public AsyncRelayCommand ScanSharesCommand { get; }

    // #886: remote management exposure - report only, act via the existing Services tab.
    public ObservableCollection<RemoteManagementExposureService.RemoteManagementItem> RemoteManagementItems { get; } = new();
    private bool _isScanningRemoteManagement;
    public bool IsScanningRemoteManagement { get => _isScanningRemoteManagement; private set => SetProperty(ref _isScanningRemoteManagement, value); }
    private string? _remoteManagementScanStatus;
    public string? RemoteManagementScanStatus { get => _remoteManagementScanStatus; private set => SetProperty(ref _remoteManagementScanStatus, value); }
    public AsyncRelayCommand ScanRemoteManagementCommand { get; }

    // #887: hosts file audit - inspection + "Open in Notepad" only, no in-app editing.
    private HostsFileAuditService.HostsFileAuditInfo? _hostsFileAudit;
    public HostsFileAuditService.HostsFileAuditInfo? HostsFileAudit { get => _hostsFileAudit; private set => SetProperty(ref _hostsFileAudit, value); }
    private bool _isScanningHostsFile;
    public bool IsScanningHostsFile { get => _isScanningHostsFile; private set => SetProperty(ref _isScanningHostsFile, value); }
    private string? _hostsFileScanStatus;
    public string? HostsFileScanStatus { get => _hostsFileScanStatus; private set => SetProperty(ref _hostsFileScanStatus, value); }
    public AsyncRelayCommand ScanHostsFileCommand { get; }
    public RelayCommand OpenHostsFileInNotepadCommand { get; }

    // #888: DNS posture - the full card (per-adapter servers, DoH, NRPT) lives on the Network tab;
    // this is just the mirrored finding, computed by independently calling the same static
    // DnsPostureService methods - see DnsPostureService's own remarks on this "shared static
    // service method called from two ViewModels" pattern.
    private bool _isCheckingDnsPostureForFindings;
    public bool IsCheckingDnsPostureForFindings { get => _isCheckingDnsPostureForFindings; private set => SetProperty(ref _isCheckingDnsPostureForFindings, value); }
    private string? _dnsPostureFindingStatus;
    public string? DnsPostureFindingStatus { get => _dnsPostureFindingStatus; private set => SetProperty(ref _dnsPostureFindingStatus, value); }
    public AsyncRelayCommand CheckDnsPostureForFindingsCommand { get; }

    // #889: proxy hijack check + reset.
    private ProxyConfigInfo? _proxyConfig;
    public ProxyConfigInfo? ProxyConfig { get => _proxyConfig; private set => SetProperty(ref _proxyConfig, value); }
    private WinHttpProxyInfo? _machineProxyConfig;
    public WinHttpProxyInfo? MachineProxyConfig { get => _machineProxyConfig; private set => SetProperty(ref _machineProxyConfig, value); }
    private bool _isLoadingProxyPosture;
    public bool IsLoadingProxyPosture { get => _isLoadingProxyPosture; private set => SetProperty(ref _isLoadingProxyPosture, value); }
    private string? _proxyPostureStatus;
    public string? ProxyPostureStatus { get => _proxyPostureStatus; private set => SetProperty(ref _proxyPostureStatus, value); }
    public AsyncRelayCommand RefreshProxyPostureCommand { get; }
    private bool _isResettingProxy;
    public bool IsResettingProxy { get => _isResettingProxy; private set => SetProperty(ref _isResettingProxy, value); }
    private string? _proxyResetOutput;
    public string? ProxyResetOutput { get => _proxyResetOutput; private set => SetProperty(ref _proxyResetOutput, value); }
    public AsyncRelayCommand ResetProxyAndWinsockCommand { get; }

    // #890: certificate store anomalies - inspection only, no removal action from this app.
    public ObservableCollection<CertificateStoreAuditService.CertificateReviewRow> CertificateAnomalies { get; } = new();
    private int _disallowedCertificateCount;
    public int DisallowedCertificateCount { get => _disallowedCertificateCount; private set => SetProperty(ref _disallowedCertificateCount, value); }
    private bool _isScanningCertificates;
    public bool IsScanningCertificates { get => _isScanningCertificates; private set => SetProperty(ref _isScanningCertificates, value); }
    private string? _certificateScanStatus;
    public string? CertificateScanStatus { get => _certificateScanStatus; private set => SetProperty(ref _certificateScanStatus, value); }
    public AsyncRelayCommand ScanCertificateStoreCommand { get; }
    public RelayCommand OpenCertificateManagerCommand { get; }

    /// <summary>#881.</summary>
    private async Task RefreshFirewallPostureAsync()
    {
        IsLoadingFirewallPosture = true;
        FirewallPostureStatus = null;
        try
        {
            var (profiles, adapterProfiles) = await Task.Run(FirewallService.ReadPosture);

            FirewallProfiles.Clear();
            foreach (var p in profiles) FirewallProfiles.Add(p);
            AdapterFirewallProfiles.Clear();
            foreach (var a in adapterProfiles) AdapterFirewallProfiles.Add(a);

            var findings = FirewallService.BuildProfileFindings(profiles);
            foreach (var f in findings) Findings.Add(f);

            FirewallPostureStatus = profiles.Count == 0
                ? "Couldn't read firewall profile posture (WMI unavailable/denied)."
                : $"{profiles.Count} profile(s) read, {adapterProfiles.Count} adapter(s) mapped - {findings.Count} new finding(s).";
        }
        catch (Exception ex)
        {
            FirewallPostureStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoadingFirewallPosture = false;
        }
    }

    /// <summary>#882: enumerates ENABLED inbound ALLOW rules and flags risky shapes - "created
    /// recently" is not computed (netsh's output carries no rule-creation timestamp).</summary>
    private async Task ScanFirewallRulesAsync()
    {
        IsScanningFirewallRules = true;
        FirewallRuleScanStatus = null;
        try
        {
            var rules = await Task.Run(FirewallService.ScanEnabledInboundAllowRules);
            FirewallRules.Clear();
            foreach (var r in rules) FirewallRules.Add(r);

            int riskyCount = rules.Count(r => r.IsRisky);
            FirewallRuleScanStatus = $"{rules.Count} enabled inbound Allow rule(s) - {riskyCount} flagged as a possibly-risky shape. \"Created recently\" isn't available from netsh's output (not implemented - infeasible via this data source). Rule names aren't guaranteed unique - Disable/Undo apply to every rule sharing that exact name.";
        }
        catch (Exception ex)
        {
            FirewallRuleScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningFirewallRules = false;
        }
    }

    private async Task DisableFirewallRuleAsync(FirewallService.FirewallRuleInfo? rule)
    {
        if (rule is null) return;
        var (success, output) = await Task.Run(() => FirewallService.DisableRule(rule.Name));
        if (success && !DisabledFirewallRuleNames.Contains(rule.Name)) DisabledFirewallRuleNames.Add(rule.Name);
        FirewallRuleScanStatus = success ? $"Disabled \"{rule.Name}\"." : $"Couldn't disable \"{rule.Name}\": {output}";
    }

    /// <summary>#882: same-session "Undo" - re-enables a rule this app disabled.</summary>
    private async Task UndoDisableFirewallRuleAsync(string? ruleName)
    {
        if (string.IsNullOrEmpty(ruleName)) return;
        var (success, output) = await Task.Run(() => FirewallService.EnableRule(ruleName));
        if (success) DisabledFirewallRuleNames.Remove(ruleName);
        FirewallRuleScanStatus = success ? $"Re-enabled \"{ruleName}\"." : $"Couldn't re-enable \"{ruleName}\": {output}";
    }

    /// <summary>#883: cross-references live LISTENING sockets against a fresh enabled-inbound-
    /// allow-rule scan and the current firewall profile posture, entirely within this one on-
    /// demand pass.</summary>
    private async Task ScanExposedListenersAsync()
    {
        IsScanningExposedListeners = true;
        ExposedListenerScanStatus = null;
        try
        {
            var listeners = await Task.Run(() =>
            {
                var connections = NetworkConnectionsService.Sample();
                var rules = FirewallService.ScanEnabledInboundAllowRules();
                var (profiles, adapterProfiles) = FirewallService.ReadPosture();

                var activeProfileNames = adapterProfiles.Select(a => a.ProfileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool everyActiveDefaultsToBlock = activeProfileNames.Count > 0 && activeProfileNames.All(name =>
                    profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                       p.DefaultInboundAction.Equals("Block", StringComparison.OrdinalIgnoreCase)));

                return NetworkConnectionsService.BuildExposedListenerMap(connections, rules, everyActiveDefaultsToBlock);
            });

            ExposedListeners.Clear();
            OtherListeners.Clear();
            foreach (var l in listeners)
            {
                if (l.IsInteresting) ExposedListeners.Add(l);
                else OtherListeners.Add(l);
            }

            bool selfShown = listeners.Any(l => l.IsSelf);
            ExposedListenerScanStatus = $"{listeners.Count} listening socket(s) - {ExposedListeners.Count} bound to all interfaces (the interesting ones), {OtherListeners.Count} loopback-only/specific-address (common, shown collapsed below). This is a heuristic cross-reference, not a definitive reachability test."
                + (selfShown ? " Includes Task Manager Plus's own remote-monitor endpoint (shown honestly, not filtered out) since it's currently running." : string.Empty);
        }
        catch (Exception ex)
        {
            ExposedListenerScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningExposedListeners = false;
        }
    }

    /// <summary>#884.</summary>
    private async Task RefreshSmbPostureAsync()
    {
        IsLoadingSmbPosture = true;
        SmbPostureStatus = null;
        try
        {
            var rows = await Task.Run(SmbLegacyProtocolService.ReadRows);
            SmbLegacyRows.Clear();
            foreach (var r in rows) SmbLegacyRows.Add(r);
            SmbPostureStatus = $"{rows.Count} row(s) read - display-only, each lists the exact registry path/command to change it.";
        }
        catch (Exception ex)
        {
            SmbPostureStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoadingSmbPosture = false;
        }
    }

    /// <summary>#885.</summary>
    private async Task ScanSharesAsync()
    {
        IsScanningShares = true;
        ShareScanStatus = null;
        try
        {
            var (shares, findings) = await Task.Run(ShareAuditService.Scan);
            Shares.Clear();
            foreach (var s in shares) Shares.Add(s);
            foreach (var f in findings) Findings.Add(f);

            int userShares = shares.Count(s => !s.IsAdministrative);
            ShareScanStatus = $"{shares.Count} share(s) found ({userShares} user-created, {shares.Count - userShares} administrative) - {findings.Count} new finding(s).";
        }
        catch (Exception ex)
        {
            ShareScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningShares = false;
        }
    }

    /// <summary>#886: report only - act via the existing Services tab.</summary>
    private async Task ScanRemoteManagementAsync()
    {
        IsScanningRemoteManagement = true;
        RemoteManagementScanStatus = null;
        try
        {
            var items = await Task.Run(RemoteManagementExposureService.Scan);
            RemoteManagementItems.Clear();
            foreach (var i in items) RemoteManagementItems.Add(i);
            RemoteManagementScanStatus = $"{items.Count} remote-management surface(s) checked - turn any of these off from the existing Services tab (or System Properties > Remote for Remote Assistance).";
        }
        catch (Exception ex)
        {
            RemoteManagementScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningRemoteManagement = false;
        }
    }

    /// <summary>#887.</summary>
    private async Task ScanHostsFileAsync()
    {
        IsScanningHostsFile = true;
        HostsFileScanStatus = null;
        try
        {
            var info = await Task.Run(HostsFileAuditService.Scan);
            HostsFileAudit = info;
            HostsFileScanStatus = info.FileFound
                ? $"{info.TotalEntries} entries - {info.UpdateOrAvBlocks.Count} possible Windows Update/AV block(s), {info.NonLoopbackEntries.Count} non-loopback entry(ies)."
                  + (info.IsLarge ? " Large ad-block-style hosts file - can slow name resolution (informational, not a problem)." : string.Empty)
                  + (info.Zone.Found ? " The hosts file itself carries a Mark of the Web - worth a look at how it got there." : string.Empty)
                : "hosts file not found or couldn't be read.";
        }
        catch (Exception ex)
        {
            HostsFileScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningHostsFile = false;
        }
    }

    /// <summary>#887: "Open in Notepad" - no direct in-app editing, per this section's own text.</summary>
    private void OpenHostsFileInNotepad()
    {
        try
        {
            string path = HostsFileAudit?.HostsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HostsFileScanStatus = $"Couldn't open Notepad: {ex.Message}";
        }
    }

    /// <summary>#888: the mirrored finding only - see DnsPostureService's remarks.</summary>
    private async Task CheckDnsPostureForFindingsAsync()
    {
        IsCheckingDnsPostureForFindings = true;
        DnsPostureFindingStatus = null;
        try
        {
            var (findings, adapterCount) = await Task.Run(() =>
            {
                var posture = DnsPostureService.ReadPosture();
                return (DnsPostureService.BuildFindings(posture), posture.Adapters.Count);
            });
            foreach (var f in findings) Findings.Add(f);
            DnsPostureFindingStatus = $"{adapterCount} adapter(s) checked - {findings.Count} new finding(s). Full DNS posture detail (per-adapter servers, DoH, NRPT) is on the Network tab.";
        }
        catch (Exception ex)
        {
            DnsPostureFindingStatus = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingDnsPostureForFindings = false;
        }
    }

    /// <summary>#889.</summary>
    private async Task RefreshProxyPostureAsync()
    {
        IsLoadingProxyPosture = true;
        ProxyPostureStatus = null;
        try
        {
            var (perUser, machine, findings) = await Task.Run(() =>
            {
                var u = NetworkDiagnosticsService.ReadProxyConfig();
                var m = NetworkDiagnosticsService.ReadWinHttpProxy();
                var f = NetworkDiagnosticsService.BuildProxyFindings(u);
                return (u, m, f);
            });

            ProxyConfig = perUser;
            MachineProxyConfig = machine;
            foreach (var f in findings) Findings.Add(f);

            ProxyPostureStatus = $"Per-user proxy: {(perUser.Enabled ? "Enabled" : "Disabled")}. Machine WinHTTP proxy: {(machine.DirectAccess ? "Direct (no proxy)" : machine.ProxyServer)}. {findings.Count} new finding(s).";
        }
        catch (Exception ex)
        {
            ProxyPostureStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoadingProxyPosture = false;
        }
    }

    /// <summary>#889: "Reset proxy and Winsock" - genuinely disruptive (winsock reset needs a
    /// reboot to fully take effect), so this is gated behind an explicit confirmation naming that
    /// consequence, matching this app's existing MessageBox.Show(YesNo, Warning) confirm-dialog
    /// convention (see RestoreQuarantineItem/RunOfflineScan above).</summary>
    private async Task ResetProxyAndWinsockAsync()
    {
        if (IsResettingProxy) return;

        var confirm = MessageBox.Show(
            "This runs, in sequence: netsh winhttp reset proxy, netsh winsock reset, and ipconfig /flushdns.\n\nWinsock reset REQUIRES A REBOOT to fully take effect - network connectivity can behave oddly until you restart. Save any open work before continuing.\n\nContinue?",
            "Reset proxy and Winsock - this requires a reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsResettingProxy = true;
        ProxyResetOutput = null;
        try
        {
            ProxyResetOutput = await NetworkDiagnosticsService.ResetProxyAndWinsockAsync();
            ProxyPostureStatus = "Reset complete - REBOOT to finish applying the Winsock reset.";
        }
        catch (Exception ex)
        {
            ProxyPostureStatus = $"Reset failed: {ex.Message}";
        }
        finally
        {
            IsResettingProxy = false;
        }
    }

    /// <summary>#890: inspection only - no removal action from this app.</summary>
    private async Task ScanCertificateStoreAsync()
    {
        IsScanningCertificates = true;
        CertificateScanStatus = null;
        try
        {
            var (rows, disallowedCount, findings) = await Task.Run(CertificateStoreAuditService.Scan);
            CertificateAnomalies.Clear();
            foreach (var r in rows) CertificateAnomalies.Add(r);
            DisallowedCertificateCount = disallowedCount;
            foreach (var f in findings) Findings.Add(f);

            CertificateScanStatus = $"{rows.Count} certificate(s) flagged for review across LocalMachine/CurrentUser Root+CA stores ({disallowedCount} already in the Disallowed store, reported as a count only - Windows itself already flagged those). \"Installed recently\" isn't determinable via X509Certificate2 (not implemented - see class remarks).";
        }
        catch (Exception ex)
        {
            CertificateScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningCertificates = false;
        }
    }

    /// <summary>#890: launches certlm.msc for the user to act themselves - this app makes no
    /// certificate-store changes.</summary>
    private void OpenCertificateManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("certlm.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CertificateScanStatus = $"Couldn't open Certificate Manager: {ex.Message}";
        }
    }
}
