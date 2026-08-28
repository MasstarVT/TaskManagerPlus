using System.Collections.ObjectModel;
using System.Windows;
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
    private readonly EventLogService _eventLog = new();

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
                // Stale delay/run-mode/trigger-summary text from a previous selection would be misleading.
                previous.DelayText = string.Empty;
                previous.RunModeText = string.Empty;
                previous.TriggerSummaryText = string.Empty;
            }
        }
    }

    public AsyncRelayCommand LoadScheduledTasksCommand { get; }
    public AsyncRelayCommand ToggleScheduledTaskCommand { get; }
    public AsyncRelayCommand CheckLogonDelayCommand { get; }

    /// <summary>#765: certutil fallback decode for the selected task's LastResult - see
    /// ScheduledTaskService.DecodeLastResultAsync.</summary>
    public AsyncRelayCommand DecodeLastResultCommand { get; }

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

    private ShellExtensionInfo? _selectedShellExtension;
    public ShellExtensionInfo? SelectedShellExtension { get => _selectedShellExtension; set => SetProperty(ref _selectedShellExtension, value); }

    // #829: approve/block toggle - writes/removes the CLSID under "Shell Extensions\Approved"
    // (ShellExtensionService.SetApproved) rather than unregistering anything.
    public RelayCommand ToggleShellExtensionApprovedCommand { get; }

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

    #region #724-731 - Boot configuration (BCD inspector)

    // #724: one shared BcdInspectorService.ReadAsync() snapshot behind every #724-731 feature -
    // see BcdInspectorService's remarks for why bcdedit is shelled out to once per refresh, not
    // once per feature.
    private BcdStore? _bcdStore;
    public BcdStore? BcdStore { get => _bcdStore; private set => SetProperty(ref _bcdStore, value); }

    public bool IsBcdAvailable => BcdStore?.Available == true;
    public string? BcdUnavailableReason => BcdStore?.Error;

    // #725: boot-mode/integrity quick-flag banner - a flag, not a verdict (see BootModeFlag's
    // remarks), each with a one-click "clear this flag" that confirms the exact bcdedit command
    // first.
    public ObservableCollection<BootModeFlag> BootModeFlags { get; } = new();
    public AsyncRelayCommand ClearBootModeFlagCommand { get; }

    // #727: performance-trap BCD options, with the observed effect compared against this
    // machine's real CPU/RAM totals.
    public ObservableCollection<PerformanceTrapOption> PerformanceTrapOptions { get; } = new();

    // #728: boot status policy / auto-repair audit.
    private BootStatusPolicyInfo? _bootStatusPolicy;
    public BootStatusPolicyInfo? BootStatusPolicy { get => _bootStatusPolicy; private set => SetProperty(ref _bootStatusPolicy, value); }
    public AsyncRelayCommand RestoreBootStatusPolicyCommand { get; }

    // #729: boot menu / multi-OS entry list - timeout change goes through a small text box bound
    // to BootTimeoutInput; default-entry change goes through a button per row (CommandParameter
    // is that row's BootMenuEntryRef).
    private BootMenuInfo? _bootMenuInfo;
    public BootMenuInfo? BootMenuInfo { get => _bootMenuInfo; private set => SetProperty(ref _bootMenuInfo, value); }

    private string _bootTimeoutInput = string.Empty;
    public string BootTimeoutInput { get => _bootTimeoutInput; set => SetProperty(ref _bootTimeoutInput, value); }

    public AsyncRelayCommand SetBootTimeoutCommand { get; }
    public AsyncRelayCommand SetDefaultEntryCommand { get; }

    // #730: UEFI firmware boot order - read-only listing plus a copyable fix command (never run
    // automatically - see FirmwareBootOrderInfo's remarks).
    private FirmwareBootOrderInfo? _firmwareBootOrder;
    public FirmwareBootOrderInfo? FirmwareBootOrder { get => _firmwareBootOrder; private set => SetProperty(ref _firmwareBootOrder, value); }
    public RelayCommand CopyFirmwareDisplayOrderFixCommand { get; }

    // #731: BCD backup and export - restore is never automated, only the matching `bcdedit
    // /import` command is shown (see BcdBackupEntry.ImportCommandText).
    public ObservableCollection<BcdBackupEntry> BcdBackups { get; } = new();
    public AsyncRelayCommand ExportBcdBackupCommand { get; }

    #endregion

    #region #734-737 - Fast Startup, hibernation, and the recovery path

    // #734: uptime-clock reconciliation - see FastStartupService.ReadUptimeInfo/FastStartupInfo's
    // remarks. MainViewModel also reads this (via Startup.FastStartupInfo) to compose the footer
    // status bar's uptime text.
    private FastStartupInfo? _fastStartupInfo;
    public FastStartupInfo? FastStartupInfo { get => _fastStartupInfo; private set => SetProperty(ref _fastStartupInfo, value); }

    // #735: "you haven't fully restarted in N days" actionable card - shown once FastStartupInfo
    // says Fast Startup is on and the gap is large enough to matter, dismissible for 7 days via
    // FastStartupPromptSettingsService. IsPromptDismissed is read fresh each Refresh (never
    // cached across the dismiss action) so a dismiss taken in this session immediately hides it.
    private bool _isFullRestartPromptDismissed;
    public bool ShowFullRestartPrompt => FastStartupInfo?.DaysSinceFullRestart is { } days && days >= 3 && !_isFullRestartPromptDismissed;
    public AsyncRelayCommand FullRestartCommand { get; }
    public RelayCommand DismissFullRestartPromptCommand { get; }

    // #736: hibernation / sleep-state inventory.
    public ObservableCollection<SleepStateInfo> SleepStates { get; } = new();
    private HiberFileInfo? _hiberFileInfo;
    public HiberFileInfo? HiberFileInfo { get => _hiberFileInfo; private set => SetProperty(ref _hiberFileInfo, value); }
    public AsyncRelayCommand DisableHibernationCommand { get; }
    public AsyncRelayCommand EnableHibernationCommand { get; }
    public AsyncRelayCommand ReduceHiberFileTypeCommand { get; }

    private string _hiberFileSizePercentInput = string.Empty;
    public string HiberFileSizePercentInput { get => _hiberFileSizePercentInput; set => SetProperty(ref _hiberFileSizePercentInput, value); }
    public AsyncRelayCommand SetHiberFileSizeCommand { get; }

    // #737: Fast Startup side-effect flags - populated whenever FastStartupInfo says Fast Startup
    // is on (see FastStartupService.SideEffects), plus its own confirmed "turn it off" action.
    public ObservableCollection<FastStartupSideEffect> FastStartupSideEffects { get; } = new();
    public AsyncRelayCommand DisableFastStartupCommand { get; }

    #endregion

    #region #738-740 - System partitions (ESP, WinRE, recovery layout)

    // #738/#740: one shared SystemPartitionService.ReadLayout() snapshot behind the ESP health
    // card, the recovery-partition layout map, and (indirectly, via its Recovery partition) the
    // recovery-too-small flag - see SystemPartitionService's remarks for why the disk/partition
    // WMI query runs once, not once per feature.
    private SystemPartitionLayout? _partitionLayout;
    public SystemPartitionLayout? PartitionLayout { get => _partitionLayout; private set => SetProperty(ref _partitionLayout, value); }

    // #738: EFI System Partition health - free space is measured on demand (mounting is a more
    // invasive action than this tab's other auto-loaded reads, same "gate the invasive step
    // behind an explicit button" tradeoff #720's MeasureProfileSizeCommand already takes).
    private EspHealthInfo? _espHealth;
    public EspHealthInfo? EspHealth { get => _espHealth; private set => SetProperty(ref _espHealth, value); }
    public AsyncRelayCommand MeasureEspFreeSpaceCommand { get; }

    // #739: WinRE status via reagentc /info.
    private WinReStatusInfo? _winReStatus;
    public WinReStatusInfo? WinReStatus { get => _winReStatus; private set => SetProperty(ref _winReStatus, value); }
    public AsyncRelayCommand EnableWinReCommand { get; }

    // #740: recovery-partition-too-small flag - also gated behind its own "Measure free space"
    // button, same reasoning as the ESP above.
    private RecoveryPartitionFlag? _recoveryPartitionFlag;
    public RecoveryPartitionFlag? RecoveryPartitionFlag { get => _recoveryPartitionFlag; private set => SetProperty(ref _recoveryPartitionFlag, value); }
    public AsyncRelayCommand MeasureRecoveryFreeSpaceCommand { get; }

    #endregion

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

    #region #742-748 - Autostart coverage beyond the Run keys

    // #742 (full autorun location sweep) has no dedicated ViewModel state - it's handled entirely
    // inside StartupManagerService.Sample() and StartupItem.SourceDescription/SupportsToggle, so
    // the new rows just show up in the existing Items grid alongside everything else.

    // #743: Active Setup component inventory - registry-only, cheap enough to read on every
    // Refresh rather than needing its own "Load" button (unlike Scheduled Tasks/browser
    // extensions below, which are each a heavier scan).
    public ObservableCollection<ActiveSetupComponent> ActiveSetupComponents { get; } = new();

    // #744: Winlogon shell chain integrity check - a quick flag, not a verdict.
    public ObservableCollection<WinlogonCheckEntry> WinlogonChecks { get; } = new();

    // #745: Image File Execution Options hijack audit.
    public ObservableCollection<ImageFileExecutionOptionsEntry> ImageFileExecutionOptionsEntries { get; } = new();

    // #746: global DLL injection audit (AppInit_DLLs x2 views, AppCertDlls, LSA security/
    // authentication packages, KnownDLLs anomalies).
    private DllInjectionAuditResult? _dllInjectionAudit;
    public DllInjectionAuditResult? DllInjectionAudit { get => _dllInjectionAudit; private set => SetProperty(ref _dllInjectionAudit, value); }

    // #747 (boot/logon-triggered scheduled tasks) has no dedicated ViewModel state either - see
    // LoadScheduledTaskStartupRowsAsync, which appends them straight into the main Items grid as
    // regular StartupItem rows (Source = StartupSource.ScheduledTaskTrigger).

    // #748 (persisted per-item startup cost history) also has no dedicated ViewModel state beyond
    // what's already on StartupItem (MedianDelayText/SparklinePointsText/DelayTrendFlag) - see
    // Refresh()'s delay-scan block, which now also calls StartupHistoryService.RecordAndCompute.

    #endregion

    #region #764-768 - Scheduled task inventory and failure history

    // #764: Microsoft-Windows-TaskScheduler/Operational is disabled by default - IsTaskSchedulerLogEnabled
    // is re-checked every time the section loads (never cached) so a user who enables it from here
    // sees the "enable" prompt disappear immediately. TaskFailures itself stays empty (not an error)
    // when the channel is disabled - the "Task failures" grid caption tells the two states apart.
    private bool _isTaskSchedulerLogEnabled;
    public bool IsTaskSchedulerLogEnabled { get => _isTaskSchedulerLogEnabled; private set => SetProperty(ref _isTaskSchedulerLogEnabled, value); }
    public AsyncRelayCommand EnableTaskFailureLogCommand { get; }
    public AsyncRelayCommand LoadTaskFailuresCommand { get; }

    public ObservableCollection<TaskSchedulerOperationalEvent> TaskFailures { get; } = new();

    private string? _taskFailuresStatus;
    public string? TaskFailuresStatus { get => _taskFailuresStatus; private set => SetProperty(ref _taskFailuresStatus, value); }

    // #768: worst-offender run-duration chart, under the Task failures grid - top 10 tasks by max
    // observed wall-clock duration, paired from operational events 100/129 (started) and 102/201
    // (completed) by ActivityId - see EventLogService.ReadTaskRunDurations. Same ColumnSeries shape
    // StabilityViewModel's Reliability History chart already uses for a ranked/discrete series.
    public ObservableCollection<double> WorstTaskDurationsMs { get; } = new();
    private readonly ColumnSeries<double> _worstTaskDurationsColumn;
    public ISeries[] WorstTaskDurationsSeries { get; }
    public Axis[] WorstTaskDurationsXAxes { get; }
    public Axis[] WorstTaskDurationsYAxes { get; }

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

        // #768: worst-offender task-duration chart - a ColumnSeries, same "discrete/ranked reads
        // better as bars" reasoning StabilityViewModel's Reliability History chart already uses.
        _worstTaskDurationsColumn = new ColumnSeries<double>
        {
            Values = WorstTaskDurationsMs,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 24,
        };
        WorstTaskDurationsSeries = new ISeries[] { _worstTaskDurationsColumn };
        WorstTaskDurationsXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = -15,
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = null,
            },
        };
        WorstTaskDurationsYAxes = new[]
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
        DecodeLastResultCommand = new AsyncRelayCommand(DecodeLastResultAsync, () => SelectedScheduledTask is not null);

        LoadBrowserExtensionsCommand = new AsyncRelayCommand(LoadBrowserExtensionsAsync);
        LoadShellExtensionsCommand = new AsyncRelayCommand(LoadShellExtensionsAsync);
        ToggleShellExtensionApprovedCommand = new RelayCommand(param => ToggleShellExtensionApproved(param as ShellExtensionInfo ?? SelectedShellExtension));

        ArmBootLogCaptureCommand = new AsyncRelayCommand(ArmBootLogCaptureAsync, () => !IsBootLogCaptureArmed);
        DisarmBootLogCaptureCommand = new AsyncRelayCommand(DisarmBootLogCaptureAsync, () => IsBootLogCaptureArmed);

        ArmBootEtwTraceCommand = new AsyncRelayCommand(ArmBootEtwTraceAsync, () => IsBootEtwTraceAvailable && !IsBootEtwTraceArmed);
        DisarmBootEtwTraceCommand = new AsyncRelayCommand(DisarmBootEtwTraceAsync, () => IsBootEtwTraceArmed);
        OpenEtlInWpaCommand = new RelayCommand(_ => { if (LastEtlPath is not null) BootEtwTraceService.OpenInWpa(LastEtlPath); }, _ => LastEtlPath is not null);

        RestorePrefetchDefaultsCommand = new AsyncRelayCommand(RestorePrefetchDefaultsAsync);
        EnableDiagnosticsChannelCommand = new AsyncRelayCommand(EnableDiagnosticsChannelAsync, () => BootDataAvailability?.CanOfferEnable == true);

        MeasureProfileSizeCommand = new AsyncRelayCommand(param => MeasureProfileSizeAsync(param as ProfileListEntry));

        // #724-731: Boot configuration (BCD inspector) commands - every mutating one confirms
        // the exact bcdedit command first (see each handler below).
        ClearBootModeFlagCommand = new AsyncRelayCommand(param => ClearBootModeFlagAsync(param as BootModeFlag));
        RestoreBootStatusPolicyCommand = new AsyncRelayCommand(RestoreBootStatusPolicyAsync, () => BootStatusPolicy?.DisablesStartupRepair == true);
        SetBootTimeoutCommand = new AsyncRelayCommand(SetBootTimeoutAsync);
        SetDefaultEntryCommand = new AsyncRelayCommand(param => SetDefaultEntryAsync(param as BootMenuEntryRef));
        CopyFirmwareDisplayOrderFixCommand = new RelayCommand(_ => CopyFirmwareDisplayOrderFix(), _ => FirmwareBootOrder?.SuggestedFixCommand is not null);
        ExportBcdBackupCommand = new AsyncRelayCommand(ExportBcdBackupAsync);

        // #734-737: Fast Startup, hibernation, and the recovery path.
        FullRestartCommand = new AsyncRelayCommand(FullRestartAsync);
        DismissFullRestartPromptCommand = new RelayCommand(_ => DismissFullRestartPrompt());
        DisableHibernationCommand = new AsyncRelayCommand(DisableHibernationAsync);
        EnableHibernationCommand = new AsyncRelayCommand(EnableHibernationAsync);
        ReduceHiberFileTypeCommand = new AsyncRelayCommand(ReduceHiberFileTypeAsync);
        SetHiberFileSizeCommand = new AsyncRelayCommand(SetHiberFileSizeAsync);
        DisableFastStartupCommand = new AsyncRelayCommand(DisableFastStartupAsync, () => FastStartupInfo?.IsFastStartupEnabled == true);

        // #738-740: System partitions (ESP, WinRE, recovery layout).
        MeasureEspFreeSpaceCommand = new AsyncRelayCommand(MeasureEspFreeSpaceAsync, () => PartitionLayout?.Esp is not null);
        EnableWinReCommand = new AsyncRelayCommand(EnableWinReAsync, () => WinReStatus?.Enabled == false);
        MeasureRecoveryFreeSpaceCommand = new AsyncRelayCommand(MeasureRecoveryFreeSpaceAsync, () => PartitionLayout?.Recovery is not null);

        // #764-768: Task failures / run-duration history.
        EnableTaskFailureLogCommand = new AsyncRelayCommand(EnableTaskFailureLogAsync, () => !IsTaskSchedulerLogEnabled);
        LoadTaskFailuresCommand = new AsyncRelayCommand(LoadTaskFailuresAsync);

        Refresh();
        LoadBootPerformance();
        LoadSignInDiagnostics();
        LoadBcdInspector();
        LoadFastStartupAndHibernation();
        LoadSystemPartitions();
        LoadAutostartAudits();
        _ = CheckPendingCaptureWorkflowsAsync();

        // #764: a cheap, no-shell-out check (EventLogConfiguration.IsEnabled) so the "enable
        // Task Scheduler operational log" prompt reflects real state as soon as this tab opens,
        // without loading the (potentially expensive) failure/duration event scans up front.
        IsTaskSchedulerLogEnabled = EventLogService.IsTaskSchedulerOperationalLogEnabled();
        EnableTaskFailureLogCommand.RaiseCanExecuteChanged();
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

    private void ToggleShellExtensionApproved(ShellExtensionInfo? extension)
    {
        if (extension is null) return;

        bool newState = !extension.IsApproved;
        var (success, error) = ShellExtensionService.SetApproved(extension.Clsid, extension.Name, newState);
        if (success)
        {
            extension.IsApproved = newState;
            StatusMessage = $"{extension.Name} {(newState ? "approved" : "unapproved")}.";
        }
        else
        {
            StatusMessage = $"Couldn't change {extension.Name}: {error}";
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

        // #768: worst-offender task-duration chart.
        WorstTaskDurationsXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WorstTaskDurationsYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WorstTaskDurationsYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
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

    /// <summary>#724-731: Boot configuration (BCD inspector) - one BcdInspectorService.ReadAsync()
    /// snapshot behind every one of these features (see BcdInspectorService's remarks), off the UI
    /// thread like every other event-log/registry read this tab does at load/refresh time. Called
    /// once at startup, and again after any BCD mutation succeeds so the whole section reflects
    /// the new state immediately rather than the user having to hit the tab's own Refresh.</summary>
    private void LoadBcdInspector()
    {
        _ = Task.Run(async () =>
        {
            var store = await BcdInspectorService.ReadAsync();
            var flags = BcdInspectorService.DetectBootModeFlags(store.CurrentEntry);
            var (logicalProcessors, totalRamBytes) = BcdInspectorService.ReadSystemTotals();
            var perfTraps = BcdInspectorService.DetectPerformanceTrapOptions(store.CurrentEntry, logicalProcessors, totalRamBytes);
            var statusPolicy = BcdInspectorService.ReadBootStatusPolicy(store.CurrentEntry);
            var menu = BcdInspectorService.ReadBootMenuInfo(store);
            var fwOrder = BcdInspectorService.ReadFirmwareBootOrder(store);
            var backups = BcdInspectorService.ListBackups();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                BcdStore = store;
                OnPropertyChanged(nameof(IsBcdAvailable));
                OnPropertyChanged(nameof(BcdUnavailableReason));

                BootModeFlags.Clear();
                foreach (var f in flags) BootModeFlags.Add(f);

                PerformanceTrapOptions.Clear();
                foreach (var p in perfTraps) PerformanceTrapOptions.Add(p);

                BootStatusPolicy = statusPolicy;

                BootMenuInfo = menu;
                BootTimeoutInput = menu.TimeoutSeconds?.ToString() ?? string.Empty;

                FirmwareBootOrder = fwOrder;
                CopyFirmwareDisplayOrderFixCommand.RaiseCanExecuteChanged();

                BcdBackups.Clear();
                foreach (var b in backups) BcdBackups.Add(b);
            });
        });
    }

    /// <summary>#734-737: Fast Startup uptime reconciliation, hibernation/sleep-state inventory,
    /// and side-effect flags - one Task.Run off the UI thread, like every other on-demand read
    /// this tab does at load/refresh time (registry + WMI + a couple of powercfg shell-outs, none
    /// expensive enough on their own to need separate buttons).</summary>
    private void LoadFastStartupAndHibernation()
    {
        _ = Task.Run(async () =>
        {
            var info = FastStartupService.ReadUptimeInfo();
            var sleepStates = await FastStartupService.ReadSleepStatesAsync();
            var hiberFile = FastStartupService.ReadHiberFileInfo();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                FastStartupInfo = info;
                DisableFastStartupCommand.RaiseCanExecuteChanged();

                SleepStates.Clear();
                foreach (var s in sleepStates) SleepStates.Add(s);

                HiberFileInfo = hiberFile;

                FastStartupSideEffects.Clear();
                if (info.IsFastStartupEnabled)
                    foreach (var s in FastStartupService.SideEffects) FastStartupSideEffects.Add(s);

                var prompt = FastStartupPromptSettingsService.Load();
                _isFullRestartPromptDismissed = FastStartupPromptSettingsService.IsCurrentlyDismissed(prompt);
                OnPropertyChanged(nameof(ShowFullRestartPrompt));
            });
        });
    }

    /// <summary>#735: `shutdown /g /f /t 0` - confirms the exact command and its effect first,
    /// matching CLAUDE.md's "mutating actions require explicit confirmation" rule.</summary>
    private async Task FullRestartAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\nshutdown /g /f /t 0\n\nThis force-closes open apps and immediately restarts the PC into a full boot (not a Fast Startup hybrid resume) - unsaved work will be lost. Continue?",
            "Full restart", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(FastStartupService.TriggerFullRestart);
        if (!success) StatusMessage = $"Couldn't start a full restart: {error}";
        // No success message - a successful call tears this process down along with everything else.
    }

    /// <summary>#735: dismiss the "you haven't fully restarted" card for 7 days.</summary>
    private void DismissFullRestartPrompt()
    {
        FastStartupPromptSettingsService.DismissForSevenDays();
        _isFullRestartPromptDismissed = true;
        OnPropertyChanged(nameof(ShowFullRestartPrompt));
    }

    /// <summary>#736: `powercfg /hibernate off` - confirmed first.</summary>
    private async Task DisableHibernationAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\npowercfg /hibernate off\n\nThis also removes hiberfil.sys and disables Fast Startup (Fast Startup depends on hibernation). Continue?",
            "Turn off hibernation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await FastStartupService.SetHibernateEnabledAsync(false);
        StatusMessage = success ? "Hibernation turned off." : $"Couldn't turn off hibernation: {error}";
        if (success) LoadFastStartupAndHibernation();
    }

    /// <summary>#736: `powercfg /hibernate on` - confirmed first (the counterpart to the disable
    /// action above, offered once hibernation is off so it can be turned back on from here too).</summary>
    private async Task EnableHibernationAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\npowercfg /hibernate on\n\nTurn hibernation back on now?",
            "Turn on hibernation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await FastStartupService.SetHibernateEnabledAsync(true);
        StatusMessage = success ? "Hibernation turned on." : $"Couldn't turn on hibernation: {error}";
        if (success) LoadFastStartupAndHibernation();
    }

    /// <summary>#736: `powercfg /hibernate /type reduced` - confirmed first.</summary>
    private async Task ReduceHiberFileTypeAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\npowercfg /hibernate /type reduced\n\nShrinks hiberfil.sys to the smaller \"reduced\" type (still supports Fast Startup, but not full hibernate-to-disk). Continue?",
            "Reduce hiberfile type", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await FastStartupService.SetHiberFileTypeReducedAsync();
        StatusMessage = success ? "Hiberfile type set to reduced." : $"Couldn't change the hiberfile type: {error}";
        if (success) LoadFastStartupAndHibernation();
    }

    /// <summary>#736: `powercfg /hibernate /size &lt;n&gt;` from the HiberFileSizePercentInput text
    /// box - confirmed first.</summary>
    private async Task SetHiberFileSizeAsync()
    {
        if (!int.TryParse(HiberFileSizePercentInput, out int percent) || percent < 0 || percent > 100)
        {
            StatusMessage = "Enter a hiberfile size between 0 and 100 (percent of installed RAM).";
            return;
        }

        var confirm = MessageBox.Show(
            $"This will run:\n\npowercfg /hibernate /size {percent}\n\nSet the hiberfile size to {percent}% of installed RAM now?",
            "Set hiberfile size", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await FastStartupService.SetHiberFileSizeAsync(percent);
        StatusMessage = success ? $"Hiberfile size set to {percent}% of RAM." : $"Couldn't set the hiberfile size: {error}";
        if (success) LoadFastStartupAndHibernation();
    }

    /// <summary>#737: sets HiberbootEnabled to 0 - confirmed first.</summary>
    private async Task DisableFastStartupAsync()
    {
        var confirm = MessageBox.Show(
            "This turns off the \"Turn on fast startup\" option (the same registry value Control Panel's Power Options checkbox writes) - HiberbootEnabled will be set to 0. Every full shutdown will then be a real full shutdown. Continue?",
            "Turn off Fast Startup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await Task.Run(FastStartupService.DisableFastStartup);
        StatusMessage = success ? "Fast Startup turned off." : $"Couldn't turn off Fast Startup: {error}";
        if (success) LoadFastStartupAndHibernation();
    }

    /// <summary>#738-740: System partitions - one SystemPartitionService.ReadLayout() snapshot
    /// (WMI partition enumeration) plus the WinRE status read (reagentc /info), off the UI thread.
    /// The actual mount-based free-space measurements for the ESP/recovery partition are NOT run
    /// here - see MeasureEspFreeSpaceAsync/MeasureRecoveryFreeSpaceAsync, gated behind their own
    /// buttons since briefly mounting a partition is more invasive than a plain read.</summary>
    private void LoadSystemPartitions()
    {
        _ = Task.Run(async () =>
        {
            var layout = SystemPartitionService.ReadLayout();
            var winRe = await SystemPartitionService.ReadWinReStatusAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                PartitionLayout = layout;
                MeasureEspFreeSpaceCommand.RaiseCanExecuteChanged();
                MeasureRecoveryFreeSpaceCommand.RaiseCanExecuteChanged();

                EspHealth = layout.Esp is { } esp ? new EspHealthInfo { Partition = esp } : null;
                RecoveryPartitionFlag = SystemPartitionService.EvaluateRecoveryPartition(layout.Recovery, null, null);

                WinReStatus = winRe;
                EnableWinReCommand.RaiseCanExecuteChanged();
            });
        });
    }

    /// <summary>#738: on-demand ESP free-space measurement (temporary mountvol mount) - gated
    /// behind its own button, same "invasive step needs an explicit click" reasoning
    /// MeasureProfileSizeAsync (#720) already takes.</summary>
    private async Task MeasureEspFreeSpaceAsync()
    {
        var esp = PartitionLayout?.Esp;
        if (esp is null) return;

        var (freeBytes, error) = await SystemPartitionService.MeasureEspFreeSpaceAsync();
        EspHealth = new EspHealthInfo { Partition = esp, FreeBytes = freeBytes, MeasureError = error };
        if (error is not null) StatusMessage = $"Couldn't measure ESP free space: {error}";
    }

    /// <summary>#740: on-demand recovery-partition free-space measurement, feeding the
    /// too-small-for-servicing flag - same on-demand gating as the ESP above.</summary>
    private async Task MeasureRecoveryFreeSpaceAsync()
    {
        var recovery = PartitionLayout?.Recovery;
        if (recovery is null) return;

        var (freeBytes, error) = await SystemPartitionService.MeasurePartitionFreeSpaceAsync(recovery);
        RecoveryPartitionFlag = SystemPartitionService.EvaluateRecoveryPartition(recovery, freeBytes, error);
        if (error is not null) StatusMessage = $"Couldn't measure recovery partition free space: {error}";
    }

    /// <summary>#743-746: Active Setup component inventory, the Winlogon shell-chain integrity
    /// check, the IFEO hijack audit, and the global DLL injection audit - one Task.Run off the UI
    /// thread like every other on-demand read this tab does at load/refresh time. #743/#744 are
    /// plain registry reads; #745 additionally enumerates running processes per flagged exe (for
    /// the "View in Processes" cross-link) and #746 reads a couple of files off disk for signature
    /// checks, so all four are grouped into one background pass rather than four separate ones.</summary>
    private void LoadAutostartAudits()
    {
        _ = Task.Run(() =>
        {
            var activeSetup = ActiveSetupService.List();
            var winlogonChecks = WinlogonIntegrityService.Read();
            var ifeoEntries = ImageFileExecutionOptionsService.Read();
            var dllAudit = DllInjectionAuditService.Read();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                ActiveSetupComponents.Clear();
                foreach (var c in activeSetup) ActiveSetupComponents.Add(c);

                WinlogonChecks.Clear();
                foreach (var w in winlogonChecks) WinlogonChecks.Add(w);

                ImageFileExecutionOptionsEntries.Clear();
                foreach (var i in ifeoEntries) ImageFileExecutionOptionsEntries.Add(i);

                DllInjectionAudit = dllAudit;
            });
        });
    }

    /// <summary>#747: boot/logon-triggered scheduled tasks, folded into the main Items grid as
    /// first-class rows - schtasks can take a moment on a system with hundreds of tasks, so these
    /// rows appear once the scan completes rather than blocking the synchronous registry-based
    /// rows Refresh() already populated. Runs the same #91/#22 delay/impact scan and #18 signature
    /// check the rest of the grid gets, so a still-running boot/logon task looks like every other
    /// row rather than a second-class one.</summary>
    private async Task LoadScheduledTaskStartupRowsAsync()
    {
        List<ScheduledTaskTriggerInfo> triggered;
        try
        {
            triggered = await ScheduledTaskService.ListBootAndLogonTriggeredAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load boot/logon-triggered scheduled tasks: {ex.Message}";
            return;
        }

        if (triggered.Count == 0) return;

        var newItems = triggered.Select(t => new StartupItem
        {
            Name = t.TaskName,
            Command = t.Command,
            Source = StartupSource.ScheduledTaskTrigger,
            IsEnabled = t.IsEnabled,
        }).ToList();

        foreach (var item in newItems) Items.Add(item);

        _ = Task.Run(() =>
        {
            var measurements = StartupDelayService.ComputeDelays(newItems);
            var statuses = newItems.ToDictionary(item => item, item => SignatureCheckService.GetStatus(StartupManagerService.ExtractPath(item.Command)));
            var historyStats = StartupHistoryService.RecordAndCompute(measurements.Select(kv => (kv.Key.Name, kv.Value.DelaySeconds)));

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, measurement) in measurements)
                {
                    item.MeasuredDelayText = measurement.DelayText;
                    item.ImpactText = measurement.ImpactText;
                    item.ImpactDetailText = measurement.ImpactDetailText;
                    if (historyStats.TryGetValue(item.Name, out var stats))
                    {
                        item.MedianDelayText = stats.MedianText;
                        item.SparklinePointsText = stats.SparklinePointsText;
                        item.DelayTrendFlag = stats.TrendFlag;
                    }
                }
                foreach (var (item, status) in statuses) item.SignatureStatus = status;
            });
        });
    }

    /// <summary>#747: toggles a merged scheduled-task row via ScheduledTaskService.SetEnabledAsync
    /// (schtasks /change) instead of StartupManagerService's StartupApproved flag-flip - see
    /// Toggle()'s branch for why these rows need a different code path.</summary>
    private async Task ToggleScheduledTaskStartupItemAsync(StartupItem item, bool newState)
    {
        var (success, error) = await ScheduledTaskService.SetEnabledAsync(item.Name, newState);
        if (success)
        {
            item.IsEnabled = newState;
            StatusMessage = $"{item.Name} {(newState ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusMessage = $"Couldn't change {item.Name}: {error}";
        }
    }

    /// <summary>#739: `reagentc /enable` - confirmed first.</summary>
    private async Task EnableWinReAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\nreagentc /enable\n\nEnable the Windows Recovery Environment now?",
            "Enable Windows RE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await SystemPartitionService.EnableWinReAsync();
        if (success)
        {
            StatusMessage = "Windows RE enabled.";
            WinReStatus = await SystemPartitionService.ReadWinReStatusAsync();
            EnableWinReCommand.RaiseCanExecuteChanged();
        }
        else
        {
            StatusMessage = $"Couldn't enable Windows RE: {error}";
        }
    }

    /// <summary>#731: always takes a fresh BCD export immediately before any mutating BCD action -
    /// best-effort (a failed backup doesn't block the actual mutation the user already confirmed;
    /// StatusMessage isn't touched here so it doesn't clobber the mutation's own result message).
    /// Restore is never automated by this app - see BcdBackupEntry.ImportCommandText.</summary>
    private static async Task BackupBeforeMutationAsync()
    {
        try { await BcdInspectorService.ExportBackupAsync(); }
        catch { /* best-effort - see remarks above */ }
    }

    /// <summary>#725: "one-click clear this flag" - confirms the exact bcdedit command that will
    /// run before running it, matching CLAUDE.md's "mutating actions require explicit
    /// confirmation" rule and this app's existing confirm-then-act pattern (see
    /// ProcessesViewModel.EndSelected).</summary>
    private async Task ClearBootModeFlagAsync(BootModeFlag? flag)
    {
        if (flag is null) return;

        var confirm = MessageBox.Show(
            $"This will run:\n\n{flag.ClearCommandText}\n\nClear this boot-mode flag now? This takes effect on the next boot.",
            "Clear boot-mode flag", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await BackupBeforeMutationAsync();
        var (success, error) = await BcdInspectorService.ClearBootModeFlagAsync(flag);
        StatusMessage = success
            ? $"Cleared {flag.OptionName}. Takes effect on the next boot."
            : $"Couldn't clear {flag.OptionName}: {error}";
        if (success) LoadBcdInspector();
    }

    /// <summary>#728: restores bootstatuspolicy/recoveryenabled to Windows defaults - confirmed
    /// with both commands shown up front.</summary>
    private async Task RestoreBootStatusPolicyAsync()
    {
        if (BcdStore?.CurrentEntry is not { } current) return;
        string id = current.Identifier;

        var confirm = MessageBox.Show(
            $"This will run:\n\nbcdedit /deletevalue {id} bootstatuspolicy\nbcdedit /set {id} recoveryenabled yes\n\nRestore boot status policy and Startup Repair to Windows defaults now?",
            "Restore boot status policy defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await BackupBeforeMutationAsync();
        var (success, error) = await BcdInspectorService.RestoreBootStatusPolicyDefaultsAsync(id);
        StatusMessage = success ? "Boot status policy restored to Windows defaults." : $"Couldn't restore boot status policy: {error}";
        if (success) LoadBcdInspector();
    }

    /// <summary>#729: boot menu timeout, from the BootTimeoutInput text box.</summary>
    private async Task SetBootTimeoutAsync()
    {
        if (!int.TryParse(BootTimeoutInput, out int seconds) || seconds < 0 || seconds > 999)
        {
            StatusMessage = "Enter a boot menu timeout between 0 and 999 seconds.";
            return;
        }

        var confirm = MessageBox.Show(
            $"This will run:\n\nbcdedit /timeout {seconds}\n\nChange the boot menu timeout to {seconds}s now?",
            "Change boot menu timeout", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await BackupBeforeMutationAsync();
        var (success, error) = await BcdInspectorService.SetTimeoutAsync(seconds);
        StatusMessage = success ? $"Boot menu timeout set to {seconds}s." : $"Couldn't set boot timeout: {error}";
        if (success) LoadBcdInspector();
    }

    /// <summary>#729: default boot entry, one button per DisplayOrder row (CommandParameter is
    /// that row's BootMenuEntryRef).</summary>
    private async Task SetDefaultEntryAsync(BootMenuEntryRef? entry)
    {
        if (entry is null) return;

        var confirm = MessageBox.Show(
            $"This will run:\n\nbcdedit /default {entry.Identifier}\n\nSet \"{entry.Description}\" as the default boot entry now?",
            "Change default boot entry", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await BackupBeforeMutationAsync();
        var (success, error) = await BcdInspectorService.SetDefaultEntryAsync(entry.Identifier);
        StatusMessage = success ? $"Default boot entry set to \"{entry.Description}\"." : $"Couldn't set default entry: {error}";
        if (success) LoadBcdInspector();
    }

    /// <summary>#730: copies the ready-to-run `bcdedit /set {fwbootmgr} displayorder ...` fix
    /// command to the clipboard - never run automatically (this app makes no firmware NVRAM
    /// writes itself, see FirmwareBootOrderInfo's remarks).</summary>
    private void CopyFirmwareDisplayOrderFix()
    {
        if (FirmwareBootOrder?.SuggestedFixCommand is not { } cmd) return;
        try
        {
            Clipboard.SetText(cmd);
            StatusMessage = "Fix command copied to clipboard.";
        }
        catch
        {
            StatusMessage = "Couldn't copy to clipboard.";
        }
    }

    /// <summary>#731: one-click `bcdedit /export` - the matching `bcdedit /import` command is
    /// shown in plain text alongside each listed backup (see BcdBackupEntry.ImportCommandText);
    /// restore is never automated.</summary>
    private async Task ExportBcdBackupAsync()
    {
        var (success, path, error) = await BcdInspectorService.ExportBackupAsync();
        if (success)
        {
            StatusMessage = $"BCD exported to {path}. To restore: bcdedit /import \"{path}\"";
            BcdBackups.Clear();
            foreach (var b in BcdInspectorService.ListBackups()) BcdBackups.Add(b);
        }
        else
        {
            StatusMessage = $"Couldn't export BCD backup: {error}";
        }
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

            // #765/#766: known-code decode (a dictionary lookup) and missing-target resolution (a
            // bounded-timeout File.Exists check) are both cheap enough to run for every row up
            // front, unlike the logon-delay/trigger-summary XML fetch below which stays on-demand
            // per selected row. Runs off the UI thread with bounded concurrency, the same shape
            // #757's bulk recovery-action audit uses - these rows aren't in ScheduledTasks yet
            // (nothing is bound to them), so setting their properties from a background thread here
            // is safe.
            await Task.Run(() =>
            {
                Parallel.ForEach(tasks, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
                {
                    t.LastResultDecodedText = ScheduledTaskService.DecodeKnownLastResult(t.LastResult) ?? string.Empty;
                    var (missing, reason) = ScheduledTaskService.EvaluateTarget(t.TaskToRun);
                    t.HasMissingTarget = missing;
                    t.MissingTargetReason = reason;
                });
            });

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

    /// <summary>#765: certutil fallback decode for the selected task's LastResult, when it isn't one
    /// of the small known-codes table (already auto-populated by LoadScheduledTasksAsync).</summary>
    private async Task DecodeLastResultAsync()
    {
        var target = SelectedScheduledTask;
        if (target is null) return;
        target.LastResultDecodedText = await ScheduledTaskService.DecodeLastResultAsync(target.LastResult);
    }

    /// <summary>#764: `wevtutil sl "Microsoft-Windows-TaskScheduler/Operational" /e:true` - confirmed
    /// first (CLAUDE.md's "mutating actions require confirmation with the exact command shown"),
    /// never enabled silently. Once enabled, immediately loads whatever failure history already
    /// exists in the channel (there may be none yet - a freshly-enabled channel only captures events
    /// going forward).</summary>
    private async Task EnableTaskFailureLogAsync()
    {
        var confirm = MessageBox.Show(
            "This will run:\n\nwevtutil sl \"Microsoft-Windows-TaskScheduler/Operational\" /e:true\n\n" +
            "This turns on a normally-disabled Windows event log channel that records Task Scheduler " +
            "failures and per-run timing going forward. It does not run automatically - continue?",
            "Enable Task Scheduler operational log",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await EventLogService.EnableTaskSchedulerOperationalLogAsync();
        IsTaskSchedulerLogEnabled = EventLogService.IsTaskSchedulerOperationalLogEnabled();
        EnableTaskFailureLogCommand.RaiseCanExecuteChanged();

        StatusMessage = success
            ? "Task Scheduler operational log enabled. Failures and run durations will be tracked going forward."
            : $"Couldn't enable the Task Scheduler operational log: {error}";

        if (success) await LoadTaskFailuresAsync();
    }

    /// <summary>#764/#768: mines the Task Scheduler operational channel for failure events and pairs
    /// start/completion events into per-run durations, ranking the worst offenders into the chart
    /// under the grid - one Task.Run covers both, the "share the reader" note this domain's design
    /// asks for (both reads use the same EventLogQuery-based channel access).</summary>
    private async Task LoadTaskFailuresAsync()
    {
        TaskFailuresStatus = "Scanning the Task Scheduler operational log…";
        var (failures, durations) = await Task.Run(() =>
            (_eventLog.ReadTaskFailureEvents(), _eventLog.ReadTaskRunDurations()));

        TaskFailures.Clear();
        foreach (var f in failures) TaskFailures.Add(f);

        var worst = durations
            .GroupBy(d => d.TaskName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { TaskName = g.Key, MaxMs = g.Max(d => d.DurationMs) })
            .OrderByDescending(g => g.MaxMs)
            .Take(10)
            .ToList();

        WorstTaskDurationsMs.Clear();
        foreach (var w in worst) WorstTaskDurationsMs.Add(w.MaxMs);
        WorstTaskDurationsXAxes[0].Labels = worst.Select(w => w.TaskName.Length > 24 ? w.TaskName[^24..] : w.TaskName).ToArray();

        TaskFailuresStatus = IsTaskSchedulerLogEnabled
            ? $"Found {failures.Count} task failure event(s) and {durations.Count} timed run(s) in the last 30 days."
            : "The Task Scheduler operational log is disabled, so there's no history to read yet - enable it above.";
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
            var (delayText, runModeText, triggerSummaryText) = await ScheduledTaskService.ReadLogonTriggerInfoAsync(target.Name);
            target.DelayText = delayText;
            target.RunModeText = runModeText;
            target.TriggerSummaryText = triggerSummaryText; // #767
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
        // #748: the same scan's numeric per-item delay also feeds StartupHistoryService, which
        // persists it to startup-history.json and hands back this item's median/sparkline/growth
        // flag over its retained sample history - written once per scan, not on any timer.
        _ = Task.Run(() =>
        {
            var measurements = StartupDelayService.ComputeDelays(items);
            var historyStats = StartupHistoryService.RecordAndCompute(measurements.Select(kv => (kv.Key.Name, kv.Value.DelaySeconds)));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, measurement) in measurements)
                {
                    item.MeasuredDelayText = measurement.DelayText;
                    item.ImpactText = measurement.ImpactText;
                    item.ImpactDetailText = measurement.ImpactDetailText;
                    if (historyStats.TryGetValue(item.Name, out var stats))
                    {
                        item.MedianDelayText = stats.MedianText;
                        item.SparklinePointsText = stats.SparklinePointsText;
                        item.DelayTrendFlag = stats.TrendFlag;
                    }
                }
            });
        });

        // #18: signed/unsigned trust badge, reusing SignatureCheckService's shared per-path cache
        // (extracted from ProcessMonitorService in Round 2) rather than a duplicate check - also
        // off the UI thread, since a first-time signature check reads the file from disk.
        // #837: publisher/self-signed piggyback on this same per-item background pass.
        _ = Task.Run(() =>
        {
            var results = items.ToDictionary(item => item, item =>
            {
                var path = StartupManagerService.ExtractPath(item.Command);
                var status = SignatureCheckService.GetStatus(path);
                var signer = SignatureCheckService.GetSignerInfo(path);
                return (status, publisher: signer.SubjectCn ?? signer.IssuerCn ?? "Unknown", signer.SelfSigned);
            });
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, result) in results)
                {
                    item.SignatureStatus = result.status;
                    item.Publisher = result.publisher;
                    item.IsSelfSigned = result.SelfSigned;
                }
            });
        });

        // #747: boot/logon-triggered scheduled tasks, folded into this same grid as first-class
        // rows - see LoadScheduledTaskStartupRowsAsync's remarks for why this rides alongside
        // Refresh() rather than needing its own button.
        _ = LoadScheduledTaskStartupRowsAsync();
    }

    private void Toggle(StartupItem? item)
    {
        if (item is null) return;

        bool newState = !item.IsEnabled;

        // #747: a merged scheduled-task row has no StartupApproved flag to flip - it's toggled via
        // schtasks /change instead (ScheduledTaskService.SetEnabledAsync), same as the standalone
        // Scheduled Tasks grid's own ToggleScheduledTaskAsync above.
        if (item.Source == StartupSource.ScheduledTaskTrigger)
        {
            _ = ToggleScheduledTaskStartupItemAsync(item, newState);
            return;
        }

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
