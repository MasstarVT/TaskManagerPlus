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

    public WindowsHealthViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ResumeUpdatesCommand = new AsyncRelayCommand(ResumeUpdatesAsync, () => UpdatePolicy.IsPausedNow);
        LoadServicingPackagesCommand = new AsyncRelayCommand(LoadServicingPackagesAsync);
        CheckFeatureUpdateFailureCommand = new AsyncRelayCommand(CheckFeatureUpdateFailureAsync);
        RunSetupDiagCommand = new AsyncRelayCommand(RunSetupDiagAsync, () => FeatureUpdateFailure?.SetupDiagAvailable ?? false);

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
}
