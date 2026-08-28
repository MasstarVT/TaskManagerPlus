using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Windows Health tab (#769-800). Modeled directly on StabilityViewModel: queried on
/// demand (an initial load plus a manual Refresh command), no DispatcherTimer - event-log scans,
/// registry sweeps and (for the heavier on-demand actions) DISM/log-parsing calls aren't cheap
/// enough to repeat on a tick, the same tradeoff every other on-demand tab in this app already
/// makes.
///
/// This first chunk (#769-775) covers the tab's "Update history" section: real WU-client install/
/// download/failure history (#769) correlated against the Setup servicing channel (#771) with a
/// shared offline error catalog (#772), plus the pending-reboot detail panel (#774) and the update
/// pause/defer/policy audit (#775) as the tab's top cards, and two heavier on-demand actions (#770
/// servicing package table, #773 feature-update failure log analysis) gated behind their own
/// buttons rather than loaded up front.
/// </summary>
public sealed class WindowsHealthViewModel : ObservableObject
{
    #region #769/#771/#772 - Update history

    public ObservableCollection<WindowsUpdateEvent> UpdateEvents { get; } = new();
    public ObservableCollection<ServicingChannelEvent> ServicingEvents { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    /// <summary>Set when RefreshAsync fails outright - mirrors StabilityViewModel.RefreshErrorText's
    /// "...failed: {message}" convention rather than letting the exception propagate uncaught out
    /// of an async void command handler.</summary>
    private string? _refreshErrorText;
    public string? RefreshErrorText { get => _refreshErrorText; private set => SetProperty(ref _refreshErrorText, value); }

    public AsyncRelayCommand RefreshCommand { get; }

    #endregion

    #region #774 - Pending reboot detail panel

    public ObservableCollection<RebootPendingIndicator> RebootPendingIndicators { get; } = new();

    public bool IsRebootPending => RebootPendingIndicators.Any(i => i.IsActive);

    #endregion

    #region #775 - Update pause, defer and policy audit

    private UpdatePolicySnapshot _updatePolicy = new();
    public UpdatePolicySnapshot UpdatePolicy { get => _updatePolicy; private set => SetProperty(ref _updatePolicy, value); }

    public AsyncRelayCommand ResumeUpdatesCommand { get; }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    #endregion

    #region #770 - Servicing package state table

    public ObservableCollection<ServicingPackageInfo> ServicingPackages { get; } = new();

    private bool _isLoadingPackages;
    public bool IsLoadingPackages { get => _isLoadingPackages; private set => SetProperty(ref _isLoadingPackages, value); }

    private bool _hasLoadedPackages;
    public bool HasLoadedPackages { get => _hasLoadedPackages; private set => SetProperty(ref _hasLoadedPackages, value); }

    public AsyncRelayCommand LoadServicingPackagesCommand { get; }

    #endregion

    #region #773 - Feature-update failure analysis

    private FeatureUpdateFailureInfo? _featureUpdateFailure;
    public FeatureUpdateFailureInfo? FeatureUpdateFailure { get => _featureUpdateFailure; private set => SetProperty(ref _featureUpdateFailure, value); }

    private bool _isLoadingFeatureUpdateFailure;
    public bool IsLoadingFeatureUpdateFailure { get => _isLoadingFeatureUpdateFailure; private set => SetProperty(ref _isLoadingFeatureUpdateFailure, value); }

    private bool _isRunningSetupDiag;
    public bool IsRunningSetupDiag { get => _isRunningSetupDiag; private set => SetProperty(ref _isRunningSetupDiag, value); }

    public AsyncRelayCommand CheckFeatureUpdateFailureCommand { get; }
    public AsyncRelayCommand RunSetupDiagCommand { get; }

    #endregion

    #region #776 - WSUS/WUfB reachability check

    private WuServerReachabilityResult? _wuServerReachability;
    public WuServerReachabilityResult? WuServerReachability { get => _wuServerReachability; private set => SetProperty(ref _wuServerReachability, value); }

    private bool _isCheckingWuServerReachability;
    public bool IsCheckingWuServerReachability { get => _isCheckingWuServerReachability; private set => SetProperty(ref _isCheckingWuServerReachability, value); }

    public AsyncRelayCommand CheckWuServerReachabilityCommand { get; }

    #endregion

    #region #777 - Update service-stack health check

    public ObservableCollection<UpdateServiceHealthEntry> UpdateServiceStackHealth { get; } = new();

    private bool _isLoadingUpdateServiceStackHealth;
    public bool IsLoadingUpdateServiceStackHealth { get => _isLoadingUpdateServiceStackHealth; private set => SetProperty(ref _isLoadingUpdateServiceStackHealth, value); }

    private bool _hasLoadedUpdateServiceStackHealth;
    public bool HasLoadedUpdateServiceStackHealth { get => _hasLoadedUpdateServiceStackHealth; private set => SetProperty(ref _hasLoadedUpdateServiceStackHealth, value); }

    public bool HasFlaggedUpdateServices => UpdateServiceStackHealth.Any(s => s.IsFlagged);

    public AsyncRelayCommand LoadUpdateServiceStackHealthCommand { get; }
    public AsyncRelayCommand RestoreUpdateServiceStackDefaultsCommand { get; }

    #endregion

    #region #778 - Guided "reset Windows Update components"

    public ObservableCollection<string> ResetWuComponentsLog { get; } = new();

    private bool _isResettingWuComponents;
    public bool IsResettingWuComponents { get => _isResettingWuComponents; private set => SetProperty(ref _isResettingWuComponents, value); }

    public AsyncRelayCommand ResetWindowsUpdateComponentsCommand { get; }

    #endregion

    #region #779 - Update cache reclaim

    private UpdateCacheInfo? _updateCacheInfo;
    public UpdateCacheInfo? UpdateCacheInfo { get => _updateCacheInfo; private set => SetProperty(ref _updateCacheInfo, value); }

    private bool _isLoadingUpdateCacheInfo;
    public bool IsLoadingUpdateCacheInfo { get => _isLoadingUpdateCacheInfo; private set => SetProperty(ref _isLoadingUpdateCacheInfo, value); }

    public ObservableCollection<string> ClearUpdateCacheLog { get; } = new();

    private bool _isClearingUpdateCache;
    public bool IsClearingUpdateCache { get => _isClearingUpdateCache; private set => SetProperty(ref _isClearingUpdateCache, value); }

    public AsyncRelayCommand CheckUpdateCacheSizeCommand { get; }
    public AsyncRelayCommand ClearUpdateCacheCommand { get; }

    #endregion

    #region #780 - Uninstall a specific update

    public ObservableCollection<RemovableUpdateInfo> RemovableUpdates { get; } = new();

    private bool _isLoadingRemovableUpdates;
    public bool IsLoadingRemovableUpdates { get => _isLoadingRemovableUpdates; private set => SetProperty(ref _isLoadingRemovableUpdates, value); }

    private bool _hasLoadedRemovableUpdates;
    public bool HasLoadedRemovableUpdates { get => _hasLoadedRemovableUpdates; private set => SetProperty(ref _hasLoadedRemovableUpdates, value); }

    private bool _isUninstallingUpdate;
    public bool IsUninstallingUpdate { get => _isUninstallingUpdate; private set => SetProperty(ref _isUninstallingUpdate, value); }

    public AsyncRelayCommand LoadRemovableUpdatesCommand { get; }
    public AsyncRelayCommand UninstallUpdateCommand { get; }

    #endregion

    #region #782 - Component store analysis

    private ComponentStoreAnalysis? _componentStoreAnalysis;
    public ComponentStoreAnalysis? ComponentStoreAnalysis { get => _componentStoreAnalysis; private set => SetProperty(ref _componentStoreAnalysis, value); }

    private bool _isAnalyzingComponentStore;
    public bool IsAnalyzingComponentStore { get => _isAnalyzingComponentStore; private set => SetProperty(ref _isAnalyzingComponentStore, value); }

    private bool _showComponentStoreRawOutput;
    public bool ShowComponentStoreRawOutput { get => _showComponentStoreRawOutput; set => SetProperty(ref _showComponentStoreRawOutput, value); }

    public AsyncRelayCommand AnalyzeComponentStoreCommand { get; }
    public RelayCommand ToggleComponentStoreRawOutputCommand { get; }

    #endregion

    #region #783 - Component store cleanup runner (plain + /ResetBase)

    private int _cleanupProgressPercent;
    public int CleanupProgressPercent { get => _cleanupProgressPercent; private set => SetProperty(ref _cleanupProgressPercent, value); }

    private bool _isCleaningComponentStore;
    public bool IsCleaningComponentStore { get => _isCleaningComponentStore; private set => SetProperty(ref _isCleaningComponentStore, value); }

    private bool _isResetBaseRunning;
    public bool IsResetBaseRunning { get => _isResetBaseRunning; private set => SetProperty(ref _isResetBaseRunning, value); }

    private string? _cleanupResultText;
    public string? CleanupResultText { get => _cleanupResultText; private set => SetProperty(ref _cleanupResultText, value); }

    private CancellationTokenSource? _cleanupCts;

    public AsyncRelayCommand RunComponentCleanupCommand { get; }
    public AsyncRelayCommand RunComponentCleanupResetBaseCommand { get; }
    public RelayCommand CancelComponentCleanupCommand { get; }

    #endregion

    #region #784 - DISM health scans (CheckHealth / ScanHealth / RestoreHealth)

    private DismHealthScanResult? _checkHealthResult;
    public DismHealthScanResult? CheckHealthResult { get => _checkHealthResult; private set => SetProperty(ref _checkHealthResult, value); }
    private bool _isCheckingHealth;
    public bool IsCheckingHealth { get => _isCheckingHealth; private set => SetProperty(ref _isCheckingHealth, value); }
    public AsyncRelayCommand RunCheckHealthCommand { get; }

    private DismHealthScanResult? _scanHealthResult;
    public DismHealthScanResult? ScanHealthResult { get => _scanHealthResult; private set => SetProperty(ref _scanHealthResult, value); }
    private bool _isScanningHealth;
    public bool IsScanningHealth { get => _isScanningHealth; private set => SetProperty(ref _isScanningHealth, value); }
    private int _scanHealthProgressPercent;
    public int ScanHealthProgressPercent { get => _scanHealthProgressPercent; private set => SetProperty(ref _scanHealthProgressPercent, value); }
    private CancellationTokenSource? _scanHealthCts;
    public AsyncRelayCommand RunScanHealthCommand { get; }
    public RelayCommand CancelScanHealthCommand { get; }

    private DismHealthScanResult? _restoreHealthResult;
    public DismHealthScanResult? RestoreHealthResult { get => _restoreHealthResult; private set => SetProperty(ref _restoreHealthResult, value); }
    private bool _isRestoringHealth;
    public bool IsRestoringHealth { get => _isRestoringHealth; private set => SetProperty(ref _isRestoringHealth, value); }
    private int _restoreHealthProgressPercent;
    public int RestoreHealthProgressPercent { get => _restoreHealthProgressPercent; private set => SetProperty(ref _restoreHealthProgressPercent, value); }
    private CancellationTokenSource? _restoreHealthCts;
    public AsyncRelayCommand RunRestoreHealthCommand { get; }
    public RelayCommand CancelRestoreHealthCommand { get; }

    #endregion

    #region #785 - RestoreHealth source picker (Get-WimInfo)

    public ObservableCollection<WimImageInfo> RepairSourceImages { get; } = new();

    private string? _repairSourceWimPath;
    public string? RepairSourceWimPath { get => _repairSourceWimPath; private set => SetProperty(ref _repairSourceWimPath, value); }

    private bool _isLoadingRepairSourceImages;
    public bool IsLoadingRepairSourceImages { get => _isLoadingRepairSourceImages; private set => SetProperty(ref _isLoadingRepairSourceImages, value); }

    private WimImageInfo? _selectedRepairSourceImage;
    public WimImageInfo? SelectedRepairSourceImage
    {
        get => _selectedRepairSourceImage;
        set { if (SetProperty(ref _selectedRepairSourceImage, value)) RetryRestoreHealthWithSourceCommand.RaiseCanExecuteChanged(); }
    }

    public AsyncRelayCommand BrowseRepairSourceCommand { get; }
    public AsyncRelayCommand RetryRestoreHealthWithSourceCommand { get; }

    #endregion

    #region #786 - sfc /scannow

    private SfcScanResult? _sfcScanResult;
    public SfcScanResult? SfcScanResult { get => _sfcScanResult; private set => SetProperty(ref _sfcScanResult, value); }

    private bool _isRunningSfcScan;
    public bool IsRunningSfcScan { get => _isRunningSfcScan; private set => SetProperty(ref _isRunningSfcScan, value); }

    private int _sfcProgressPercent;
    public int SfcProgressPercent { get => _sfcProgressPercent; private set => SetProperty(ref _sfcProgressPercent, value); }

    private CancellationTokenSource? _sfcCts;

    public AsyncRelayCommand RunSfcScanCommand { get; }
    public RelayCommand CancelSfcScanCommand { get; }

    #endregion

    #region #787 - Integrity scan history

    public ObservableCollection<IntegrityHistoryEntry> IntegrityHistory { get; } = new();

    private string? _sfcComparisonText;
    public string? SfcComparisonText { get => _sfcComparisonText; private set => SetProperty(ref _sfcComparisonText, value); }

    #endregion

    #region #788 - In-place repair-install guidance

    private RepairInstallGuidance? _repairInstallGuidance;
    public RepairInstallGuidance? RepairInstallGuidance { get => _repairInstallGuidance; private set => SetProperty(ref _repairInstallGuidance, value); }

    #endregion

    #region #789 - Restore point inventory (Recovery section)

    private SystemRestoreSnapshot? _systemRestoreSnapshot;
    public SystemRestoreSnapshot? SystemRestoreSnapshot { get => _systemRestoreSnapshot; private set => SetProperty(ref _systemRestoreSnapshot, value); }

    private bool _isLoadingSystemRestore;
    public bool IsLoadingSystemRestore { get => _isLoadingSystemRestore; private set => SetProperty(ref _isLoadingSystemRestore, value); }

    public AsyncRelayCommand LoadSystemRestoreCommand { get; }

    #endregion

    #region #790 - Create restore point / rstrui launcher

    private bool _isCreatingRestorePoint;
    public bool IsCreatingRestorePoint { get => _isCreatingRestorePoint; private set => SetProperty(ref _isCreatingRestorePoint, value); }

    public AsyncRelayCommand CreateRestorePointCommand { get; }
    public RelayCommand LaunchRstruiCommand { get; }

    #endregion

    public WindowsHealthViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ResumeUpdatesCommand = new AsyncRelayCommand(ResumeUpdatesAsync, () => UpdatePolicy.IsPausedNow);
        LoadServicingPackagesCommand = new AsyncRelayCommand(LoadServicingPackagesAsync);
        CheckFeatureUpdateFailureCommand = new AsyncRelayCommand(CheckFeatureUpdateFailureAsync);
        RunSetupDiagCommand = new AsyncRelayCommand(RunSetupDiagAsync, () => FeatureUpdateFailure?.SetupDiagAvailable ?? false);
        CheckWuServerReachabilityCommand = new AsyncRelayCommand(CheckWuServerReachabilityAsync, () => !string.IsNullOrEmpty(UpdatePolicy.WuServer));
        LoadUpdateServiceStackHealthCommand = new AsyncRelayCommand(LoadUpdateServiceStackHealthAsync);
        RestoreUpdateServiceStackDefaultsCommand = new AsyncRelayCommand(RestoreUpdateServiceStackDefaultsAsync, () => HasFlaggedUpdateServices);
        ResetWindowsUpdateComponentsCommand = new AsyncRelayCommand(ResetWindowsUpdateComponentsAsync);
        CheckUpdateCacheSizeCommand = new AsyncRelayCommand(CheckUpdateCacheSizeAsync);
        ClearUpdateCacheCommand = new AsyncRelayCommand(ClearUpdateCacheAsync, () => UpdateCacheInfo is { } c && (c.DownloadCacheSizeBytes > 0 || c.DeliveryOptimizationCacheSizeBytes > 0));
        LoadRemovableUpdatesCommand = new AsyncRelayCommand(LoadRemovableUpdatesAsync);
        UninstallUpdateCommand = new AsyncRelayCommand(UninstallUpdateAsync);

        AnalyzeComponentStoreCommand = new AsyncRelayCommand(AnalyzeComponentStoreAsync, () => !IsAnalyzingComponentStore);
        ToggleComponentStoreRawOutputCommand = new RelayCommand(() => ShowComponentStoreRawOutput = !ShowComponentStoreRawOutput);

        RunComponentCleanupCommand = new AsyncRelayCommand(() => RunComponentCleanupAsync(resetBase: false), () => !IsCleaningComponentStore && !IsResetBaseRunning);
        RunComponentCleanupResetBaseCommand = new AsyncRelayCommand(() => RunComponentCleanupAsync(resetBase: true), () => !IsCleaningComponentStore && !IsResetBaseRunning);
        CancelComponentCleanupCommand = new RelayCommand(() => _cleanupCts?.Cancel(), () => IsCleaningComponentStore || IsResetBaseRunning);

        RunCheckHealthCommand = new AsyncRelayCommand(RunCheckHealthAsync, () => !IsCheckingHealth);
        RunScanHealthCommand = new AsyncRelayCommand(RunScanHealthAsync, () => !IsScanningHealth);
        CancelScanHealthCommand = new RelayCommand(() => _scanHealthCts?.Cancel(), () => IsScanningHealth);
        RunRestoreHealthCommand = new AsyncRelayCommand(() => RunRestoreHealthAsync(null), () => !IsRestoringHealth);
        CancelRestoreHealthCommand = new RelayCommand(() => _restoreHealthCts?.Cancel(), () => IsRestoringHealth);

        BrowseRepairSourceCommand = new AsyncRelayCommand(BrowseRepairSourceAsync, () => !IsLoadingRepairSourceImages);
        RetryRestoreHealthWithSourceCommand = new AsyncRelayCommand(RetryRestoreHealthWithSourceAsync, () => SelectedRepairSourceImage is not null && !IsRestoringHealth);

        RunSfcScanCommand = new AsyncRelayCommand(RunSfcScanAsync, () => !IsRunningSfcScan);
        CancelSfcScanCommand = new RelayCommand(() => _sfcCts?.Cancel(), () => IsRunningSfcScan);

        LoadSystemRestoreCommand = new AsyncRelayCommand(LoadSystemRestoreAsync);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsCreatingRestorePoint);
        LaunchRstruiCommand = new RelayCommand(SystemRestoreService.LaunchRstrui);

        // #787: integrity-history.json is a small local file (same cost as ThemeService's theme.json
        // or PollIntervalSettingsService's own JSON) - cheap enough to load up front, unlike every
        // other action on this tab, so the timeline and #788's repair-install guidance already
        // reflect prior sessions' scans the moment the tab opens rather than staying empty until the
        // user runs a brand new scan.
        var priorHistory = SfcIntegrityService.LoadHistory();
        foreach (var h in priorHistory.OrderByDescending(h => h.Timestamp)) IntegrityHistory.Add(h);
        SfcComparisonText = SfcIntegrityService.CompareToPreviousRun(priorHistory, "SFC");
        UpdateRepairInstallGuidance();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var wuEvents = await Task.Run(WindowsUpdateHistoryService.ReadUpdateClientHistory);
            var setupEvents = await Task.Run(WindowsUpdateHistoryService.ReadSetupChannelEvents);
            // #771: mutates wuEvents in place with the nearest Setup-channel failure - no new query.
            WindowsUpdateHistoryService.Correlate(wuEvents, setupEvents);

            // #772: resolve a plain-language description for every distinct error code seen across
            // both lists in one pass, rather than one certutil shell-out per event row (a machine
            // with a long run of the same recurring failure would otherwise repeat the same lookup
            // dozens of times).
            var distinctCodes = wuEvents.Select(e => e.ErrorCode)
                .Concat(setupEvents.Select(e => e.ErrorCode))
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in distinctCodes)
                descriptions[code!] = await WindowsUpdateErrorCatalog.DescribeAsync(code);
            foreach (var e in wuEvents)
                if (e.ErrorCode is { } code && descriptions.TryGetValue(code, out var desc)) e.ErrorDescription = desc;

            var rebootIndicators = await Task.Run(WindowsUpdatePolicyService.ReadRebootPendingDetail);
            var policy = await Task.Run(WindowsUpdatePolicyService.ReadPolicySnapshot);

            Apply(wuEvents, setupEvents, rebootIndicators, policy);
            RefreshErrorText = null;
        }
        catch (Exception ex)
        {
            RefreshErrorText = $"Couldn't refresh Windows Health data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(List<WindowsUpdateEvent> wuEvents, List<ServicingChannelEvent> setupEvents,
        List<RebootPendingIndicator> rebootIndicators, UpdatePolicySnapshot policy)
    {
        UpdateEvents.Clear();
        foreach (var e in wuEvents) UpdateEvents.Add(e);

        ServicingEvents.Clear();
        foreach (var e in setupEvents) ServicingEvents.Add(e);

        RebootPendingIndicators.Clear();
        foreach (var i in rebootIndicators) RebootPendingIndicators.Add(i);
        OnPropertyChanged(nameof(IsRebootPending));

        UpdatePolicy = policy;
        ResumeUpdatesCommand.RaiseCanExecuteChanged();
        // #776: a fresh refresh means any earlier reachability check is against a possibly-stale
        // WUServer value - clear it rather than leave a check result on screen that no longer
        // matches UpdatePolicy.WuServer.
        WuServerReachability = null;
        CheckWuServerReachabilityCommand.RaiseCanExecuteChanged();
    }

    /// <summary>#775: clears the pause-updates registry values - confirmed first, matching
    /// CLAUDE.md's "mutating actions require confirmation with the exact command/change shown"
    /// convention every other mutating action in this app follows.</summary>
    private async Task ResumeUpdatesAsync()
    {
        var confirm = MessageBox.Show(
            "This clears the pause-updates values under:\n\nHKLM\\SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings\n\n(PauseUpdatesExpiryTime, PauseFeatureUpdatesStartTime/EndTime, PauseQualityUpdatesStartTime/EndTime) - the same values Settings' own \"Resume updates\" button clears. Resume updates now?",
            "Resume updates", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(WindowsUpdatePolicyService.ResumeUpdates);
        if (success)
        {
            StatusMessage = "Updates resumed.";
            UpdatePolicy = await Task.Run(WindowsUpdatePolicyService.ReadPolicySnapshot);
            ResumeUpdatesCommand.RaiseCanExecuteChanged();
        }
        else
        {
            StatusMessage = $"Couldn't resume updates: {error}";
        }
    }

    /// <summary>#770: `dism /online /get-packages` - a full package-store enumeration that can take
    /// several seconds, gated behind its own button rather than loaded with the rest of the tab.</summary>
    private async Task LoadServicingPackagesAsync()
    {
        IsLoadingPackages = true;
        try
        {
            var packages = await WindowsServicingService.ListPackagesAsync();
            ServicingPackages.Clear();
            foreach (var p in packages) ServicingPackages.Add(p);
            HasLoadedPackages = true;
        }
        finally
        {
            IsLoadingPackages = false;
        }
    }

    /// <summary>#773: parses setuperr.log/setupact.log for the failing phase/rollback reason -
    /// gated behind its own button, same reasoning as #770's package table.</summary>
    private async Task CheckFeatureUpdateFailureAsync()
    {
        IsLoadingFeatureUpdateFailure = true;
        try
        {
            FeatureUpdateFailure = await Task.Run(WindowsServicingService.AnalyzeFeatureUpdateFailure);
            RunSetupDiagCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsLoadingFeatureUpdateFailure = false;
        }
    }

    /// <summary>#773: runs SetupDiag.exe (only offered once CheckFeatureUpdateFailureAsync found it
    /// present on the machine) and shows its own verdict instead of this app's regex-based parse.</summary>
    private async Task RunSetupDiagAsync()
    {
        IsRunningSetupDiag = true;
        try
        {
            var (success, verdict, error) = await WindowsServicingService.RunSetupDiagAsync();
            if (success && verdict is not null)
            {
                FeatureUpdateFailure = FeatureUpdateFailure is { } current
                    ? new FeatureUpdateFailureInfo
                    {
                        LogsFound = current.LogsFound,
                        SourceLogPath = current.SourceLogPath,
                        FailingPhase = current.FailingPhase,
                        RollbackReason = current.RollbackReason,
                        MoupgErrorLines = current.MoupgErrorLines,
                        SetupDiagAvailable = current.SetupDiagAvailable,
                        SetupDiagVerdict = verdict,
                    }
                    : new FeatureUpdateFailureInfo { LogsFound = true, SetupDiagAvailable = true, SetupDiagVerdict = verdict };
                StatusMessage = "SetupDiag finished.";
            }
            else
            {
                StatusMessage = $"Couldn't run SetupDiag: {error}";
            }
        }
        finally
        {
            IsRunningSetupDiag = false;
        }
    }

    /// <summary>#776: on-demand HTTP reachability check against the WUServer policy URL - only
    /// offered when one is actually configured (see UpdatePolicy.WuServer / the command's
    /// CanExecute). A network call, so it's a button, never something Refresh runs automatically.</summary>
    private async Task CheckWuServerReachabilityAsync()
    {
        if (string.IsNullOrEmpty(UpdatePolicy.WuServer)) return;
        IsCheckingWuServerReachability = true;
        try
        {
            WuServerReachability = await WindowsUpdatePolicyService.CheckWuServerReachabilityAsync(UpdatePolicy.WuServer);
        }
        finally
        {
            IsCheckingWuServerReachability = false;
        }
    }

    /// <summary>#777: start type/state for every service Windows Update itself depends on - eight
    /// targeted per-name reads, cheap enough to run alongside the rest of the tab, but still gated
    /// behind its own button (not folded into RefreshAsync) so a first paint of the tab isn't
    /// blocked on it.</summary>
    private async Task LoadUpdateServiceStackHealthAsync()
    {
        IsLoadingUpdateServiceStackHealth = true;
        try
        {
            var entries = await Task.Run(ServiceControlService.ReadUpdateServiceStackHealth);
            UpdateServiceStackHealth.Clear();
            foreach (var e in entries) UpdateServiceStackHealth.Add(e);
            HasLoadedUpdateServiceStackHealth = true;
            OnPropertyChanged(nameof(HasFlaggedUpdateServices));
            RestoreUpdateServiceStackDefaultsCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsLoadingUpdateServiceStackHealth = false;
        }
    }

    /// <summary>#777: confirmed, with the exact effect (every service name + the `sc config`
    /// mechanism) shown before it runs, matching CLAUDE.md's mutating-action convention. Re-reads
    /// the card afterward so the flagged rows reflect the actual result rather than an assumed one.</summary>
    private async Task RestoreUpdateServiceStackDefaultsAsync()
    {
        var flagged = UpdateServiceStackHealth.Where(s => s.IsFlagged).Select(s => s.DisplayName).ToList();
        string flaggedText = flagged.Count > 0 ? string.Join(", ", flagged) : "(none currently flagged)";

        var confirm = MessageBox.Show(
            "This runs `sc config <name> start= <type>` for wuauserv, BITS, CryptSvc, msiserver, TrustedInstaller, UsoSvc, WaaSMedicSvc and DoSvc, restoring each to Windows' own documented default start type.\n\n" +
            $"Currently flagged as Disabled: {flaggedText}\n\nRestore defaults now?",
            "Restore update service defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await ServiceControlService.RestoreUpdateServiceStackDefaultsAsync();
        StatusMessage = success
            ? "Update service start types restored to Windows defaults."
            : $"Couldn't restore some update services: {error}";

        await LoadUpdateServiceStackHealthAsync();
    }

    /// <summary>#778: guided, confirmed, step-logged - never automatic (CLAUDE.md's "guided, never
    /// automatic" rule names this exact feature). The confirmation dialog states the exact sequence
    /// before anything runs; ResetWuComponentsLog then carries exactly what happened, including the
    /// undo step for each rename.</summary>
    private async Task ResetWindowsUpdateComponentsAsync()
    {
        var confirm = MessageBox.Show(
            "This runs the documented Windows Update component-reset repair sequence:\n\n" +
            "1. Stop wuauserv, CryptSvc, BITS, msiserver\n" +
            "2. Rename C:\\Windows\\SoftwareDistribution to SoftwareDistribution.bak\n" +
            "3. Rename C:\\Windows\\System32\\catroot2 to catroot2.bak\n" +
            "4. Restart wuauserv, CryptSvc, BITS, msiserver\n\n" +
            "Windows recreates both folders automatically the next time it checks for updates. Each rename can be undone afterwards by renaming the .bak folder back over the original. Run this now?",
            "Reset Windows Update components", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsResettingWuComponents = true;
        try
        {
            var log = await Task.Run(WindowsServicingService.ResetWindowsUpdateComponents);
            ResetWuComponentsLog.Clear();
            foreach (var line in log) ResetWuComponentsLog.Add(line);
            StatusMessage = "Windows Update component reset finished - see the log below.";
        }
        finally
        {
            IsResettingWuComponents = false;
        }
    }

    /// <summary>#779: measures both cache folders on demand (a recursive directory walk).</summary>
    private async Task CheckUpdateCacheSizeAsync()
    {
        IsLoadingUpdateCacheInfo = true;
        try
        {
            UpdateCacheInfo = await Task.Run(WindowsServicingService.ReadUpdateCacheSizes);
            ClearUpdateCacheCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsLoadingUpdateCacheInfo = false;
        }
    }

    /// <summary>#779: confirmed, with the exact folders and the services that get stopped/restarted
    /// shown before it runs. Re-measures afterward so the reported sizes reflect the actual result.</summary>
    private async Task ClearUpdateCacheAsync()
    {
        if (UpdateCacheInfo is not { } info) return;

        bool clearDownload = info.DownloadCacheSizeBytes > 0;
        bool clearDo = info.DeliveryOptimizationCacheSizeBytes > 0;
        var lines = new List<string>();
        if (clearDownload) lines.Add($"- Download cache ({Formatting.FormatBytes(info.DownloadCacheSizeBytes)}) at \"{info.DownloadCachePath}\" - stops/restarts wuauserv");
        if (clearDo) lines.Add($"- Delivery Optimization cache ({Formatting.FormatBytes(info.DeliveryOptimizationCacheSizeBytes)}) at \"{info.DeliveryOptimizationCachePath}\" - stops/restarts DoSvc");

        var confirm = MessageBox.Show(
            "This stops the owning service, deletes everything inside, then restarts it:\n\n" +
            string.Join("\n", lines) +
            "\n\nBoth will simply be rebuilt over time as updates are downloaded again. Clear now?",
            "Clear update caches", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsClearingUpdateCache = true;
        try
        {
            var log = await WindowsServicingService.ClearUpdateCacheAsync(clearDownload, clearDo);
            ClearUpdateCacheLog.Clear();
            foreach (var line in log) ClearUpdateCacheLog.Add(line);
            StatusMessage = "Update cache clear finished - see the log below.";

            UpdateCacheInfo = await Task.Run(WindowsServicingService.ReadUpdateCacheSizes);
            ClearUpdateCacheCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsClearingUpdateCache = false;
        }
    }

    /// <summary>#780: combines QFE hotfixes and DISM "Installed" packages into one removable-updates
    /// list - a full `dism /online /get-packages` sweep (no /format:table, so Install Time survives)
    /// can take a while, so this is gated behind its own button like #770's package table.</summary>
    private async Task LoadRemovableUpdatesAsync()
    {
        IsLoadingRemovableUpdates = true;
        try
        {
            var updates = await WindowsUpdateUninstallService.ListRemovableUpdatesAsync();
            RemovableUpdates.Clear();
            foreach (var u in updates) RemovableUpdates.Add(u);
            HasLoadedRemovableUpdates = true;
        }
        finally
        {
            IsLoadingRemovableUpdates = false;
        }
    }

    /// <summary>#780: confirmed with the exact command shown before it runs; reports a reboot-
    /// required result clearly rather than as a failure (wusa/dism both use exit code 3010 for
    /// "succeeded, needs a reboot").</summary>
    private async Task UninstallUpdateAsync(object? parameter)
    {
        if (parameter is not RemovableUpdateInfo update) return;

        string command = update.IsDismPackage
            ? $"dism /online /remove-package /packagename:{update.Identifier} /norestart"
            : $"wusa /uninstall /kb:{update.Identifier} /quiet /norestart";

        var confirm = MessageBox.Show(
            $"This removes \"{update.DisplayName}\" by running:\n\n{command}\n\n" +
            "A restart may be required afterwards to finish removing it. Uninstall now?",
            "Uninstall update", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsUninstallingUpdate = true;
        try
        {
            var (success, rebootRequired, output) = await WindowsUpdateUninstallService.UninstallAsync(update);
            StatusMessage = success
                ? rebootRequired
                    ? $"\"{update.DisplayName}\" was removed - a restart is required to finish removing it."
                    : $"\"{update.DisplayName}\" was removed."
                : $"Couldn't uninstall \"{update.DisplayName}\": {output}";

            if (success)
            {
                await LoadRemovableUpdatesAsync();
            }
        }
        finally
        {
            IsUninstallingUpdate = false;
        }
    }

    /// <summary>#782: `dism /online /cleanup-image /analyzecomponentstore` - read-only, so no
    /// confirmation, matching CheckHealth/LoadServicingPackages' own "read-only diagnostic, just a
    /// button" treatment.</summary>
    private async Task AnalyzeComponentStoreAsync()
    {
        IsAnalyzingComponentStore = true;
        try
        {
            ComponentStoreAnalysis = await DismService.AnalyzeComponentStoreAsync();
        }
        finally
        {
            IsAnalyzingComponentStore = false;
        }
    }

    /// <summary>#783: plain `/StartComponentCleanup` (reversible) or `/StartComponentCleanup
    /// /ResetBase` (permanent) - both confirmed first since both delete files from the component
    /// store, but with deliberately different wording/icon: ResetBase's dialog spells out the exact,
    /// irreversible effect (can never uninstall a currently installed update/driver afterward) and
    /// - per #790's cross-reference - points at "Create a restore point" in the Recovery section
    /// below as a suggested pre-step (a restore point doesn't undo ResetBase itself, but is still
    /// worth having before any repair session). Progress streamed into CleanupProgressPercent;
    /// cancellable via CancelComponentCleanupCommand/_cleanupCts.</summary>
    private async Task RunComponentCleanupAsync(bool resetBase)
    {
        string message = resetBase
            ? "This runs:\n\n  dism /online /cleanup-image /startcomponentcleanup /resetbase\n\n" +
              "PERMANENT: afterward you can no longer uninstall ANY Windows update or driver that's " +
              "currently installed - the update itself stays installed and working, but its prior " +
              "version is gone from the component store for good. This cannot be undone.\n\n" +
              "Consider creating a restore point first (see \"Create a restore point\" in the Recovery " +
              "section below) before continuing - it won't undo this specific effect, but it's a " +
              "reasonable safety net before any repair session.\n\nRun /ResetBase now?"
            : "This runs:\n\n  dism /online /cleanup-image /startcomponentcleanup\n\n" +
              "Removes superseded versions of components from the WinSxS store. Reversible - Windows " +
              "can still uninstall any currently installed update afterward. Run cleanup now?";

        var confirm = MessageBox.Show(message,
            resetBase ? "Component store cleanup - /ResetBase (PERMANENT)" : "Component store cleanup",
            MessageBoxButton.YesNo, resetBase ? MessageBoxImage.Stop : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (resetBase) IsResetBaseRunning = true; else IsCleaningComponentStore = true;
        CleanupProgressPercent = 0;
        CleanupResultText = null;
        _cleanupCts = new CancellationTokenSource();
        CancelComponentCleanupCommand.RaiseCanExecuteChanged();
        try
        {
            var progress = new Progress<int>(p => CleanupProgressPercent = p);
            var (success, output, exitCode) = await DismService.StartComponentCleanupAsync(resetBase, progress, _cleanupCts.Token).ConfigureAwait(true);
            string tail = output.Length > 1500 ? output[^1500..] : output;
            CleanupResultText = success
                ? resetBase ? "/ResetBase cleanup finished." : "Component store cleanup finished."
                : $"Cleanup failed (exit code {exitCode}):\n{tail}";

            if (success)
                ComponentStoreAnalysis = await DismService.AnalyzeComponentStoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsCleaningComponentStore = false;
            IsResetBaseRunning = false;
            _cleanupCts = null;
            CancelComponentCleanupCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#784: CheckHealth (instant, reads a stored flag) - no progress/cancel needed, same
    /// as this tab's other quick read-only checks.</summary>
    private async Task RunCheckHealthAsync()
    {
        IsCheckingHealth = true;
        try
        {
            var result = await DismService.CheckHealthAsync().ConfigureAwait(true);
            CheckHealthResult = result;
            AppendIntegrityHistory("DISM CheckHealth", result.Summary, result.Success, result.DurationSeconds,
                result.IsRepairable == true ? new List<string> { "Component store corruption flagged (CheckHealth gives no per-file detail)" } : new List<string>());
        }
        finally
        {
            IsCheckingHealth = false;
        }
    }

    /// <summary>#784: ScanHealth - a real scan, can take minutes. Progress streamed into
    /// ScanHealthProgressPercent; cancellable via CancelScanHealthCommand/_scanHealthCts.</summary>
    private async Task RunScanHealthAsync()
    {
        IsScanningHealth = true;
        ScanHealthProgressPercent = 0;
        _scanHealthCts = new CancellationTokenSource();
        CancelScanHealthCommand.RaiseCanExecuteChanged();
        try
        {
            var progress = new Progress<int>(p => ScanHealthProgressPercent = p);
            var result = await DismService.ScanHealthAsync(progress, _scanHealthCts.Token).ConfigureAwait(true);
            ScanHealthResult = result;
            AppendIntegrityHistory("DISM ScanHealth", result.Summary, result.Success, result.DurationSeconds,
                result.IsRepairable == true ? new List<string> { "Component store corruption found" } : new List<string>());
        }
        finally
        {
            IsScanningHealth = false;
            _scanHealthCts = null;
            CancelScanHealthCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#784/#785: RestoreHealth - attempts an actual repair, optionally against the
    /// #785-picked WIM source (sourceArg is the pre-built `/Source:WIM:"&lt;path&gt;":&lt;index&gt;
    /// /LimitAccess` argument, or null for the plain Windows-Update-backed attempt). On success or
    /// failure alike, re-evaluates #788's repair-install guidance (a fresh failure is the more urgent
    /// trigger for that card). Progress into RestoreHealthProgressPercent; cancellable via
    /// CancelRestoreHealthCommand/_restoreHealthCts.</summary>
    private async Task RunRestoreHealthAsync(string? sourceArg)
    {
        IsRestoringHealth = true;
        RestoreHealthProgressPercent = 0;
        _restoreHealthCts = new CancellationTokenSource();
        CancelRestoreHealthCommand.RaiseCanExecuteChanged();
        try
        {
            var progress = new Progress<int>(p => RestoreHealthProgressPercent = p);
            var result = await DismService.RestoreHealthAsync(sourceArg, progress, _restoreHealthCts.Token).ConfigureAwait(true);
            RestoreHealthResult = result;
            AppendIntegrityHistory("DISM RestoreHealth", result.Summary, result.Success, result.DurationSeconds,
                result.Success ? new List<string>() : new List<string> { $"RestoreHealth failed{(result.ErrorCode is { } code ? $" ({code})" : string.Empty)}" });
            UpdateRepairInstallGuidance();

            if (result.NeedsRepairSource)
                StatusMessage = "DISM /RestoreHealth needs a repair source (error 0x800f081f) - use \"Browse for repair source\" below to pick a mounted ISO's install.wim/install.esd.";
        }
        finally
        {
            IsRestoringHealth = false;
            _restoreHealthCts = null;
            CancelRestoreHealthCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#785: browse to a mounted ISO's sources\install.wim/install.esd and list its image
    /// indexes via `Get-WimInfo`, so RetryRestoreHealthWithSourceAsync can build a `/Source:WIM:...`
    /// argument that names the matching edition instead of guessing index 1.</summary>
    private async Task BrowseRepairSourceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Browse for a repair source (install.wim or install.esd from a mounted ISO)",
            Filter = "Windows image files (*.wim;*.esd)|*.wim;*.esd|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        IsLoadingRepairSourceImages = true;
        RepairSourceImages.Clear();
        SelectedRepairSourceImage = null;
        try
        {
            var (success, images, error) = await DismService.GetWimInfoAsync(dialog.FileName).ConfigureAwait(true);
            if (success)
            {
                RepairSourceWimPath = dialog.FileName;
                foreach (var image in images) RepairSourceImages.Add(image);
            }
            else
            {
                RepairSourceWimPath = null;
                StatusMessage = $"Couldn't read image indexes from \"{dialog.FileName}\": {error}";
            }
        }
        finally
        {
            IsLoadingRepairSourceImages = false;
        }
    }

    /// <summary>#785: retries RestoreHealth against the picked WIM path + image index.</summary>
    private async Task RetryRestoreHealthWithSourceAsync()
    {
        if (SelectedRepairSourceImage is not { } image || RepairSourceWimPath is not { } path) return;
        string sourceArg = $"/Source:WIM:\"{path}\":{image.Index} /LimitAccess";
        await RunRestoreHealthAsync(sourceArg);
    }

    /// <summary>#786: `sfc /scannow` with CBS.log [SR]-line extraction - read/repair-in-place, same
    /// "no confirmation, it's the whole point of the button" treatment as #784's DISM health scans.
    /// Progress into SfcProgressPercent; cancellable via CancelSfcScanCommand/_sfcCts.</summary>
    private async Task RunSfcScanAsync()
    {
        IsRunningSfcScan = true;
        SfcProgressPercent = 0;
        _sfcCts = new CancellationTokenSource();
        CancelSfcScanCommand.RaiseCanExecuteChanged();
        try
        {
            var progress = new Progress<int>(p => SfcProgressPercent = p);
            var result = await SfcIntegrityService.RunScanAsync(progress, _sfcCts.Token).ConfigureAwait(true);
            SfcScanResult = result;
            AppendIntegrityHistory("SFC", result.VerdictText, result.Success, result.DurationSeconds, result.UnrepairableEntries);
            UpdateRepairInstallGuidance();
        }
        finally
        {
            IsRunningSfcScan = false;
            _sfcCts = null;
            CancelSfcScanCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#787: appends one scan's result to integrity-history.json (shared by SFC and every
    /// DISM health-scan verb) and refreshes both the in-memory timeline and the SFC-vs-previous-SFC
    /// comparison text shown under the scan buttons.</summary>
    private void AppendIntegrityHistory(string scanType, string verdict, bool success, double durationSeconds, List<string> unrepairableFiles)
    {
        var entry = new IntegrityHistoryEntry
        {
            Timestamp = DateTime.Now,
            ScanType = scanType,
            Verdict = verdict,
            DurationSeconds = durationSeconds,
            Success = success,
            UnrepairableFiles = unrepairableFiles,
        };
        var history = SfcIntegrityService.AppendAndSave(entry);
        IntegrityHistory.Clear();
        foreach (var h in history.OrderByDescending(h => h.Timestamp)) IntegrityHistory.Add(h);
        SfcComparisonText = SfcIntegrityService.CompareToPreviousRun(history, "SFC");
    }

    /// <summary>#788: recomputes whether the in-place-repair-install guidance card should show. A
    /// RestoreHealth failure earlier in this session takes priority (the more urgent, immediate
    /// trigger); otherwise falls back to the persisted history's own "two unrepairable SFC scans in a
    /// row" signal, so the card still shows on a fresh tab open even if that state was reached in an
    /// earlier session.</summary>
    private void UpdateRepairInstallGuidance()
    {
        if (RestoreHealthResult is { Success: false } rh)
        {
            RepairInstallGuidance = SfcIntegrityService.BuildGuidance(
                $"DISM /RestoreHealth failed (exit code {rh.ExitCode}{(rh.ErrorCode is { } code ? $", {code}" : string.Empty)}).");
            return;
        }

        var history = SfcIntegrityService.LoadHistory();
        RepairInstallGuidance = SfcIntegrityService.HasRepeatedUnrepairableSfcFailures(history)
            ? SfcIntegrityService.BuildGuidance("sfc /scannow reported unrepairable files on the two most recent scans in a row.")
            : null;
    }

    /// <summary>#789: restore point inventory, per-volume System Protection inference, shadow-storage
    /// allocation and the automatic-frequency policy readout - all four bundled in one snapshot call.</summary>
    private async Task LoadSystemRestoreAsync()
    {
        IsLoadingSystemRestore = true;
        try
        {
            SystemRestoreSnapshot = await SystemRestoreService.ReadSnapshotAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoadingSystemRestore = false;
        }
    }

    /// <summary>#790: creates a MODIFY_SETTINGS-type restore point via the SystemRestore WMI class -
    /// confirmed first (it does mutate system state / shadow-storage usage), then reloads the Recovery
    /// snapshot so the new point shows up in the inventory immediately.</summary>
    private async Task CreateRestorePointAsync()
    {
        var confirm = MessageBox.Show(
            "This creates a new System Restore point via Windows' own SystemRestore WMI class - the " +
            "same action as Control Panel's \"Create a restore point\" button. Windows may skip it if " +
            "System Protection is off for the system volume, or if one was created very recently. " +
            "Create a restore point now?",
            "Create a restore point", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsCreatingRestorePoint = true;
        try
        {
            string description = $"Task Manager Plus - manual restore point {DateTime.Now:yyyy-MM-dd HH:mm}";
            var (success, message) = await SystemRestoreService.CreateRestorePointAsync(description).ConfigureAwait(true);
            StatusMessage = success ? "Restore point created." : $"Couldn't create restore point: {message}";
            if (success) await LoadSystemRestoreAsync();
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }
    }
}
