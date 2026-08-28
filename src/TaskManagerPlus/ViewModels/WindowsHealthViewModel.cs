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

    private DismComponentStoreAnalysis? _componentStoreAnalysis;
    public DismComponentStoreAnalysis? DismComponentStoreAnalysis { get => _componentStoreAnalysis; private set => SetProperty(ref _componentStoreAnalysis, value); }

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

    #region #791 - WMI repository verify/repair

    private WmiRepositoryHealth? _wmiRepositoryHealth;
    public WmiRepositoryHealth? WmiRepositoryHealth { get => _wmiRepositoryHealth; private set => SetProperty(ref _wmiRepositoryHealth, value); }

    private bool _isCheckingWmiRepository;
    public bool IsCheckingWmiRepository { get => _isCheckingWmiRepository; private set => SetProperty(ref _isCheckingWmiRepository, value); }

    private bool _isSalvagingWmiRepository;
    public bool IsSalvagingWmiRepository { get => _isSalvagingWmiRepository; private set => SetProperty(ref _isSalvagingWmiRepository, value); }

    private bool _isResettingWmiRepository;
    public bool IsResettingWmiRepository { get => _isResettingWmiRepository; private set => SetProperty(ref _isResettingWmiRepository, value); }

    public AsyncRelayCommand VerifyWmiRepositoryCommand { get; }
    public AsyncRelayCommand SalvageWmiRepositoryCommand { get; }
    public AsyncRelayCommand ResetWmiRepositoryCommand { get; }

    #endregion

    #region #792/#793 - WMI activity error analyzer + permanent event consumer inventory

    public ObservableCollection<WmiActivityErrorGroup> WmiActivityErrorGroups { get; } = new();
    public ObservableCollection<WmiEventConsumerEntry> WmiEventConsumers { get; } = new();

    private bool _isLoadingWmiDiagnostics;
    public bool IsLoadingWmiDiagnostics { get => _isLoadingWmiDiagnostics; private set => SetProperty(ref _isLoadingWmiDiagnostics, value); }

    private bool _hasLoadedWmiDiagnostics;
    public bool HasLoadedWmiDiagnostics { get => _hasLoadedWmiDiagnostics; private set => SetProperty(ref _hasLoadedWmiDiagnostics, value); }

    public AsyncRelayCommand LoadWmiDiagnosticsCommand { get; }

    #endregion

    #region #794/#795 - Registry hive health + backup status/re-enable

    private RegistryHealthSnapshot? _registryHealth;
    public RegistryHealthSnapshot? RegistryHealth { get => _registryHealth; private set => SetProperty(ref _registryHealth, value); }

    private RegistryBackupStatus? _registryBackupStatus;
    public RegistryBackupStatus? RegistryBackupStatus { get => _registryBackupStatus; private set => SetProperty(ref _registryBackupStatus, value); }

    private bool _isLoadingRegistryHealth;
    public bool IsLoadingRegistryHealth { get => _isLoadingRegistryHealth; private set => SetProperty(ref _isLoadingRegistryHealth, value); }

    public AsyncRelayCommand LoadRegistryHealthCommand { get; }
    public AsyncRelayCommand EnablePeriodicBackupCommand { get; }

    #endregion

    #region #796 - Registry change journal (undo + export-as-.reg)

    public ObservableCollection<RegistryChangeEntry> RegistryChanges { get; } = new();

    public RelayCommand UndoRegistryChangeCommand { get; }
    public RelayCommand ExportRegistryChangeCommand { get; }

    #endregion

    #region #797 - PATH doctor

    private PathDoctorResult? _pathDoctorResult;
    public PathDoctorResult? PathDoctorResult { get => _pathDoctorResult; private set => SetProperty(ref _pathDoctorResult, value); }

    private bool _isRunningPathDoctor;
    public bool IsRunningPathDoctor { get => _isRunningPathDoctor; private set => SetProperty(ref _isRunningPathDoctor, value); }

    public AsyncRelayCommand RunPathDoctorCommand { get; }

    #endregion

    #region #798 - Environment variable inspector/editor

    public ObservableCollection<EnvironmentVariableEntry> EnvironmentVariables { get; } = new();
    public ObservableCollection<EnvironmentSanityCheck> EnvironmentSanityChecks { get; } = new();

    private bool _isLoadingEnvironmentVariables;
    public bool IsLoadingEnvironmentVariables { get => _isLoadingEnvironmentVariables; private set => SetProperty(ref _isLoadingEnvironmentVariables, value); }

    private bool _hasLoadedEnvironmentVariables;
    public bool HasLoadedEnvironmentVariables { get => _hasLoadedEnvironmentVariables; private set => SetProperty(ref _hasLoadedEnvironmentVariables, value); }

    private string _newEnvVarScope = "User";
    public string NewEnvVarScope { get => _newEnvVarScope; set => SetProperty(ref _newEnvVarScope, value); }

    private string _newEnvVarName = string.Empty;
    public string NewEnvVarName { get => _newEnvVarName; set => SetProperty(ref _newEnvVarName, value); }

    private string _newEnvVarValue = string.Empty;
    public string NewEnvVarValue { get => _newEnvVarValue; set => SetProperty(ref _newEnvVarValue, value); }

    public IReadOnlyList<string> EnvVarScopeOptions { get; } = new[] { "User", "Machine" };

    public AsyncRelayCommand LoadEnvironmentVariablesCommand { get; }
    public AsyncRelayCommand SetEnvironmentVariableCommand { get; }
    public AsyncRelayCommand DeleteEnvironmentVariableCommand { get; }

    #endregion

    #region #799 - Process vs. system environment drift (Windows Health tab summary side)

    private int _processEnvironmentDriftCount;
    public int ProcessEnvironmentDriftCount { get => _processEnvironmentDriftCount; private set => SetProperty(ref _processEnvironmentDriftCount, value); }

    private int _processEnvironmentCheckedCount;
    public int ProcessEnvironmentCheckedCount { get => _processEnvironmentCheckedCount; private set => SetProperty(ref _processEnvironmentCheckedCount, value); }

    private bool _hasScannedProcessEnvironmentDrift;
    public bool HasScannedProcessEnvironmentDrift { get => _hasScannedProcessEnvironmentDrift; private set => SetProperty(ref _hasScannedProcessEnvironmentDrift, value); }

    private bool _isScanningProcessEnvironmentDrift;
    public bool IsScanningProcessEnvironmentDrift { get => _isScanningProcessEnvironmentDrift; private set => SetProperty(ref _isScanningProcessEnvironmentDrift, value); }

    public AsyncRelayCommand ScanProcessEnvironmentDriftCommand { get; }

    #endregion

    #region #800 - Activation, build lifecycle and upgrade-readiness roll-up (top card)

    private UpgradeReadinessSnapshot? _upgradeReadiness;
    public UpgradeReadinessSnapshot? UpgradeReadiness { get => _upgradeReadiness; private set => SetProperty(ref _upgradeReadiness, value); }

    private bool _isLoadingUpgradeReadiness;
    public bool IsLoadingUpgradeReadiness { get => _isLoadingUpgradeReadiness; private set => SetProperty(ref _isLoadingUpgradeReadiness, value); }

    public AsyncRelayCommand LoadUpgradeReadinessCommand { get; }
    public AsyncRelayCommand MeasureEspForReadinessCommand { get; }

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

        VerifyWmiRepositoryCommand = new AsyncRelayCommand(VerifyWmiRepositoryAsync, () => !IsCheckingWmiRepository);
        SalvageWmiRepositoryCommand = new AsyncRelayCommand(SalvageWmiRepositoryAsync, () => !IsSalvagingWmiRepository);
        ResetWmiRepositoryCommand = new AsyncRelayCommand(ResetWmiRepositoryAsync, () => !IsResettingWmiRepository);

        LoadWmiDiagnosticsCommand = new AsyncRelayCommand(LoadWmiDiagnosticsAsync, () => !IsLoadingWmiDiagnostics);

        LoadRegistryHealthCommand = new AsyncRelayCommand(LoadRegistryHealthAsync, () => !IsLoadingRegistryHealth);
        EnablePeriodicBackupCommand = new AsyncRelayCommand(EnablePeriodicBackupAsync, () => RegistryBackupStatus?.PeriodicBackupEnabled != true);

        UndoRegistryChangeCommand = new RelayCommand(param => _ = UndoRegistryChangeAsync(param as RegistryChangeEntry), param => param is RegistryChangeEntry { Undone: false });
        ExportRegistryChangeCommand = new RelayCommand(param => ExportRegistryChange(param as RegistryChangeEntry), param => param is RegistryChangeEntry);

        RunPathDoctorCommand = new AsyncRelayCommand(RunPathDoctorAsync, () => !IsRunningPathDoctor);

        LoadEnvironmentVariablesCommand = new AsyncRelayCommand(LoadEnvironmentVariablesAsync, () => !IsLoadingEnvironmentVariables);
        SetEnvironmentVariableCommand = new AsyncRelayCommand(SetEnvironmentVariableAsync, () => !string.IsNullOrWhiteSpace(NewEnvVarName));
        DeleteEnvironmentVariableCommand = new AsyncRelayCommand(param => DeleteEnvironmentVariableAsync(param as EnvironmentVariableEntry));

        ScanProcessEnvironmentDriftCommand = new AsyncRelayCommand(ScanProcessEnvironmentDriftAsync, () => !IsScanningProcessEnvironmentDrift);

        LoadUpgradeReadinessCommand = new AsyncRelayCommand(LoadUpgradeReadinessAsync, () => !IsLoadingUpgradeReadiness);
        MeasureEspForReadinessCommand = new AsyncRelayCommand(MeasureEspForReadinessAsync, () => !IsLoadingUpgradeReadiness && UpgradeReadiness is { EspFound: true });

        // #796: the registry-change journal is a small local file (same cost tier as #787's
        // integrity-history.json below) - loaded up front so "Changes made by this app" already
        // reflects prior sessions the moment the tab (or the settings drawer) opens.
        foreach (var change in RegistryChangeJournalService.LoadHistory().OrderByDescending(c => c.Timestamp))
            RegistryChanges.Add(change);

        // #791: the repository folder's own size/last-modified is a plain directory-size sum (no
        // shell-out) - cheap on a typical repository, but a large one can still mean walking many
        // thousands of small files, so this is read off the UI thread (like every other "fire off
        // an initial load from the constructor" case in this file) rather than synchronously
        // inline, which would otherwise block MainViewModel's own synchronous construction of
        // every tab's ViewModel at app startup.
        _ = LoadWmiRepositoryFootprintAsync();

        // #800: the upgrade-readiness card is this tab's OTHER top card (alongside #774's pending-
        // reboot panel above) - every read it does (BCD/TPM/Secure Boot/partition-style/ESP/
        // licensing) is a quick registry/WMI/bcdedit read, no event-log scan or DISM call, so it
        // loads automatically rather than waiting on a button like #791-799's heavier cards below.
        _ = LoadUpgradeReadinessAsync();

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

            // #800: now that a real package list exists, replace the readiness card's placeholder
            // servicing-stack-version text - see RefreshServicingStackVersionText's remarks.
            RefreshServicingStackVersionText();
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
            DismComponentStoreAnalysis = await DismService.AnalyzeComponentStoreAsync();
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
                DismComponentStoreAnalysis = await DismService.AnalyzeComponentStoreAsync().ConfigureAwait(true);
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

    #region #791 methods - WMI repository verify/salvage/reset

    private async Task LoadWmiRepositoryFootprintAsync()
    {
        WmiRepositoryHealth = await Task.Run(WmiHealthService.ReadRepositoryFootprint).ConfigureAwait(true);
    }

    /// <summary>#791: `winmgmt /verifyrepository` - read-only, no confirmation needed (same
    /// treatment #784's DISM CheckHealth gets).</summary>
    private async Task VerifyWmiRepositoryAsync()
    {
        IsCheckingWmiRepository = true;
        try
        {
            WmiRepositoryHealth = await WmiHealthService.VerifyRepositoryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsCheckingWmiRepository = false;
        }
    }

    /// <summary>#791: `winmgmt /salvagerepository` - confirmed first (attempts an in-place repair),
    /// matching CLAUDE.md's mutating-action convention.</summary>
    private async Task SalvageWmiRepositoryAsync()
    {
        var confirm = MessageBox.Show(
            "This runs:\n\n  winmgmt /salvagerepository\n\n" +
            "Attempts to repair the WMI repository in place, keeping as much of its existing content " +
            "as possible. Usually safe, but a WMI service restart happens as part of it. Run now?",
            "Salvage WMI repository", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsSalvagingWmiRepository = true;
        try
        {
            var (success, output) = await WmiHealthService.SalvageRepositoryAsync().ConfigureAwait(true);
            StatusMessage = success ? "WMI repository salvage finished." : $"WMI repository salvage reported a problem: {output}";
            WmiRepositoryHealth = await WmiHealthService.VerifyRepositoryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsSalvagingWmiRepository = false;
        }
    }

    /// <summary>#791: `winmgmt /resetrepository` - the last-resort, fully-destructive rebuild.
    /// Labelled and dialog-worded as escalation per this chunk's own instructions: it can break
    /// third-party management/monitoring agents (SCCM, many AV/EDR/backup agents) that registered
    /// their own WMI classes/providers, since those registrations are discarded along with
    /// everything else in the repository.</summary>
    private async Task ResetWmiRepositoryAsync()
    {
        var confirm = MessageBox.Show(
            "ESCALATION - LAST RESORT ONLY. This runs:\n\n  winmgmt /resetrepository\n\n" +
            "Discards the ENTIRE WMI repository and rebuilds it from scratch. This can break any " +
            "third-party management, monitoring, antivirus/EDR, or backup agent that registered its " +
            "own WMI classes or providers - they will very likely need reinstalling or re-registering " +
            "afterward. Only use this if \"Salvage\" above has already failed to fix an inconsistent " +
            "repository. Reset the WMI repository now?",
            "Reset WMI repository (PERMANENT, ESCALATION)", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (confirm != MessageBoxResult.Yes) return;

        IsResettingWmiRepository = true;
        try
        {
            var (success, output) = await WmiHealthService.ResetRepositoryAsync().ConfigureAwait(true);
            StatusMessage = success ? "WMI repository reset finished." : $"WMI repository reset reported a problem: {output}";
            WmiRepositoryHealth = await WmiHealthService.VerifyRepositoryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsResettingWmiRepository = false;
        }
    }

    #endregion

    #region #792/#793 methods - WMI activity errors + permanent event consumers

    /// <summary>#792/#793: bundled behind one button (same "share one refresh" shape #769/#771/
    /// #772/#774/#775 already use above) since both are read-only WMI-card diagnostics gathered in
    /// one pass - a 30-day WMI-Activity/Operational scan plus a root\subscription enumeration.</summary>
    private async Task LoadWmiDiagnosticsAsync()
    {
        IsLoadingWmiDiagnostics = true;
        try
        {
            var errorGroups = await Task.Run(() => WmiHealthService.ReadActivityErrorGroups()).ConfigureAwait(true);
            var consumers = await Task.Run(WmiHealthService.ReadPermanentConsumers).ConfigureAwait(true);

            WmiActivityErrorGroups.Clear();
            foreach (var g in errorGroups) WmiActivityErrorGroups.Add(g);

            WmiEventConsumers.Clear();
            foreach (var c in consumers) WmiEventConsumers.Add(c);

            HasLoadedWmiDiagnostics = true;
        }
        finally
        {
            IsLoadingWmiDiagnostics = false;
        }
    }

    #endregion

    #region #794/#795 methods - Registry hive health + backup status/re-enable

    /// <summary>#794/#795: bundled behind one button - both read the same System32\config folder
    /// tree (hive files, RegBack) so one "Load" click populates the whole Registry card.</summary>
    private async Task LoadRegistryHealthAsync()
    {
        IsLoadingRegistryHealth = true;
        try
        {
            RegistryHealth = await Task.Run(RegistryHealthService.ReadHiveHealth).ConfigureAwait(true);
            RegistryBackupStatus = await Task.Run(RegistryHealthService.ReadBackupStatus).ConfigureAwait(true);
            EnablePeriodicBackupCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsLoadingRegistryHealth = false;
        }
    }

    /// <summary>#795: sets EnablePeriodicBackup=1 - confirmed first, matching CLAUDE.md's
    /// mutating-action convention.</summary>
    private async Task EnablePeriodicBackupAsync()
    {
        var confirm = MessageBox.Show(
            "This sets:\n\n  HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Configuration Manager\n  EnablePeriodicBackup = 1\n\n" +
            "Restores the pre-Windows-10-1803 behavior of Windows automatically refreshing " +
            "System32\\config\\RegBack roughly every 10 days via a scheduled task. Enable periodic " +
            "registry backups now?",
            "Enable periodic registry backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(RegistryHealthService.EnablePeriodicBackup).ConfigureAwait(true);
        StatusMessage = success ? "Periodic registry backup enabled." : $"Couldn't enable periodic registry backup: {error}";
        if (success)
        {
            var updated = RegistryChangeJournalService.LoadHistory().OrderByDescending(c => c.Timestamp).ToList();
            RegistryChanges.Clear();
            foreach (var c in updated) RegistryChanges.Add(c);
            RegistryBackupStatus = await Task.Run(RegistryHealthService.ReadBackupStatus).ConfigureAwait(true);
            EnablePeriodicBackupCommand.RaiseCanExecuteChanged();
        }
    }

    #endregion

    #region #796 methods - Registry change journal (undo + export-as-.reg)

    /// <summary>#796: confirmed first (it's itself a registry write), then re-reads the whole
    /// journal file rather than just flipping Undone locally, so the list stays correct even if
    /// something else in this session also appended to it in the meantime.</summary>
    private async Task UndoRegistryChangeAsync(RegistryChangeEntry? entry)
    {
        if (entry is null) return;

        var confirm = MessageBox.Show(
            $"This writes the previous value back to:\n\n  {entry.FullKeyText}\n  \"{entry.ValueName}\"\n\n" +
            (entry.OldValueText is null
                ? "The value didn't exist before this app created it - undoing will DELETE it."
                : $"Old value: {entry.OldValueText}\nCurrent value: {entry.NewValueText}") +
            "\n\nUndo this change now?",
            "Undo registry change", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(() => RegistryChangeJournalService.Undo(entry)).ConfigureAwait(true);
        StatusMessage = success ? "Change undone." : $"Couldn't undo this change: {error}";

        var updated = RegistryChangeJournalService.LoadHistory().OrderByDescending(c => c.Timestamp).ToList();
        RegistryChanges.Clear();
        foreach (var c in updated) RegistryChanges.Add(c);
    }

    /// <summary>#796: exports one journal entry as a standalone .reg file via a Save dialog - same
    /// OpenFileDialog/SaveFileDialog usage #785's BrowseRepairSourceAsync already takes for a
    /// different file-picker need in this same class.</summary>
    private void ExportRegistryChange(RegistryChangeEntry? entry)
    {
        if (entry is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export registry change as .reg",
            Filter = "Registry files (*.reg)|*.reg|All files (*.*)|*.*",
            FileName = $"{entry.ValueName}.reg",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, RegistryChangeJournalService.BuildRegFileContent(entry));
            StatusMessage = $"Exported to \"{dialog.FileName}\".";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't export: {ex.Message}";
        }
    }

    #endregion

    #region #797 methods - PATH doctor

    private async Task RunPathDoctorAsync()
    {
        IsRunningPathDoctor = true;
        try
        {
            PathDoctorResult = await Task.Run(EnvironmentHealthService.ReadPathDoctorResult).ConfigureAwait(true);
        }
        finally
        {
            IsRunningPathDoctor = false;
        }
    }

    #endregion

    #region #798 methods - Environment variable inspector/editor

    private async Task LoadEnvironmentVariablesAsync()
    {
        IsLoadingEnvironmentVariables = true;
        try
        {
            var variables = await Task.Run(EnvironmentHealthService.ReadAllVariables).ConfigureAwait(true);
            EnvironmentVariables.Clear();
            foreach (var v in variables) EnvironmentVariables.Add(v);

            var checks = await Task.Run(() => EnvironmentHealthService.RunSanityChecks(variables)).ConfigureAwait(true);
            EnvironmentSanityChecks.Clear();
            foreach (var c in checks) EnvironmentSanityChecks.Add(c);

            HasLoadedEnvironmentVariables = true;
        }
        finally
        {
            IsLoadingEnvironmentVariables = false;
        }
    }

    /// <summary>#798: add-or-edit, confirmed first with the exact scope/name/value shown - matching
    /// CLAUDE.md's mutating-action convention for #795's EnablePeriodicBackup toggle and #791's
    /// Salvage/Reset above. Broadcasts WM_SETTINGCHANGE afterward (inside
    /// EnvironmentHealthService.SetVariable) and reloads the list/sanity checks/journal.</summary>
    private async Task SetEnvironmentVariableAsync()
    {
        string scope = NewEnvVarScope, name = NewEnvVarName.Trim(), value = NewEnvVarValue;
        if (name.Length == 0) return;

        bool existed = EnvironmentVariables.Any(v => v.Scope == scope && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var confirm = MessageBox.Show(
            $"This {(existed ? "changes" : "creates")} the {scope} environment variable:\n\n  {name} = {value}\n\n" +
            "and notifies already-running programs that listen for environment changes (Explorer " +
            "among them) - programs that don't listen still need a restart to see it. Continue?",
            $"{(existed ? "Edit" : "Add")} environment variable", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(() => EnvironmentHealthService.SetVariable(scope, name, value)).ConfigureAwait(true);
        StatusMessage = success ? $"{name} saved." : $"Couldn't save {name}: {error}";
        if (success)
        {
            NewEnvVarName = string.Empty;
            NewEnvVarValue = string.Empty;
            await LoadEnvironmentVariablesAsync();
            ReloadRegistryChanges();
        }
    }

    /// <summary>#798: delete, confirmed first with the exact scope/name/current-value shown.</summary>
    private async Task DeleteEnvironmentVariableAsync(EnvironmentVariableEntry? entry)
    {
        if (entry is null) return;

        var confirm = MessageBox.Show(
            $"This deletes the {entry.Scope} environment variable:\n\n  {entry.Name} = {entry.Value}\n\n" +
            "and notifies already-running programs that listen for environment changes. Delete now?",
            "Delete environment variable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(() => EnvironmentHealthService.DeleteVariable(entry.Scope, entry.Name)).ConfigureAwait(true);
        StatusMessage = success ? $"{entry.Name} deleted." : $"Couldn't delete {entry.Name}: {error}";
        if (success)
        {
            await LoadEnvironmentVariablesAsync();
            ReloadRegistryChanges();
        }
    }

    private void ReloadRegistryChanges()
    {
        var updated = RegistryChangeJournalService.LoadHistory().OrderByDescending(c => c.Timestamp).ToList();
        RegistryChanges.Clear();
        foreach (var c in updated) RegistryChanges.Add(c);
    }

    #endregion

    #region #799 methods - Process vs. system environment drift (tab summary)

    /// <summary>#799 (Windows Health side): an explicit, on-demand sweep of every running process's
    /// PATH/TEMP against the current machine+user values - see ProcessEnvironmentDriftService's own
    /// remarks for why this is a button, not something run automatically. Per-row detail for a
    /// single already-selected process lives on the Processes tab instead (ProcessesViewModel.
    /// SelectedProcessEnvironmentDrift) - this side only needs the summary count/link.</summary>
    private async Task ScanProcessEnvironmentDriftAsync()
    {
        IsScanningProcessEnvironmentDrift = true;
        try
        {
            var (checkedCount, drifted) = await ProcessEnvironmentDriftService.ScanAllAsync().ConfigureAwait(true);
            ProcessEnvironmentCheckedCount = checkedCount;
            ProcessEnvironmentDriftCount = drifted.Count;
            HasScannedProcessEnvironmentDrift = true;
        }
        finally
        {
            IsScanningProcessEnvironmentDrift = false;
        }
    }

    #endregion

    #region #800 methods - Upgrade readiness roll-up

    private Task LoadUpgradeReadinessAsync() => LoadUpgradeReadinessAsync(measureEsp: false);

    private async Task LoadUpgradeReadinessAsync(bool measureEsp)
    {
        IsLoadingUpgradeReadiness = true;
        try
        {
            UpgradeReadiness = await UpgradeReadinessService.ReadSnapshotAsync(measureEsp).ConfigureAwait(true);
            MeasureEspForReadinessCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read upgrade-readiness data: {ex.Message}";
        }
        finally
        {
            IsLoadingUpgradeReadiness = false;
        }
    }

    /// <summary>#800: the ESP-mount step is opt-in, matching the Startup tab's own #738 "Measure
    /// free space" button (SystemPartitionService.MeasureEspFreeSpaceAsync briefly mutates system
    /// state by mounting the partition) - never run automatically, only from this explicit click.</summary>
    private async Task MeasureEspForReadinessAsync() => await LoadUpgradeReadinessAsync(measureEsp: true).ConfigureAwait(true);

    /// <summary>#800: once #770's servicing-package list has been loaded (its own button, above),
    /// this re-describes the readiness card's servicing-stack version text from that already-
    /// fetched list rather than running a second DISM enumeration - called from
    /// LoadServicingPackagesAsync below via a small hook.</summary>
    private void RefreshServicingStackVersionText()
    {
        if (UpgradeReadiness is not { } current) return;
        string versionText = UpgradeReadinessService.DescribeServicingStackVersion(ServicingPackages);
        UpgradeReadiness = new UpgradeReadinessSnapshot
        {
            Activation = current.Activation,
            EditionText = current.EditionText,
            BuildText = current.BuildText,
            DisplayVersionText = current.DisplayVersionText,
            EndOfServicing = current.EndOfServicing,
            ServicingStackVersionText = versionText,
            TpmReady = current.TpmReady,
            SecureBootEnabled = current.SecureBootEnabled,
            SystemDiskIsMbr = current.SystemDiskIsMbr,
            SystemDriveFreeBytes = current.SystemDriveFreeBytes,
            EspFound = current.EspFound,
            EspFreeBytes = current.EspFreeBytes,
            BlockingItems = current.BlockingItems,
        };
    }

    #endregion
}
