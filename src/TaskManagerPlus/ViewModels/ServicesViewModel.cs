using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class ServicesViewModel : ObservableObject, IDisposable
{
    private readonly ServiceControlService _service = new();
    private readonly EventLogService _eventLog = new();

    /// <summary>#192/#193: service crash-loop detection (SCM 7000/7009/7024/7031/7034) and the
    /// ServicesPipeTimeout start-timeout explanation - its own Services/* instance, same
    /// no-DI-container "each ViewModel composes its own services directly" convention as _eventLog
    /// above.</summary>
    private readonly ServiceHealthEventService _serviceHealth = new();

    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;
    private bool _isBusy;

    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ICollectionView ServicesView { get; }

    /// <summary>Round 7 #15: kernel/file-system driver "services" - a separate collection/view
    /// rather than mixed into Services, since drivers carry a much narrower set of meaningful
    /// columns (no dependencies, rarely a logon account) - see ServiceControlService.SampleDrivers.
    /// Only sampled while ShowDrivers is on, so a user who never opens this sub-view never pays
    /// for the extra enumeration.</summary>
    public ObservableCollection<ServiceRow> Drivers { get; } = new();

    private bool _showDrivers;
    public bool ShowDrivers
    {
        get => _showDrivers;
        set
        {
            if (SetProperty(ref _showDrivers, value) && value)
            {
                ShowSvcHostGroups = false; // #761: the two sub-views share the same grid row - mutually exclusive
                _ = RefreshDriversAsync();
            }
        }
    }

    /// <summary>#761/#762: svchost group breakdown sub-view - same "toggle checkbox loads/reveals a
    /// second grid in the same row" shape ShowDrivers already uses. Cross-linked from the Processes
    /// tab's svchost row (see ProcessesView.xaml.cs).</summary>
    public ObservableCollection<SvcHostGroupInfo> SvcHostGroups { get; } = new();

    private SvcHostSplitInfo? _svcHostSplitInfo;
    public SvcHostSplitInfo? SvcHostSplitInfo { get => _svcHostSplitInfo; private set => SetProperty(ref _svcHostSplitInfo, value); }

    private bool _showSvcHostGroups;
    public bool ShowSvcHostGroups
    {
        get => _showSvcHostGroups;
        set
        {
            if (SetProperty(ref _showSvcHostGroups, value) && value)
            {
                ShowDrivers = false;
                _ = LoadSvcHostGroupsAsync();
            }
        }
    }

    private ServiceRow? _selectedService;
    public ServiceRow? SelectedService
    {
        get => _selectedService;
        set
        {
            var previous = _selectedService;
            if (SetProperty(ref _selectedService, value) && previous is not null)
            {
                previous.FailureActionsText = string.Empty; // stale text from a previous selection would be misleading
                previous.TriggerInfoText = string.Empty; // #756: same "stale text is misleading" reasoning
                previous.HangDiagnosisText = string.Empty; // #763: same "stale text is misleading" reasoning
                previous.IsHung = false;
            }
        }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ServicesView.Refresh();
        }
    }

    private bool _failedToStartOnly;
    public bool FailedToStartOnly
    {
        get => _failedToStartOnly;
        set
        {
            if (SetProperty(ref _failedToStartOnly, value))
                ServicesView.Refresh();
        }
    }

    /// <summary>#750: filters to rows ServiceRow.IsCrashLooping flags - populated only after
    /// LoadFailureHistoryCommand has run at least once (before that, no row is ever crash-looping).</summary>
    private bool _crashLoopingOnly;
    public bool CrashLoopingOnly
    {
        get => _crashLoopingOnly;
        set
        {
            if (SetProperty(ref _crashLoopingOnly, value))
                ServicesView.Refresh();
        }
    }

    /// <summary>#753: filters to rows ServiceRow.IsOrphaned flags - populated only after
    /// RunInventoryAuditCommand has run at least once.</summary>
    private bool _orphanedOnly;
    public bool OrphanedOnly
    {
        get => _orphanedOnly;
        set
        {
            if (SetProperty(ref _orphanedOnly, value))
                ServicesView.Refresh();
        }
    }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    #region #757 - Bulk recovery-action audit

    /// <summary>#757: results grid, populated only after RunRecoveryAuditCommand runs - see
    /// ServiceControlService.RunRecoveryActionAuditAsync.</summary>
    public ObservableCollection<ServiceRecoveryAuditEntry> RecoveryAuditResults { get; } = new();

    private double _recoveryAuditProgress;
    /// <summary>0-100 - driven by RunRecoveryActionAuditAsync's IProgress callback, which
    /// Progress&lt;T&gt; automatically marshals back onto this (UI) thread since it's constructed
    /// here.</summary>
    public double RecoveryAuditProgress { get => _recoveryAuditProgress; private set => SetProperty(ref _recoveryAuditProgress, value); }

    private bool _isRunningRecoveryAudit;
    public bool IsRunningRecoveryAudit { get => _isRunningRecoveryAudit; private set => SetProperty(ref _isRunningRecoveryAudit, value); }

    public RelayCommand RunRecoveryAuditCommand { get; }

    /// <summary>Jumps the main grid/filter/selection to one audit result's service - a quick way to
    /// go from "here's an outlier" to the row that lets you act on it (recovery-actions form,
    /// delete, etc).</summary>
    public RelayCommand JumpToRecoveryAuditServiceCommand { get; }

    #endregion

    #region #758 - Editable recovery actions

    /// <summary>#758: options for each of the three failure-order dropdowns - "None" is represented
    /// internally as an empty sc.exe action token (see ServiceControlService.SetFailureActionsAsync's
    /// remarks), never the literal word "none" (sc.exe doesn't recognize that as an action keyword).</summary>
    public static string[] RecoveryActionOptions { get; } = { "None", "Restart the service", "Restart the computer" };

    private string _recoveryFirstFailureAction = "Restart the service";
    public string RecoveryFirstFailureAction { get => _recoveryFirstFailureAction; set => SetProperty(ref _recoveryFirstFailureAction, value); }

    private string _recoverySecondFailureAction = "Restart the service";
    public string RecoverySecondFailureAction { get => _recoverySecondFailureAction; set => SetProperty(ref _recoverySecondFailureAction, value); }

    private string _recoverySubsequentFailureAction = "None";
    public string RecoverySubsequentFailureAction { get => _recoverySubsequentFailureAction; set => SetProperty(ref _recoverySubsequentFailureAction, value); }

    private string _recoveryResetPeriodDays = "1";
    public string RecoveryResetPeriodDays { get => _recoveryResetPeriodDays; set => SetProperty(ref _recoveryResetPeriodDays, value); }

    private string _recoveryRestartDelaySeconds = "60";
    public string RecoveryRestartDelaySeconds { get => _recoveryRestartDelaySeconds; set => SetProperty(ref _recoveryRestartDelaySeconds, value); }

    public RelayCommand SaveRecoveryActionsCommand { get; }

    #endregion

    #region #759/#760 - Start-type change audit and new-install log

    /// <summary>#759: "Recent configuration changes" list, alongside the existing snapshot-drift
    /// detection above (CaptureConfigBaselineCommand/CheckConfigDriftCommand) - a second, independent
    /// way to see the same kind of change, sourced from the System log's own event 7040 rather than a
    /// user-captured baseline.</summary>
    public ObservableCollection<ServiceStartTypeChangeEvent> RecentConfigChanges { get; } = new();
    public RelayCommand LoadStartTypeChangesCommand { get; }

    /// <summary>#760: "New since &lt;date&gt;" list - services/drivers installed within the lookback
    /// window (event 7045), correlated against SignatureCheckService for signer status.</summary>
    public ObservableCollection<NewServiceInstallEvent> NewServiceInstalls { get; } = new();
    public RelayCommand LoadNewServiceInstallsCommand { get; }

    #endregion

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand RefreshNowCommand { get; }
    public RelayCommand ViewFailureActionsCommand { get; }

    /// <summary>Round 7 #13: mines Service Control Manager 7036 events for approximate per-service
    /// start durations - a single on-demand pass merged onto whatever rows are already loaded,
    /// the same on-demand shape StabilityViewModel already uses for its own event-log query. #193
    /// extends this same pass/text with the start-timeout (7009) failure case 7036 alone can't
    /// represent - see LoadStartDurationsAsync.</summary>
    public RelayCommand LoadStartDurationsCommand { get; }

    /// <summary>#192: correlates SCM 7000/7009/7024/7031/7034 per service and badges every service
    /// this scan flags as crash-looping, plus builds CrashLoopSummary below.</summary>
    public AsyncRelayCommand ScanCrashLoopsCommand { get; }

    /// <summary>Round 7 #16: capture the current StartType/logon-account config as a baseline, or
    /// compare against a previously saved one - reuses SnapshotService/SystemSnapshot from Round 6
    /// rather than a second baseline file format.</summary>
    // #486: SnapshotService.Capture became CaptureAsync (driver inventory/driver store
    // enumeration added a few seconds of shell-out work) - AsyncRelayCommand rather than
    // RelayCommand for the same reason SummaryViewModel's SaveSnapshotCommand is.
    public AsyncRelayCommand CaptureConfigBaselineCommand { get; }
    public RelayCommand CheckConfigDriftCommand { get; }

    /// <summary>#749/#750/#751: one shared SCM-event scan, merged onto every loaded row three ways -
    /// see ApplyFailureHistory.</summary>
    public RelayCommand LoadFailureHistoryCommand { get; }

    /// <summary>#752/#753/#754: one shared registry pass over HKLM\...\Services, merged onto every
    /// loaded row three ways - see ServiceControlService.RunInventoryAudit.</summary>
    public RelayCommand RunInventoryAuditCommand { get; }

    /// <summary>#753: `sc delete` for the selected row, confirmed first - only enabled once an audit
    /// has flagged it as orphaned.</summary>
    public RelayCommand DeleteOrphanedServiceCommand { get; }

    /// <summary>#756: `sc qtriggerinfo` for the selected row - same on-demand cadence as
    /// ViewFailureActionsCommand.</summary>
    public RelayCommand ViewTriggerInfoCommand { get; }

    /// <summary>#763: `sc queryex` hang diagnosis for the selected row, and a confirmed force-kill of
    /// its host process once diagnosed.</summary>
    public RelayCommand DiagnoseHangCommand { get; }
    public RelayCommand ForceKillHostProcessCommand { get; }

    private string? _startDurationStatus;
    public string? StartDurationStatus { get => _startDurationStatus; set => SetProperty(ref _startDurationStatus, value); }

    /// <summary>#763: when each currently-pending (START_PENDING/STOP_PENDING) row first became
    /// pending, tracked live off MergeInto's per-tick status transitions - never fabricated for a
    /// service that was already pending before this app started watching it (DiagnoseHangAsync then
    /// reports "an unknown duration" instead of guessing).</summary>
    private readonly Dictionary<string, DateTime> _pendingSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>#192: the summary card - only the services this scan actually flags as crash-
    /// looping (ServiceCrashLoopInfo.IsCrashLooping), each enriched with its `sc qfailure` recovery
    /// settings once flagged (see ScanCrashLoopsAsync). Empty until the scan has run.</summary>
    public ObservableCollection<ServiceCrashLoopInfo> CrashLoopSummary { get; } = new();

    private string? _crashLoopStatusText;
    public string? CrashLoopStatusText { get => _crashLoopStatusText; set => SetProperty(ref _crashLoopStatusText, value); }

    /// <summary>#193: what ServicesPipeTimeout is currently set to (or that it's unset, meaning
    /// Windows' 30000ms default applies) plus a plain-English explanation of what it governs -
    /// populated alongside the start-duration scan since both come from the same "why did this
    /// service start slowly/fail to start" question.</summary>
    private string? _servicesPipeTimeoutText;
    public string? ServicesPipeTimeoutText { get => _servicesPipeTimeoutText; set => SetProperty(ref _servicesPipeTimeoutText, value); }

    public ServicesViewModel()
    {
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterPredicate;

        StartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Start, "started"), _ => CanStart());
        StopCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Stop, "stopped"), _ => CanStop());
        RestartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Restart, "restarted"), _ => CanStop());
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshAsync());
        ViewFailureActionsCommand = new RelayCommand(_ => _ = LoadFailureActionsAsync(), _ => SelectedService is not null);
        LoadStartDurationsCommand = new RelayCommand(_ => _ = LoadStartDurationsAsync());
        ScanCrashLoopsCommand = new AsyncRelayCommand(ScanCrashLoopsAsync);
        CaptureConfigBaselineCommand = new AsyncRelayCommand(CaptureConfigBaselineAsync);
        CheckConfigDriftCommand = new RelayCommand(_ => CheckConfigDrift());
        LoadFailureHistoryCommand = new RelayCommand(_ => _ = LoadFailureHistoryAsync());
        RunInventoryAuditCommand = new RelayCommand(_ => _ = RunInventoryAuditAsync());
        // #1073: the three destructive commands below carry the same !ReadOnlyModeService.IsReadOnly
        // gate as Start/Stop/Restart (CanStart/CanStop) - read-only mode must not be able to delete
        // a service, rewrite recovery actions, or kill a shared host process.
        DeleteOrphanedServiceCommand = new RelayCommand(_ => _ = DeleteOrphanedServiceAsync(), _ => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { IsOrphaned: true });
        ViewTriggerInfoCommand = new RelayCommand(_ => _ = LoadTriggerInfoAsync(), _ => SelectedService is not null);

        RunRecoveryAuditCommand = new RelayCommand(_ => _ = RunRecoveryAuditAsync(), _ => !IsRunningRecoveryAudit);
        JumpToRecoveryAuditServiceCommand = new RelayCommand(param => JumpToRecoveryAuditService(param as ServiceRecoveryAuditEntry));
        SaveRecoveryActionsCommand = new RelayCommand(_ => _ = SaveRecoveryActionsAsync(),
            _ => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { } s && !ServiceControlService.IsProtectedCoreService(s.ServiceName));

        LoadStartTypeChangesCommand = new RelayCommand(_ => _ = LoadStartTypeChangesAsync());
        LoadNewServiceInstallsCommand = new RelayCommand(_ => _ = LoadNewServiceInstallsAsync());

        DiagnoseHangCommand = new RelayCommand(_ => _ = DiagnoseHangAsync(), _ => !IsBusy && SelectedService is not null);
        ForceKillHostProcessCommand = new RelayCommand(_ => _ = ForceKillHostProcessAsync(), _ => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { ProcessId: > 0 });

        // Round 12, #100: configurable poll interval - see PollIntervalSettingsService's remarks.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(PollIntervalSettingsService.Load().ServicesSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    /// <summary>Round 12, #100: how often the Services tab refreshes - default unchanged (2s).
    /// Loaded fresh from disk on every change (never cached) so this tab's slider can't clobber
    /// another tab's own saved interval in the same shared JSON file.</summary>
    public double PollIntervalSeconds
    {
        get => _timer.Interval.TotalSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 10.0);
            if (Math.Abs(_timer.Interval.TotalSeconds - clamped) < 0.01) return;

            _timer.Interval = TimeSpan.FromSeconds(clamped);
            var settings = PollIntervalSettingsService.Load();
            settings.ServicesSeconds = clamped;
            PollIntervalSettingsService.Save(settings);
            OnPropertyChanged();
        }
    }

    // #980: read-only mode disables Start/Stop/Restart (mutating) but leaves everything else on
    // this tab (Refresh, failure-actions view, start-durations, config baseline/drift) working.
    private bool CanStart() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { CanStart: true };
    private bool CanStop() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { CanStop: true };

    private bool FilterPredicate(object obj)
    {
        if (obj is not ServiceRow row) return false;

        if (FailedToStartOnly && !row.HasFailedToStart) return false;
        if (CrashLoopingOnly && !row.IsCrashLooping) return false;
        if (OrphanedOnly && !row.IsOrphaned) return false;

        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return row.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               row.ServiceName.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var latest = await Task.Run(() => _service.Sample());
            MergeInto(latest);

            if (ShowDrivers)
                await RefreshDriversAsync();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void MergeInto(List<ServiceRow> latest)
    {
        var latestByName = latest.ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase);

        for (int i = Services.Count - 1; i >= 0; i--)
        {
            var existing = Services[i];
            if (latestByName.TryGetValue(existing.ServiceName, out var fresh))
            {
                // #763: track when this row first became pending, live off this per-tick status
                // transition - see _pendingSince's remarks.
                bool wasPending = existing.Status is ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending;
                bool isPending = fresh.Status is ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending;
                if (isPending && !wasPending) _pendingSince[existing.ServiceName] = DateTime.Now;
                else if (!isPending) _pendingSince.Remove(existing.ServiceName);

                existing.Status = fresh.Status;
                existing.StartType = fresh.StartType;
                existing.ProcessId = fresh.ProcessId;
                existing.ExitCode = fresh.ExitCode;
                existing.DependsOn = fresh.DependsOn;
                existing.DependentServices = fresh.DependentServices;
                existing.LogOnAs = fresh.LogOnAs;
                existing.IsDelayedAutoStart = fresh.IsDelayedAutoStart;
                existing.AutoStartDelaySeconds = fresh.AutoStartDelaySeconds;
                latestByName.Remove(existing.ServiceName);
            }
            else
            {
                _pendingSince.Remove(existing.ServiceName);
                Services.Remove(existing);
            }
        }

        foreach (var row in latestByName.Values)
            Services.Add(row);
    }

    private async Task RunAction(Func<string, (bool Success, string? Error)> action, string verbPast)
    {
        var target = SelectedService;
        if (target is null) return;

        string before = target.Status.ToString();
        IsBusy = true;
        try
        {
            var (success, error) = await Task.Run(() => action(target.ServiceName));
            StatusMessage = success
                ? $"{target.DisplayName} {verbPast}."
                : $"Couldn't control {target.DisplayName}: {error}";

            await RefreshAsync();

            // #972: record every mutation this app performs - after the refresh above so
            // AfterValue reflects the service's actual post-action status, not a stale
            // pre-refresh read (MergeInto updates `target` in place, so it's still the right row).
            ChangeJournalService.Append(new ChangeJournalEntry
            {
                Kind = ChangeKind.ServiceStateChange,
                Target = target.DisplayName,
                ActionDescription = $"Service {verbPast}",
                BeforeValue = before,
                AfterValue = target.Status.ToString(),
                TriggeredBy = "Services tab",
                Success = success,
                IsUndoable = success,
                ServiceName = target.ServiceName,
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Loads SelectedService's recovery-actions text (#71) on demand - see
    /// ServiceControlService.ReadFailureActionsTextAsync for why this shells to sc.exe rather than
    /// every tick.</summary>
    private async Task LoadFailureActionsAsync()
    {
        var target = SelectedService;
        if (target is null) return;

        target.FailureActionsText = await ServiceControlService.ReadFailureActionsTextAsync(target.ServiceName);
    }

    /// <summary>Round 7 #15: (re)samples the driver sub-view - only ever called while ShowDrivers
    /// is on (the toggle setter, and the main RefreshAsync tick once it's already on).</summary>
    private async Task RefreshDriversAsync()
    {
        try
        {
            var latest = await Task.Run(() => _service.SampleDrivers());
            var latestByName = latest.ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase);

            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                var existing = Drivers[i];
                if (latestByName.TryGetValue(existing.ServiceName, out var fresh))
                {
                    existing.Status = fresh.Status;
                    existing.StartType = fresh.StartType;
                    latestByName.Remove(existing.ServiceName);
                }
                else
                {
                    Drivers.Remove(existing);
                }
            }
            foreach (var row in latestByName.Values)
                Drivers.Add(row);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>Round 7 #13: merges approximate start-duration data onto whatever service rows are
    /// already loaded - see EventLogService.ReadServiceStartDurations for exactly what's measured.
    /// #193 extends this same pass with the one failure case 7036 alone can't represent: a service
    /// that never reached "running" at all, logged instead as SCM 7009 ("did not respond in a timely
    /// fashion"). ServicesPipeTimeoutText is populated here too since both answer the same "why did
    /// this service start slowly/time out" question.</summary>
    private async Task LoadStartDurationsAsync()
    {
        StartDurationStatus = "Scanning the System event log…";
        var durations = await Task.Run(() => _eventLog.ReadServiceStartDurations());
        var byName = durations.ToDictionary(d => d.ServiceName, StringComparer.OrdinalIgnoreCase);

        // #193: the 7009 start-timeout counts, from the same SCM event family #192's crash-loop
        // scan reads - a second small scan rather than folding 7009 into ReadServiceStartDurations
        // itself, since 7036's stopped/running-pair math and 7009's plain per-service count are two
        // genuinely different measurements over two different event IDs.
        var crashLoopScan = await Task.Run(() => _serviceHealth.ReadServiceCrashLoops());
        var timeouts = crashLoopScan.Where(c => c.TimeoutCount > 0).ToDictionary(c => c.ServiceName, StringComparer.OrdinalIgnoreCase);

        var timeoutInfo = ServiceHealthEventService.ReadServiceStartTimeout();
        ServicesPipeTimeoutText = timeoutInfo.IsCustomized
            ? $"ServicesPipeTimeout is customized: {timeoutInfo.EffectiveTimeoutMs}ms. This governs how long the Service Control Manager waits for a service to report it started before giving up and logging a 7009 - it does not affect how long a service itself takes to actually finish starting."
            : $"ServicesPipeTimeout isn't set - Windows' built-in default of {timeoutInfo.EffectiveTimeoutMs}ms applies. This governs how long the Service Control Manager waits for a service to report it started before giving up and logging a 7009 - it does not affect how long a service itself takes to actually finish starting.";

        int matched = 0;
        foreach (var row in Services)
        {
            string baseText;
            if (byName.TryGetValue(row.ServiceName, out var d))
            {
                baseText = $"~{d.LastStartDurationMs / 1000.0:0.0}s (avg {d.AvgStartDurationMs / 1000.0:0.0}s, {d.SampleCount} samples)";
                matched++;
            }
            else
            {
                baseText = "No recent data";
            }

            // #193: append the start-timeout failure case, which 7036 (a stopped/running state
            // transition) never logs at all for a service that timed out instead.
            if (timeouts.TryGetValue(row.ServiceName, out var timeoutHit))
            {
                string timeoutNote = $"{timeoutHit.TimeoutCount} start-timeout(s) (SCM 7009) in last 30 days";
                baseText = baseText == "No recent data" ? timeoutNote : $"{baseText} — {timeoutNote}";
                matched++;
            }

            row.StartDurationText = baseText;
        }

        StartDurationStatus = $"Found start-duration/timeout history for {matched} of {Services.Count} services (last {30} days, approximate - see tooltip).";
    }

    /// <summary>#192: correlates SCM 7000/7009/7024/7031/7034 per service, badges every service the
    /// scan flags as crash-looping (ServiceCrashLoopInfo.IsCrashLooping), and builds CrashLoopSummary
    /// with each flagged service's `sc qfailure` recovery settings loaded alongside (reusing
    /// ServiceControlService.ReadFailureActionsTextAsync - the same shell-out the "Recovery actions"
    /// button already uses - rather than a second sc.exe-parsing path).</summary>
    private async Task ScanCrashLoopsAsync()
    {
        CrashLoopStatusText = "Scanning the System event log for service crash loops…";
        var loops = await Task.Run(() => _serviceHealth.ReadServiceCrashLoops());
        var byName = loops.ToDictionary(l => l.ServiceName, StringComparer.OrdinalIgnoreCase);

        foreach (var row in Services)
        {
            if (byName.TryGetValue(row.ServiceName, out var info) && info.IsCrashLooping)
            {
                row.IsCrashLoopingFlagged = true;
                row.CrashLoopSummaryText = $"{info.TerminatedCount} unexpected termination(s), {info.TimeoutCount} start-timeout(s), {info.FailedToStartCount} failed start(s) in the last 30 days.";
            }
            else
            {
                row.IsCrashLoopingFlagged = false;
                row.CrashLoopSummaryText = string.Empty;
            }
        }

        var flagged = loops.Where(l => l.IsCrashLooping).OrderByDescending(l => l.TotalCount).ToList();
        foreach (var info in flagged)
            info.RecoveryActionsText = await ServiceControlService.ReadFailureActionsTextAsync(info.ServiceName);

        CrashLoopSummary.Clear();
        foreach (var info in flagged) CrashLoopSummary.Add(info);

        CrashLoopStatusText = flagged.Count == 0
            ? "No services show repeated crashes/restarts in the last 30 days."
            : $"{flagged.Count} service(s) flagged as crash-looping in the last 30 days - quick flag, not a verdict.";
    }

    /// <summary>Round 7 #16: saves the current StartType/logon-account config as a JSON baseline -
    /// reuses SnapshotService/SystemSnapshot, the same file a user might otherwise capture from the
    /// Summary tab's "Save snapshot" button (that capture now includes ServiceConfigs too, and,
    /// since #486, driver inventory/driver store contents).</summary>
    private async Task CaptureConfigBaselineAsync()
    {
        var snapshotsDir = AppPaths.GetPath("Snapshots");
        try { Directory.CreateDirectory(snapshotsDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            Title = "Save service configuration baseline",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"TaskManagerPlus-ServiceBaseline-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json",
            InitialDirectory = snapshotsDir,
        };
        if (dialog.ShowDialog() != true) return;

        StatusMessage = "Capturing baseline...";
        try
        {
            SnapshotService.Save(await SnapshotService.CaptureAsync(), dialog.FileName);
            StatusMessage = $"Baseline saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save baseline: {ex.Message}";
        }
    }

    /// <summary>Round 7 #16: compares each currently-loaded service's StartType/LogOnAs against a
    /// saved baseline, flagging any row where either changed. A service present now but missing
    /// from the baseline (installed since) or vice versa is left alone here - that's exactly what
    /// the Summary tab's existing added/removed service diff already covers.</summary>
    private void CheckConfigDrift()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Check service configuration drift against a saved baseline",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Snapshots"),
        };
        if (dialog.ShowDialog() != true) return;

        var baseline = SnapshotService.Load(dialog.FileName);
        if (baseline is null)
        {
            StatusMessage = "Couldn't read that baseline file.";
            return;
        }

        var baselineByName = baseline.ServiceConfigs.ToDictionary(c => c.ServiceName, StringComparer.OrdinalIgnoreCase);
        int drifted = 0, compared = 0;
        foreach (var row in Services)
        {
            if (!baselineByName.TryGetValue(row.ServiceName, out var baselineConfig))
            {
                row.HasConfigDrift = false;
                row.ConfigDriftText = string.Empty;
                continue;
            }

            compared++;
            var changes = new List<string>();
            if (!string.Equals(baselineConfig.StartType, row.StartType.ToString(), StringComparison.OrdinalIgnoreCase))
                changes.Add($"StartType {baselineConfig.StartType} -> {row.StartType}");
            if (!string.Equals(baselineConfig.LogOnAs, row.LogOnAs, StringComparison.OrdinalIgnoreCase))
                changes.Add($"Account {baselineConfig.LogOnAs} -> {row.LogOnAs}");

            row.HasConfigDrift = changes.Count > 0;
            row.ConfigDriftText = string.Join("; ", changes);
            if (row.HasConfigDrift) drifted++;
        }

        StatusMessage = $"Compared {compared} services against baseline from {baseline.CapturedAt:g} - {drifted} changed.";
    }

    /// <summary>#749/#750/#751: runs the one shared SCM failure-event scan and merges it onto every
    /// currently-loaded row.</summary>
    private async Task LoadFailureHistoryAsync()
    {
        StatusMessage = "Scanning the System event log for service failures…";
        var events = await Task.Run(() => _eventLog.ReadServiceFailureEvents());
        ApplyFailureHistory(events);

        int crashLooping = Services.Count(r => r.IsCrashLooping);
        StatusMessage = $"Loaded {events.Count} SCM failure event(s) from the last 30 days - {crashLooping} service(s) crash-looping.";
    }

    /// <summary>#749/#750/#751: merges one shared failure-event scan onto every currently-loaded row
    /// three ways - the raw per-service timeline (#749), a rolling-24h crash-loop count (#750), and
    /// (once every row's own events are assigned, so the walk below has something to look up) a
    /// dependency-failure root-cause walk (#751). Matched onto rows by display name, since that's
    /// what SCM's own formatted event messages embed - see EventLogService.ReadServiceFailureEvents.</summary>
    private void ApplyFailureHistory(List<ServiceScmEvent> events)
    {
        var byDisplayName = new Dictionary<string, List<ServiceScmEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (!byDisplayName.TryGetValue(e.ServiceDisplayName, out var list))
                byDisplayName[e.ServiceDisplayName] = list = new List<ServiceScmEvent>();
            list.Add(e);
        }

        var cutoff = DateTime.Now.AddHours(-24);
        foreach (var row in Services)
        {
            if (byDisplayName.TryGetValue(row.DisplayName, out var rowEvents))
            {
                row.ScmEvents = rowEvents.OrderByDescending(e => e.TimeCreated).ToList();
                row.CrashLoopCount24H = rowEvents.Count(e => e.IsCrashEvent && e.TimeCreated >= cutoff);
            }
            else
            {
                row.ScmEvents = Array.Empty<ServiceScmEvent>();
                row.CrashLoopCount24H = 0;
            }
        }

        // #1034: duplicate-safe, like byDisplayName above - ToDictionary throws ArgumentException
        // on a case-insensitive display-name collision (registry-written or localized names), and
        // that exception vanishes into LoadFailureHistoryAsync's discarded task, sticking
        // StatusMessage at "Scanning…" forever. First row wins on a collision.
        var rowsByDisplayName = new Dictionary<string, ServiceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Services)
            rowsByDisplayName.TryAdd(row.DisplayName, row);
        foreach (var row in Services)
            row.DependencyRootCauseText = BuildRootCause(row, rowsByDisplayName);
    }

    /// <summary>#751: walks DependsOn (display names, already loaded every tick - #37) one hop at a
    /// time, following whichever dependency itself has failure history, until one is found with a
    /// real failure of its own (not just a 7001 "a dependency of mine failed" event, which only
    /// means the chain continues one hop further) or the chain runs out. Cycle-guarded since a
    /// dependency graph isn't guaranteed acyclic in general.</summary>
    private static string BuildRootCause(ServiceRow row, Dictionary<string, ServiceRow> rowsByDisplayName)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.DisplayName };
        var current = row;

        while (true)
        {
            var ownFailure = current.ScmEvents
                .Where(e => e.EventId != 7001)
                .OrderByDescending(e => e.TimeCreated)
                .FirstOrDefault();

            if (ownFailure is not null)
            {
                return chain.Count == 0
                    ? $"Failed directly ({ownFailure.EventLabel}, {ownFailure.TimeCreated:g}): {ownFailure.Message}"
                    : $"Blocked by: {string.Join(" -> ", chain)} -> {current.DisplayName}, which failed directly " +
                      $"({ownFailure.EventLabel}, {ownFailure.TimeCreated:g}): {ownFailure.Message}";
            }

            var next = current.DependsOn
                .Select(name => rowsByDisplayName.TryGetValue(name, out var r) ? r : null)
                .FirstOrDefault(r => r is not null && r.ScmEvents.Count > 0 && !visited.Contains(r.DisplayName));

            if (next is null)
            {
                return current.ScmEvents.Any(e => e.EventId == 7001)
                    ? "Depends on a service reported as failed, but that dependency's own failure record wasn't found in the last 30 days."
                    : string.Empty; // no failure history for this service or its dependencies - nothing to report
            }

            chain.Add(current.DisplayName);
            visited.Add(next.DisplayName);
            current = next;
        }
    }

    /// <summary>#752/#753/#754: runs the one shared registry-inventory audit and merges its flags
    /// onto every currently-loaded row (including the driver sub-view, since drivers are entries
    /// under the same registry key and can be just as orphaned/unquoted/broken).</summary>
    private async Task RunInventoryAuditAsync()
    {
        StatusMessage = "Auditing service registry entries…";
        var flags = await Task.Run(ServiceControlService.RunInventoryAudit);
        var byName = flags.ToDictionary(f => f.ServiceName, StringComparer.OrdinalIgnoreCase);

        int orphaned = 0, unquoted = 0, broken = 0;

        void Apply(ServiceRow row)
        {
            if (byName.TryGetValue(row.ServiceName, out var f))
            {
                row.HasBrokenDependency = f.HasBrokenDependency;
                row.BrokenDependencyText = f.BrokenDependencyText;
                row.IsOrphaned = f.IsOrphaned;
                row.OrphanedImagePath = f.OrphanedImagePath;
                row.HasUnquotedPath = f.HasUnquotedPath;
                row.UnquotedPathOriginal = f.UnquotedPathOriginal;
                row.UnquotedPathCorrected = f.UnquotedPathCorrected;
            }
            else
            {
                row.HasBrokenDependency = false;
                row.BrokenDependencyText = string.Empty;
                row.IsOrphaned = false;
                row.OrphanedImagePath = string.Empty;
                row.HasUnquotedPath = false;
                row.UnquotedPathOriginal = string.Empty;
                row.UnquotedPathCorrected = string.Empty;
            }

            if (row.IsOrphaned) orphaned++;
            if (row.HasUnquotedPath) unquoted++;
            if (row.HasBrokenDependency) broken++;
        }

        foreach (var row in Services) Apply(row);
        foreach (var row in Drivers) Apply(row);

        StatusMessage = $"Service audit: {orphaned} orphaned, {unquoted} unquoted path, {broken} broken dependency (of {flags.Count} flagged registry entries).";
    }

    /// <summary>#753: `sc delete` for the selected orphaned row - confirmed first, matching
    /// CLAUDE.md's "mutating actions require explicit confirmation" rule (the same pattern
    /// ProcessesViewModel.EndSelected and StartupViewModel's destructive actions already use).</summary>
    private async Task DeleteOrphanedServiceAsync()
    {
        var target = SelectedService;
        if (target is null || !target.IsOrphaned) return;

        var confirm = MessageBox.Show(
            $"This will run:\n\nsc delete \"{target.ServiceName}\"\n\n\"{target.DisplayName}\" points at a binary that no longer exists:\n{target.OrphanedImagePath}\n\nThis permanently removes the service registration. Continue?",
            "Delete orphaned service",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var (success, error) = await ServiceControlService.DeleteAsync(target.ServiceName);
            StatusMessage = success ? $"Deleted {target.DisplayName}." : $"Couldn't delete {target.DisplayName}: {error}";
            if (success) Services.Remove(target);

            // #1073: record the mutation, like every other system-mutating action (see RunAction).
            ChangeJournalService.Append(new ChangeJournalEntry
            {
                Kind = ChangeKind.ServiceStateChange,
                Target = target.DisplayName,
                ActionDescription = "Deleted orphaned service (sc delete)",
                BeforeValue = target.OrphanedImagePath,
                AfterValue = "Service registration removed",
                TriggeredBy = "Services tab",
                Success = success,
                IsUndoable = false,
                NotUndoableReason = "The service registration was permanently removed - reinstall the service to restore it.",
                ServiceName = target.ServiceName,
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>#756: loads SelectedService's trigger-start conditions on demand - see
    /// ServiceControlService.ReadTriggerInfoTextAsync for why this shells to sc.exe rather than
    /// every tick.</summary>
    private async Task LoadTriggerInfoAsync()
    {
        var target = SelectedService;
        if (target is null) return;

        target.TriggerInfoText = await ServiceControlService.ReadTriggerInfoTextAsync(target.ServiceName);
    }

    #region #757 - Bulk recovery-action audit

    /// <summary>#757: runs ServiceControlService.RunRecoveryActionAuditAsync across every
    /// currently-loaded row, reporting progress into RecoveryAuditProgress.</summary>
    private async Task RunRecoveryAuditAsync()
    {
        IsRunningRecoveryAudit = true;
        RecoveryAuditProgress = 0;
        StatusMessage = "Auditing recovery actions across all services (this shells out to sc.exe once per service)…";
        try
        {
            var services = Services.Select(r => (r.ServiceName, IsAutomaticStart: r.StartType == ServiceStartMode.Automatic)).ToList();
            var progress = new Progress<(int Done, int Total)>(p =>
                RecoveryAuditProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total);

            var results = await ServiceControlService.RunRecoveryActionAuditAsync(services, progress);

            RecoveryAuditResults.Clear();
            foreach (var r in results) RecoveryAuditResults.Add(r);

            StatusMessage = $"Recovery action audit: {results.Count} outlier(s) of {services.Count} services checked.";
        }
        finally
        {
            IsRunningRecoveryAudit = false;
            RecoveryAuditProgress = 100;
        }
    }

    /// <summary>Jumps FilterText/SelectedService to one audit result's row, so acting on an outlier
    /// (e.g. opening the #758 recovery-actions form) is a single click away from finding it.</summary>
    private void JumpToRecoveryAuditService(ServiceRecoveryAuditEntry? entry)
    {
        if (entry is null) return;
        FilterText = entry.ServiceName;
        var match = Services.FirstOrDefault(r => r.ServiceName.Equals(entry.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SelectedService = match;
    }

    #endregion

    #region #758 - Editable recovery actions

    /// <summary>
    /// #758: builds the exact `sc failure` command from the form fields, shows it in a confirmation
    /// dialog (CLAUDE.md's "mutating actions require confirmation with the exact command shown"),
    /// then runs that same string. Declines outright for a protected core service - the command's
    /// own CanExecute already keeps the button disabled for one, this is defense in depth matching
    /// SetFailureActionsAsync's own server-side check.
    /// </summary>
    private async Task SaveRecoveryActionsAsync()
    {
        var target = SelectedService;
        if (target is null) return;
        if (ServiceControlService.IsProtectedCoreService(target.ServiceName))
        {
            StatusMessage = $"{target.DisplayName} is a protected core service - its recovery actions can't be edited here.";
            return;
        }

        if (!int.TryParse(RecoveryResetPeriodDays, out int resetDays) || resetDays < 0)
        {
            StatusMessage = "Enter a reset period of 0 or more days.";
            return;
        }
        if (!int.TryParse(RecoveryRestartDelaySeconds, out int delaySeconds) || delaySeconds < 0)
        {
            StatusMessage = "Enter a restart delay of 0 or more seconds.";
            return;
        }

        string ActionToken(string action) => action switch
        {
            "Restart the service" => "restart",
            "Restart the computer" => "reboot",
            _ => string.Empty, // "None" - sc.exe has no "none" keyword; an empty slot is SC_ACTION_NONE
        };

        string BuildPair(string action)
        {
            string token = ActionToken(action);
            int delayMs = token.Length == 0 ? 0 : delaySeconds * 1000;
            return $"{token}/{delayMs}";
        }

        string actionsArg = string.Join("/", new[]
        {
            BuildPair(RecoveryFirstFailureAction),
            BuildPair(RecoverySecondFailureAction),
            BuildPair(RecoverySubsequentFailureAction),
        });
        long resetSeconds = resetDays * 86400L;
        string args = $"failure \"{target.ServiceName}\" reset= {resetSeconds} actions= {actionsArg}";

        var confirm = MessageBox.Show(
            $"This will run:\n\nsc.exe {args}\n\nChange recovery actions for \"{target.DisplayName}\" now?",
            "Change recovery actions",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var (success, error) = await ServiceControlService.SetFailureActionsAsync(target.ServiceName, args);
            StatusMessage = success
                ? $"Recovery actions updated for {target.DisplayName}."
                : $"Couldn't update recovery actions for {target.DisplayName}: {error}";

            // #1073: record the mutation, like every other system-mutating action (see RunAction).
            // The prior recovery actions aren't captured in a machine-reversible form, so this is
            // journaled as a one-shot run of the exact command shown in the confirmation dialog.
            ChangeJournalService.Append(new ChangeJournalEntry
            {
                Kind = ChangeKind.OneShotToolRun,
                Target = target.DisplayName,
                ActionDescription = "Changed service recovery actions",
                AfterValue = $"sc.exe {args}",
                TriggeredBy = "Services tab",
                Success = success,
                IsUndoable = false,
                NotUndoableReason = "The prior recovery actions weren't recorded - set them again from this form to change them back.",
                ServiceName = target.ServiceName,
            });

            // Refresh the on-demand "Recovery actions" text (if it was already loaded) so the
            // confirmed change is visible immediately, without a second manual click.
            if (success && target.FailureActionsText.Length > 0)
                target.FailureActionsText = await ServiceControlService.ReadFailureActionsTextAsync(target.ServiceName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    #region #759/#760 - Start-type change audit and new-install log

    /// <summary>#759: mines System-log event 7040 for start-type changes within the lookback window.</summary>
    private async Task LoadStartTypeChangesAsync()
    {
        StatusMessage = "Scanning the System event log for service start-type changes…";
        var events = await Task.Run(() => _eventLog.ReadStartTypeChangeEvents());
        RecentConfigChanges.Clear();
        foreach (var e in events) RecentConfigChanges.Add(e);
        StatusMessage = $"Found {events.Count} start-type change(s) in the last 30 days.";
    }

    /// <summary>#760: mines System-log event 7045 for services/drivers installed within the lookback
    /// window, then correlates each against SignatureCheckService for signer status - the signature
    /// check reads the file from disk, so this stays off the UI thread alongside the event scan.</summary>
    private async Task LoadNewServiceInstallsAsync()
    {
        StatusMessage = "Scanning the System event log for newly installed services…";
        var events = await Task.Run(() =>
        {
            var found = _eventLog.ReadNewServiceInstallEvents();
            foreach (var e in found)
                e.SignatureStatus = SignatureCheckService.GetStatus(StartupManagerService.ExtractPath(e.ImagePath));
            return found;
        });

        NewServiceInstalls.Clear();
        foreach (var e in events) NewServiceInstalls.Add(e);
        StatusMessage = $"Found {events.Count} newly installed service(s)/driver(s) in the last 30 days.";
    }

    #endregion

    #region #761/#762 - svchost group breakdown

    /// <summary>#761/#762: reads the svchost group breakdown and split-threshold info together -
    /// both are cheap registry/WMI-free reads plus a handful of Process snapshots, so one Task.Run
    /// covers both rather than two round trips.</summary>
    private async Task LoadSvcHostGroupsAsync()
    {
        StatusMessage = "Reading svchost group breakdown…";
        var (groups, splitInfo) = await Task.Run(() =>
            (ServiceControlService.ReadSvchostGroups(), ServiceControlService.ReadSvcHostSplitInfo()));

        SvcHostGroups.Clear();
        foreach (var g in groups) SvcHostGroups.Add(g);
        SvcHostSplitInfo = splitInfo;

        StatusMessage = $"Loaded {groups.Count} svchost group(s) across {groups.Sum(g => g.ProcessCount)} process(es).";
    }

    #endregion

    #region #763 - Hung-service diagnosis

    /// <summary>#763: samples SelectedService's `sc queryex` state twice (~3s apart) to tell "still
    /// making progress" from "genuinely stuck" - see ServiceControlService.DiagnoseHangAsync.</summary>
    private async Task DiagnoseHangAsync()
    {
        var target = SelectedService;
        if (target is null) return;

        IsBusy = true;
        StatusMessage = $"Diagnosing {target.DisplayName} - sampling its checkpoint over ~3 seconds…";
        try
        {
            _pendingSince.TryGetValue(target.ServiceName, out var since);
            TimeSpan? pendingDuration = since == default ? null : DateTime.Now - since;

            var diagnosis = await ServiceControlService.DiagnoseHangAsync(target.ServiceName, pendingDuration);
            target.IsHung = diagnosis.IsPending && !diagnosis.CheckpointAdvancing;
            target.HangDiagnosisText = diagnosis.SummaryText;
            StatusMessage = diagnosis.SummaryText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>#763: force-ends the process hosting SelectedService - confirmed first, matching
    /// CLAUDE.md's "mutating actions require confirmation" rule and ProcessesViewModel.EndSelected's
    /// own phrasing for the same underlying action.</summary>
    private async Task ForceKillHostProcessAsync()
    {
        var target = SelectedService;
        if (target is null || target.ProcessId <= 0) return;

        var confirm = MessageBox.Show(
            $"This will forcibly end the process hosting \"{target.DisplayName}\" (PID {target.ProcessId}).\n\n" +
            "Any unsaved data in that process, and any other services it hosts, will be lost. Continue?",
            "Force-end host process",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var (success, error) = await Task.Run(() => ProcessMonitorService.EndProcess(target.ProcessId));
            StatusMessage = success
                ? $"Ended host process (PID {target.ProcessId})."
                : $"Couldn't end host process: {error}";

            // #1073: record the mutation, like every other system-mutating action (see RunAction).
            ChangeJournalService.Append(new ChangeJournalEntry
            {
                Kind = ChangeKind.OneShotToolRun,
                Target = $"{target.DisplayName} (host PID {target.ProcessId})",
                ActionDescription = "Force-ended service host process",
                TriggeredBy = "Services tab",
                Success = success,
                IsUndoable = false,
                NotUndoableReason = "The host process was forcibly ended - start the affected service(s) again to recover.",
                ServiceName = target.ServiceName,
                Pid = target.ProcessId,
            });

            if (success) await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    public void Dispose() => _timer.Stop();
}
