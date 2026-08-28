using System.Collections.ObjectModel;
using System.Windows;
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
}
