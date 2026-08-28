using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class StartupViewModel : ObservableObject
{
    private readonly StartupManagerService _service = new();

    public ObservableCollection<StartupItem> Items { get; } = new();

    private StartupItem? _selectedItem;
    public StartupItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }

    // #79: Scheduled Tasks - a huge, often-overlooked source of background slowdowns and
    // unwanted auto-launches the registry-Run/Startup-folder scan above doesn't cover at all.
    // Loaded on demand (a "Load scheduled tasks" button) rather than up front - enumerating every
    // registered task can take a couple of seconds on a system with hundreds of them, the same
    // "expensive, so make it explicit" tradeoff as the Stability tab's event-log query.
    public ObservableCollection<ScheduledTaskRow> ScheduledTasks { get; } = new();

    private bool _isLoadingScheduledTasks;
    public bool IsLoadingScheduledTasks { get => _isLoadingScheduledTasks; private set => SetProperty(ref _isLoadingScheduledTasks, value); }

    private ScheduledTaskRow? _selectedScheduledTask;
    public ScheduledTaskRow? SelectedScheduledTask
    {
        get => _selectedScheduledTask;
        set
        {
            var previous = _selectedScheduledTask;
            if (SetProperty(ref _selectedScheduledTask, value) && previous is not null)
            {
                // Stale delay/run-mode text from a previous selection would be misleading.
                previous.DelayText = string.Empty;
                previous.RunModeText = string.Empty;
            }
        }
    }

    public AsyncRelayCommand LoadScheduledTasksCommand { get; }
    public AsyncRelayCommand ToggleScheduledTaskCommand { get; }
    public AsyncRelayCommand CheckLogonDelayCommand { get; }

    // #19/#20: browser extension inventory and registered shell extensions - both loaded on
    // demand (a couple of hundred filesystem/registry reads apiece is more I/O than this tab's
    // live-polled sections do), the same "expensive, so make it explicit" tradeoff as Scheduled
    // Tasks above.
    public ObservableCollection<BrowserExtensionInfo> BrowserExtensions { get; } = new();
    private bool _isLoadingBrowserExtensions;
    public bool IsLoadingBrowserExtensions { get => _isLoadingBrowserExtensions; private set => SetProperty(ref _isLoadingBrowserExtensions, value); }
    public AsyncRelayCommand LoadBrowserExtensionsCommand { get; }

    public ObservableCollection<ShellExtensionInfo> ShellExtensions { get; } = new();
    private bool _isLoadingShellExtensions;
    public bool IsLoadingShellExtensions { get => _isLoadingShellExtensions; private set => SetProperty(ref _isLoadingShellExtensions, value); }
    public AsyncRelayCommand LoadShellExtensionsCommand { get; }

    // #89/#90: boot time breakdown for the most recent boot, plus a small self-recorded trend
    // across sessions - see BootPerformanceService's remarks on why the breakdown is an adaptive
    // field list rather than fixed named properties.
    private BootTimeBreakdown? _bootBreakdown;
    public BootTimeBreakdown? BootBreakdown { get => _bootBreakdown; private set => SetProperty(ref _bootBreakdown, value); }

    public ObservableCollection<double> BootHistoryMs { get; } = new();
    private readonly LineSeries<double> _bootHistoryLine;
    public ISeries[] BootHistorySeries { get; }
    public Axis[] BootHistoryXAxes { get; }
    public Axis[] BootHistoryYAxes { get; }

    // #701: "what slowed this boot down" - the 101/102/103/106/109/110 degradation-family rows
    // that belong to the most recent boot only (see BootPerformanceService.FilterForBoot).
    public ObservableCollection<BootDegradationEvent> ThisBootDegradations { get; } = new();

    // #702: slow-boot culprit board, ranked by summed degradation time across every boot the
    // Diagnostics-Performance channel still retains.
    public ObservableCollection<BootCulprit> SlowBootCulprits { get; } = new();

    // #704: firmware POST time from the ACPI FPDT - see FirmwareBootTime's remarks for why this
    // realistically shows "Unknown" on most systems, and why that's still an honest result.
    private FirmwareBootTime? _firmwareBootTime;
    public FirmwareBootTime? FirmwareBootTime { get => _firmwareBootTime; private set => SetProperty(ref _firmwareBootTime, value); }

    // #706: median boot time per boot-type bucket ("Fast Startup resume: 9s · full restart: 71s").
    public ObservableCollection<BootTypeStat> BootTypeStats { get; } = new();

    private string? _bootTypeStatsText;
    /// <summary>Precomposed caption joining every bucket in BootTypeStats with " · ", e.g.
    /// "Fast Startup resume: 9s (12) · Full restart: 71s (3)" - built once in the ViewModel
    /// rather than with an XAML converter chain, matching how MeasuredDelayText/ImpactText etc.
    /// are already precomposed elsewhere on this tab.</summary>
    public string? BootTypeStatsText { get => _bootTypeStatsText; private set => SetProperty(ref _bootTypeStatsText, value); }

    // #707: "this boot was 2.4x your normal" quick flag, null when there's no meaningful
    // regression (or not enough same-boot-type history yet to judge against).
    private BootRegressionFlag? _regressionFlag;
    public BootRegressionFlag? RegressionFlag { get => _regressionFlag; private set => SetProperty(ref _regressionFlag, value); }

    // #708: boot-start driver/system-start driver and service load failures (Service Control
    // Manager 7026/7000/7001) - cross-linked to the Services tab's driver list, see
    // StartupView.xaml.cs's ViewDriversInServices_Click.
    public ObservableCollection<DriverLoadFailure> DriverLoadFailures { get; } = new();

    // #709: two-step, opt-in "Capture a boot log" workflow (bcdedit bootlog + ntbtlog.txt parse) -
    // see BootLogCaptureService's remarks. Never armed silently - IsBootLogCaptureArmed always
    // reflects the persisted pending state, so the Startup tab shows an explicit armed/pending
    // state (and an easy Disarm) rather than a silent background flag.
    private bool _isBootLogCaptureArmed;
    public bool IsBootLogCaptureArmed { get => _isBootLogCaptureArmed; private set => SetProperty(ref _isBootLogCaptureArmed, value); }

    private NtbtlogResult? _ntbtlogResult;
    public NtbtlogResult? NtbtlogResult { get => _ntbtlogResult; private set => SetProperty(ref _ntbtlogResult, value); }

    public AsyncRelayCommand ArmBootLogCaptureCommand { get; }
    public AsyncRelayCommand DisarmBootLogCaptureCommand { get; }

    // #710: one-click boot ETW trace via the Windows Performance Recorder - hidden entirely on the
    // Startup tab when wpr.exe isn't present (IsBootEtwTraceAvailable). Same two-step, opt-in shape
    // as boot log capture above.
    public bool IsBootEtwTraceAvailable => BootEtwTraceService.IsAvailable;

    private bool _isBootEtwTraceArmed;
    public bool IsBootEtwTraceArmed { get => _isBootEtwTraceArmed; private set => SetProperty(ref _isBootEtwTraceArmed, value); }

    private string? _lastEtlPath;
    public string? LastEtlPath { get => _lastEtlPath; private set => SetProperty(ref _lastEtlPath, value); }

    public AsyncRelayCommand ArmBootEtwTraceCommand { get; }
    public AsyncRelayCommand DisarmBootEtwTraceCommand { get; }
    public RelayCommand OpenEtlInWpaCommand { get; }

    // #711: Prefetcher/ReadyBoot configuration audit - see PrefetchAuditService's remarks.
    private PrefetchAuditResult? _prefetchAudit;
    public PrefetchAuditResult? PrefetchAudit { get => _prefetchAudit; private set => SetProperty(ref _prefetchAudit, value); }
    public AsyncRelayCommand RestorePrefetchDefaultsCommand { get; }

    // #712: boot-data availability explainer - only populated when BootBreakdown came back null,
    // i.e. there was actually nothing to explain away.
    private BootDataAvailability? _bootDataAvailability;
    public BootDataAvailability? BootDataAvailability { get => _bootDataAvailability; private set => SetProperty(ref _bootDataAvailability, value); }
    public AsyncRelayCommand EnableDiagnosticsChannelCommand { get; }

    // #714: BootExecute audit (autochk/chkdsk-at-boot detection) - see BootPerformanceService.ReadBootExecute.
    private BootExecuteInfo? _bootExecuteInfo;
    public BootExecuteInfo? BootExecuteInfo { get => _bootExecuteInfo; private set => SetProperty(ref _bootExecuteInfo, value); }

    #region #715-723 - Sign-in section (logon breakdown, Group Policy, profile health)

    // #715: Winlogon notification-subscriber timing (GPClient/Profiles/TermSrv/Sens, whichever
    // subscribers this boot's sign-in actually notified) - see LogonDiagnosticsService.ReadSubscriberTimings.
    public ObservableCollection<LogonSubscriberTiming> SignInSubscriberTimings { get; } = new();

    // #716: Group Policy total processing time per boot (computer 8000 / user 8001), charted
    // alongside the boot-time trend above using the same LineSeries-pair-per-collection shape.
    public ObservableCollection<double> GpComputerPolicyMs { get; } = new();
    public ObservableCollection<double> GpUserPolicyMs { get; } = new();
    private readonly LineSeries<double> _gpComputerPolicyLine;
    private readonly LineSeries<double> _gpUserPolicyLine;
    public ISeries[] GroupPolicyProcessingSeries { get; }
    public Axis[] GroupPolicyProcessingXAxes { get; }
    public Axis[] GroupPolicyProcessingYAxes { get; }

    // #717: slowest Group Policy client-side extensions (Drive Maps, Scripts, Folder Redirection,
    // Registry, ...) ranked under the chart above - see LogonDiagnosticsService.ReadSlowestExtensions.
    public ObservableCollection<GroupPolicyCseEntry> SlowestGroupPolicyExtensions { get; } = new();

    // #718: synchronous foreground policy audit - read-only, see SyncForegroundPolicyAudit's remarks.
    private SyncForegroundPolicyAudit? _syncForegroundAudit;
    public SyncForegroundPolicyAudit? SyncForegroundAudit { get => _syncForegroundAudit; private set => SetProperty(ref _syncForegroundAudit, value); }

    // #719: logon/startup/logoff/shutdown script inventory, plus the legacy UserInitMprLogonScript value.
    public ObservableCollection<LogonScriptInfo> LogonScripts { get; } = new();

    // #720: profile load duration per sign-in (event 1/2 pairing) - see ProfileDiagnosticsService.ReadProfileLoadTimings.
    public ObservableCollection<ProfileLoadTiming> ProfileLoadTimings { get; } = new();

    // #721/#722: every SID this machine's ProfileList registry key knows about, plus the subset
    // with a roaming CentralProfile configured (a separate collection so the roaming card can
    // collapse entirely on a machine with no roaming profiles - see StartupView.xaml).
    public ObservableCollection<ProfileListEntry> ProfileListEntries { get; } = new();
    public ObservableCollection<ProfileListEntry> RoamingProfiles { get; } = new();

    // #721/#722: correlated User Profile Service Application-log events (1500/1502/1511/1515 -
    // temp/corrupt profile family; 1509/1521 - roaming copy/sync errors).
    public ObservableCollection<ProfileServiceEventEntry> ProfileServiceEvents { get; } = new();

    // #723: the 1530 "registry file is still in use" subset of the events above, each with the
    // leaked hive's holding process names parsed out for the Processes-tab cross-link - see
    // StartupView.xaml.cs's ViewLeakedProcessInProcesses_Click.
    public ObservableCollection<ProfileServiceEventEntry> RegistryHandleLeaks { get; } = new();

    public AsyncRelayCommand MeasureProfileSizeCommand { get; }

    #endregion

    public StartupViewModel()
    {
        _bootHistoryLine = new LineSeries<double>
        {
            Values = BootHistoryMs,
            Stroke = new SolidColorPaint(SKColors.DeepSkyBlue, 2f),
            Fill = null,
            GeometrySize = 6,
            LineSmoothness = 0.2,
        };
        BootHistorySeries = new ISeries[] { _bootHistoryLine };
        BootHistoryXAxes = new[] { new Axis { IsVisible = false, ShowSeparatorLines = false } };
        BootHistoryYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v / 1000.0:0.#}s",
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 },
            },
        };

        // #716: computer/user Group Policy processing-time trend, same pair-of-collections/one-
        // axis-set shape as the boot-time trend above.
        _gpComputerPolicyLine = new LineSeries<double>
        {
            Name = "Computer boot policy",
            Values = GpComputerPolicyMs,
            Stroke = new SolidColorPaint(SKColors.MediumPurple, 2f),
            Fill = null,
            GeometrySize = 5,
            LineSmoothness = 0.2,
        };
        _gpUserPolicyLine = new LineSeries<double>
        {
            Name = "User logon policy",
            Values = GpUserPolicyMs,
            Stroke = new SolidColorPaint(SKColors.Orange, 2f),
            Fill = null,
            GeometrySize = 5,
            LineSmoothness = 0.2,
        };
        GroupPolicyProcessingSeries = new ISeries[] { _gpComputerPolicyLine, _gpUserPolicyLine };
        GroupPolicyProcessingXAxes = new[] { new Axis { IsVisible = false, ShowSeparatorLines = false } };
        GroupPolicyProcessingYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v / 1000.0:0.#}s",
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 },
            },
        };

        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleEnabledCommand = new RelayCommand(param => Toggle(param as StartupItem ?? SelectedItem));

        LoadScheduledTasksCommand = new AsyncRelayCommand(LoadScheduledTasksAsync);
        ToggleScheduledTaskCommand = new AsyncRelayCommand(param => ToggleScheduledTaskAsync(param as ScheduledTaskRow ?? SelectedScheduledTask));
        CheckLogonDelayCommand = new AsyncRelayCommand(CheckLogonDelayAsync, () => SelectedScheduledTask is not null);

        LoadBrowserExtensionsCommand = new AsyncRelayCommand(LoadBrowserExtensionsAsync);
        LoadShellExtensionsCommand = new AsyncRelayCommand(LoadShellExtensionsAsync);

        ArmBootLogCaptureCommand = new AsyncRelayCommand(ArmBootLogCaptureAsync, () => !IsBootLogCaptureArmed);
        DisarmBootLogCaptureCommand = new AsyncRelayCommand(DisarmBootLogCaptureAsync, () => IsBootLogCaptureArmed);

        ArmBootEtwTraceCommand = new AsyncRelayCommand(ArmBootEtwTraceAsync, () => IsBootEtwTraceAvailable && !IsBootEtwTraceArmed);
        DisarmBootEtwTraceCommand = new AsyncRelayCommand(DisarmBootEtwTraceAsync, () => IsBootEtwTraceArmed);
        OpenEtlInWpaCommand = new RelayCommand(_ => { if (LastEtlPath is not null) BootEtwTraceService.OpenInWpa(LastEtlPath); }, _ => LastEtlPath is not null);

        RestorePrefetchDefaultsCommand = new AsyncRelayCommand(RestorePrefetchDefaultsAsync);
        EnableDiagnosticsChannelCommand = new AsyncRelayCommand(EnableDiagnosticsChannelAsync, () => BootDataAvailability?.CanOfferEnable == true);

        MeasureProfileSizeCommand = new AsyncRelayCommand(param => MeasureProfileSizeAsync(param as ProfileListEntry));

        Refresh();
        LoadBootPerformance();
        LoadSignInDiagnostics();
        _ = CheckPendingCaptureWorkflowsAsync();
    }

    private async Task LoadBrowserExtensionsAsync()
    {
        IsLoadingBrowserExtensions = true;
        try
        {
            var extensions = await Task.Run(BrowserExtensionService.List);
            BrowserExtensions.Clear();
            foreach (var e in extensions) BrowserExtensions.Add(e);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load browser extensions: {ex.Message}";
        }
        finally
        {
            IsLoadingBrowserExtensions = false;
        }
    }

    private async Task LoadShellExtensionsAsync()
    {
        IsLoadingShellExtensions = true;
        try
        {
            var extensions = await Task.Run(ShellExtensionService.List);
            ShellExtensions.Clear();
            foreach (var e in extensions) ShellExtensions.Add(e);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load shell extensions: {ex.Message}";
        }
        finally
        {
            IsLoadingShellExtensions = false;
        }
    }

    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        BootHistoryYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        BootHistoryYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        GroupPolicyProcessingYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        GroupPolicyProcessingYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private void LoadBootPerformance()
    {
        _ = Task.Run(async () =>
        {
            var breakdown = BootPerformanceService.ReadLatest();
            var history = BootPerformanceService.RecordAndLoadHistory(breakdown);

            // #701: this boot's own slice of the degradation-event family, bounded to events at
            // or after this boot's timestamp (there's no "next boot" yet - this is the current
            // session).
            var allDegradations = BootPerformanceService.ReadDegradationEvents();
            var thisBootDegradations = breakdown is not null
                ? BootPerformanceService.FilterForBoot(allDegradations, breakdown.BootTime, null)
                : new List<BootDegradationEvent>();

            // #702: ranked culprit board across every boot the channel still retains.
            var culprits = BootPerformanceService.BuildCulpritBoard();

            // #704: firmware POST time (realistically "Unknown" on most systems - see
            // FirmwareBootTime's remarks).
            var firmware = BootPerformanceService.ReadFirmwareBootTime();

            // #706/#707: boot-type-split stats and the regression flag, both derived from the
            // same persisted history #705 tags with a boot type as it's recorded.
            var typeStats = BootPerformanceService.ComputeBootTypeStats(history);
            var regressionFlag = BootPerformanceService.ComputeRegressionFlag(history, thisBootDegradations);

            // #708: boot-start/system-start driver and service load failures.
            var driverFailures = BootPerformanceService.ReadDriverLoadFailures();

            // #711: Prefetcher/ReadyBoot configuration audit.
            var prefetchAudit = PrefetchAuditService.Read();

            // #714: BootExecute audit (autochk/chkdsk-at-boot detection).
            var bootExecute = BootPerformanceService.ReadBootExecute();

            // #712: only worth diagnosing why boot data is missing when it's actually missing.
            var availability = breakdown is null ? await BootPerformanceService.DiagnoseUnavailabilityAsync() : null;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BootBreakdown = breakdown;
                BootHistoryMs.Clear();
                foreach (var h in history) BootHistoryMs.Add(h.TotalMs);

                ThisBootDegradations.Clear();
                foreach (var d in thisBootDegradations) ThisBootDegradations.Add(d);

                SlowBootCulprits.Clear();
                foreach (var c in culprits) SlowBootCulprits.Add(c);

                FirmwareBootTime = firmware;

                BootTypeStats.Clear();
                foreach (var s in typeStats) BootTypeStats.Add(s);
                BootTypeStatsText = typeStats.Count == 0 ? null : string.Join(" · ", typeStats.Select(s => s.Text));

                RegressionFlag = regressionFlag;

                DriverLoadFailures.Clear();
                foreach (var f in driverFailures) DriverLoadFailures.Add(f);

                PrefetchAudit = prefetchAudit;
                BootExecuteInfo = bootExecute;
                BootDataAvailability = availability;
            });
        });
    }

    /// <summary>#715-719/#721-723: the Startup tab's "Sign-in" section - Winlogon subscriber
    /// timing, Group Policy processing time + slowest CSEs, the synchronous-foreground-policy
    /// audit, the logon/startup script inventory, profile load duration, and the ProfileList/
    /// User-Profile-Service correlation for temp/corrupt/roaming profiles and registry-handle
    /// leaks. All event-log/registry/file-system reads, same "expensive, so run once off the UI
    /// thread at load/refresh time rather than on a tick" tradeoff as LoadBootPerformance above -
    /// #720's actual size *walk* is the one piece gated behind its own explicit button
    /// (MeasureProfileSizeCommand), never run automatically here.</summary>
    private void LoadSignInDiagnostics()
    {
        _ = Task.Run(() =>
        {
            var subscriberTimings = LogonDiagnosticsService.ReadSubscriberTimings();
            var gpProcessingTimes = LogonDiagnosticsService.ReadProcessingTimes();
            var slowestExtensions = LogonDiagnosticsService.ReadSlowestExtensions();
            var syncForegroundAudit = LogonDiagnosticsService.ReadSyncForegroundPolicyAudit();
            var logonScripts = LogonDiagnosticsService.ReadLogonScripts();

            var profileLoadTimings = ProfileDiagnosticsService.ReadProfileLoadTimings();
            var profileListEntries = ProfileDiagnosticsService.ReadProfileListEntries();
            var profileServiceEvents = ProfileDiagnosticsService.ReadProfileServiceEvents();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SignInSubscriberTimings.Clear();
                foreach (var t in subscriberTimings) SignInSubscriberTimings.Add(t);

                // #716: split by IsUserPolicy into the two chart series - both share the same
                // x-index scheme as the boot-time trend (one point per recorded event, oldest
                // first), not aligned to a shared calendar axis, same "index, not timestamp, is
                // the x-axis" shape BootHistorySeries already uses.
                GpComputerPolicyMs.Clear();
                foreach (var e in gpProcessingTimes.Where(e => !e.IsUserPolicy)) GpComputerPolicyMs.Add(e.ElapsedMs);
                GpUserPolicyMs.Clear();
                foreach (var e in gpProcessingTimes.Where(e => e.IsUserPolicy)) GpUserPolicyMs.Add(e.ElapsedMs);

                SlowestGroupPolicyExtensions.Clear();
                foreach (var e in slowestExtensions) SlowestGroupPolicyExtensions.Add(e);

                SyncForegroundAudit = syncForegroundAudit;

                LogonScripts.Clear();
                foreach (var s in logonScripts) LogonScripts.Add(s);

                ProfileLoadTimings.Clear();
                foreach (var p in profileLoadTimings) ProfileLoadTimings.Add(p);

                ProfileListEntries.Clear();
                foreach (var p in profileListEntries) ProfileListEntries.Add(p);

                // #722: the roaming subset gets its own collection so the card can collapse
                // entirely on a machine with no roaming profiles configured - see StartupView.xaml.
                RoamingProfiles.Clear();
                foreach (var p in profileListEntries.Where(p => p.IsRoaming)) RoamingProfiles.Add(p);

                ProfileServiceEvents.Clear();
                RegistryHandleLeaks.Clear();
                foreach (var e in profileServiceEvents)
                {
                    ProfileServiceEvents.Add(e);
                    if (e.EventId == 1530) RegistryHandleLeaks.Add(e);
                }
            });
        });
    }

    /// <summary>#720: on-demand size/file-count walk for one ProfileList row's ProfileImagePath -
    /// gated behind the "Measure size" button on that row, never run automatically (a recursive
    /// walk of an entire profile folder is exactly the kind of file-system walk this app's
    /// on-demand-vs-polled convention reserves for an explicit click). Result is stored on the row
    /// itself (ProfileListEntry.SizeInfo) rather than a single ViewModel-wide property, so
    /// measuring one row doesn't clobber another row's already-measured result.</summary>
    private async Task MeasureProfileSizeAsync(ProfileListEntry? entry)
    {
        if (entry?.ProfileImagePath is not { } path) return;

        entry.IsMeasuringSize = true;
        try
        {
            entry.SizeInfo = await Task.Run(() => ProfileDiagnosticsService.ComputeProfileSize(path));
        }
        finally
        {
            entry.IsMeasuringSize = false;
        }
    }

    /// <summary>#709/#710: checks whether a boot log capture and/or a boot ETW trace was armed in
    /// a previous session and, if so, attempts to finish the workflow now that the app is running
    /// again (presumably after the reboot the arm step asked for). Called once at startup,
    /// alongside LoadBootPerformance - never on a timer.</summary>
    private async Task CheckPendingCaptureWorkflowsAsync()
    {
        var logState = await Task.Run(BootLogCaptureService.LoadState);
        if (logState.IsArmed && logState.ArmedAtUtc is { } armedAt)
        {
            var parsed = await Task.Run(() => BootLogCaptureService.ReadAndParseLog(armedAt));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBootLogCaptureArmed = true;
                NtbtlogResult = parsed;
                if (parsed is not null)
                    StatusMessage = $"Boot log captured - {parsed.FailedDrivers.Count} driver(s) did not load. Turn boot logging back off when you're done reviewing it.";
            });
        }

        var etwState = await Task.Run(BootEtwTraceService.LoadState);
        if (etwState.IsArmed)
        {
            var (collected, error, path) = await BootEtwTraceService.CollectIfPendingAsync();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBootEtwTraceArmed = false; // CollectIfPendingAsync always clears the pending state, success or not
                if (collected)
                {
                    LastEtlPath = path;
                    StatusMessage = $"Boot ETW trace collected: {path}";
                }
                else if (error is not null)
                {
                    StatusMessage = $"Couldn't collect the pending boot ETW trace: {error}";
                }
            });
        }
    }

    private async Task ArmBootLogCaptureAsync()
    {
        var (success, error) = await BootLogCaptureService.ArmAsync();
        if (success)
        {
            IsBootLogCaptureArmed = true;
            NtbtlogResult = null;
            StatusMessage = "Boot logging armed. Restart your PC, then reopen this app to see the results.";
        }
        else
        {
            StatusMessage = $"Couldn't arm boot log capture: {error}";
        }
    }

    private async Task DisarmBootLogCaptureAsync()
    {
        var (success, error) = await BootLogCaptureService.DisarmAsync();
        IsBootLogCaptureArmed = false;
        NtbtlogResult = null;
        StatusMessage = success ? "Boot logging turned off." : $"Couldn't turn off boot logging: {error}";
    }

    private async Task ArmBootEtwTraceAsync()
    {
        var (success, error) = await BootEtwTraceService.ArmAsync();
        if (success)
        {
            IsBootEtwTraceArmed = true;
            LastEtlPath = null;
            StatusMessage = "Boot ETW trace armed. Restart your PC, then reopen this app to collect it.";
        }
        else
        {
            StatusMessage = $"Couldn't arm the boot ETW trace: {error}";
        }
    }

    private async Task DisarmBootEtwTraceAsync()
    {
        var (success, error) = await BootEtwTraceService.DisarmAsync();
        IsBootEtwTraceArmed = false;
        StatusMessage = success ? "Boot ETW trace canceled." : $"Couldn't cancel the boot ETW trace: {error}";
    }

    private async Task RestorePrefetchDefaultsAsync()
    {
        var (success, error) = await Task.Run(PrefetchAuditService.RestoreDefaults);
        if (success)
        {
            PrefetchAudit = await Task.Run(PrefetchAuditService.Read);
            StatusMessage = "Prefetcher/SysMain restored to Windows defaults.";
        }
        else
        {
            StatusMessage = $"Couldn't restore Prefetcher defaults: {error}";
        }
    }

    private async Task EnableDiagnosticsChannelAsync()
    {
        var (success, error) = await BootPerformanceService.EnableDiagnosticsChannelAsync();
        StatusMessage = success
            ? "Diagnostics channel enabled. Boot-time data will be available after the next boot."
            : $"Couldn't enable the diagnostics channel: {error}";
        if (success) BootDataAvailability = await BootPerformanceService.DiagnoseUnavailabilityAsync();
    }

    private async Task LoadScheduledTasksAsync()
    {
        IsLoadingScheduledTasks = true;
        try
        {
            var tasks = await ScheduledTaskService.ListAsync();
            ScheduledTasks.Clear();
            foreach (var t in tasks) ScheduledTasks.Add(t);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load scheduled tasks: {ex.Message}";
        }
        finally
        {
            IsLoadingScheduledTasks = false;
        }
    }

    private async Task ToggleScheduledTaskAsync(ScheduledTaskRow? task)
    {
        if (task is null) return;

        bool newState = !task.IsEnabled;
        var (success, error) = await ScheduledTaskService.SetEnabledAsync(task.Name, newState);
        if (success)
        {
            task.IsEnabled = newState;
            StatusMessage = $"{task.Name} {(newState ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusMessage = $"Couldn't change {task.Name}: {error}";
        }
    }

    private async Task CheckLogonDelayAsync()
    {
        var target = SelectedScheduledTask;
        if (target is null) return;

        try
        {
            var (delayText, runModeText) = await ScheduledTaskService.ReadLogonTriggerInfoAsync(target.Name);
            target.DelayText = delayText;
            target.RunModeText = runModeText;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't check logon delay for {target.Name}: {ex.Message}";
        }
    }

    private void Refresh()
    {
        Items.Clear();
        var items = _service.Sample();
        foreach (var item in items)
            Items.Add(item);

        // #91/#22: measured startup delay + combined impact score, off the UI thread (Process
        // enumeration + per-process StartTime/CPU/memory reads) - applied back via Dispatcher, the
        // same pattern StorageViewModel's background WMI queries use.
        _ = Task.Run(() =>
        {
            var measurements = StartupDelayService.ComputeDelays(items);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, measurement) in measurements)
                {
                    item.MeasuredDelayText = measurement.DelayText;
                    item.ImpactText = measurement.ImpactText;
                    item.ImpactDetailText = measurement.ImpactDetailText;
                }
            });
        });

        // #18: signed/unsigned trust badge, reusing SignatureCheckService's shared per-path cache
        // (extracted from ProcessMonitorService in Round 2) rather than a duplicate check - also
        // off the UI thread, since a first-time signature check reads the file from disk.
        _ = Task.Run(() =>
        {
            var statuses = items.ToDictionary(item => item, item => SignatureCheckService.GetStatus(StartupManagerService.ExtractPath(item.Command)));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, status) in statuses) item.SignatureStatus = status;
            });
        });
    }

    private void Toggle(StartupItem? item)
    {
        if (item is null) return;

        bool newState = !item.IsEnabled;
        var (success, error) = StartupManagerService.SetEnabled(item, newState);
        if (success)
        {
            item.IsEnabled = newState;
            StatusMessage = $"{item.Name} {(newState ? "enabled" : "disabled")}. Takes effect on next sign-in.";
        }
        else
        {
            StatusMessage = $"Couldn't change {item.Name}: {error}";
        }
    }
}
