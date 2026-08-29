using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public ThemeViewModel Theme { get; } = new();

    // #401/#406: cross-restart per-process history and the pinned leak-watch sampler - both
    // field-initialized (no dependencies of their own) so they're ready before ProcessesViewModel
    // needs them below.
    public ProcessHistoryService ProcessHistory { get; } = new();
    public LeakWatchViewModel LeakWatch { get; } = new();

    public ProcessesViewModel Processes { get; }
    public PerformanceViewModel Performance { get; } = new();
    public ServicesViewModel Services { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public SystemSpecsViewModel SystemSpecs { get; } = new();
    // #711: now takes EnergyThermals (constructed via the parameter version of its constructor
    // below) for thermal-throttle-vs-crash correlation - see StabilityViewModel's own remarks.
    public StabilityViewModel Stability { get; }

    // #769-800: new 13th top-level tab - modeled directly on StabilityViewModel (on-demand, no
    // DispatcherTimer) since event-log scans/registry sweeps/DISM calls aren't cheap enough to
    // repeat on a tick, the same tradeoff every other on-demand tab in this app already makes.
    public WindowsHealthViewModel WindowsHealth { get; } = new();

    // #453: Devices & Drivers - on-demand like Stability/SystemSpecs above (driverquery + a full
    // Win32_PnPSignedDriver sweep + per-row registry reads aren't cheap enough to repeat on a tick).
    public DevicesDriversViewModel DevicesDrivers { get; } = new();

    // #916-927: the Health Check card's rules engine - one shared instance (one FileSystemWatcher,
    // one loaded rule set) between the live Health Check feed (SummaryViewModel) and the Settings
    // drawer's rule editor/test/import-export panel (RulesEditorViewModel). Constructed in the
    // constructor body (needs the already-initialized Performance instance for #920's sustained-
    // condition history lookups), not as a field initializer.
    public RulesEngineService RulesEngine { get; }

    public SummaryViewModel Summary { get; }
    public RulesEditorViewModel RulesEditor { get; }

    // #901: symptom-picker diagnostics tab - takes the shared Performance/Processes instances so
    // the "My PC is slow" branch reuses already-polled live data instead of re-sampling from
    // scratch (see TroubleshootViewModel's remarks).
    public TroubleshootViewModel Troubleshoot { get; }

    // #943: see the Attach() call in the constructor - logs thermal/throttle transitions to
    // thermal-events.jsonl for the Timeline panel's Thermal events lane.
    private readonly ThermalEventLogService _thermalEventLog = new();

    // #959-966: the always-on background health collector - started once here (alongside every
    // other always-on piece MainViewModel owns), independent of LoggingViewModel's user-started
    // CSV logger. Exposed publicly only so App-level code could reach it if ever needed; the
    // Background Health panel (Troubleshoot.BackgroundHealth) is its actual consumer.
    public BackgroundHealthCollectorService BackgroundHealthCollector { get; }

    // Round 13, #801: Security tab - on-demand, same shape as Startup/SystemSpecs/Stability
    // above. Not part of TabShortcutOrder below (Ctrl+1..9 only covers the first nine tabs today).
    public SecurityViewModel Security { get; } = new();

    // #101-108: full Event Viewer replacement tab - see EventsViewModel's remarks. Constructed in
    // the body (not a field initializer) since it needs Processes' already-live collection for
    // #106's PID -> process-name lookup.
    public EventsViewModel Events { get; }

    // Thin wrappers over the shared Performance sampler (see CpuViewModel's remarks) - the
    // CPU/Memory/Storage/Network tabs are split views of one underlying data source, not four
    // independent pollers.
    public CpuViewModel Cpu { get; }
    public MemoryViewModel Memory { get; }
    public StorageViewModel Storage { get; }
    public NetworkViewModel Network { get; }

    // Round 10: dedicated GPU tab (#53-56). Owns its own timer/sampler (dynamic "GPU Engine"/
    // "GPU Adapter Memory" perf-counter enumeration), unlike the four above - see GpuViewModel's
    // remarks, the same "doesn't fit the shared sampler" reasoning EnergyThermalsViewModel already
    // documents.
    public GpuViewModel Gpu { get; }

    // Owns its own timer/sampler (LibreHardwareMonitorLib), unlike the four above - see
    // EnergyThermalsViewModel's remarks.
    public EnergyThermalsViewModel EnergyThermals { get; }

    // New Responsiveness tab (suggestions.md #201-214) - DPC/ISR latency measurement. Owns a
    // lightweight always-on timer (per-core DPC/interrupt + DPC watchdog headroom) plus its own
    // Start/Stop-gated measurement session (kernel-trace sampling) - see ResponsivenessViewModel's
    // remarks for why this doesn't fit PerformanceViewModel's shared-sampler model either.
    // #245 (items 235-246): takes Processes so it can sum ProcessRow.GdiHandleCount/UserHandleCount
    // for the desktop-heap card without a second per-process syscall pass - built in the
    // constructor body (below), not a field initializer, since it needs Processes (itself a field
    // initializer declared above, so already a real instance by the time the body runs) rather than
    // the parameterless constructor every other field-initialized ViewModel above uses.
    public ResponsivenessViewModel Responsiveness { get; }

    public LoggingViewModel Logging { get; }

    // #695-#700: the Stress test panel (hosted inside the Energy & Thermals tab, see
    // StressTestPanel.xaml) - composed here rather than inside EnergyThermalsViewModel since it
    // needs both EnergyThermals and Gpu (TDR watch) already constructed, the same "compose after
    // its dependencies exist" shape Summary/Logging/Search already use below.
    public StressTestViewModel StressTest { get; }

    // #100: cross-tab search - see GlobalSearchViewModel's remarks.
    public GlobalSearchViewModel Search { get; }

    // #101: remote/read-only monitoring endpoint - see RemoteMonitorService's remarks. Off by
    // default and opt-in via the Settings drawer; the sample delegate reads already-polled state
    // off Performance/EnergyThermals rather than adding a second sampler.
    private readonly RemoteMonitorSettings _remoteMonitorSettings = RemoteMonitorSettingsService.Load();
    public RemoteMonitorService RemoteMonitor { get; }
    public RelayCommand ToggleRemoteMonitorCommand { get; }
    public int RemoteMonitorPort => _remoteMonitorSettings.Port;

    public bool IsRemoteMonitorEnabled
    {
        get => _remoteMonitorSettings.Enabled;
        set
        {
            if (_remoteMonitorSettings.Enabled == value) return;
            _remoteMonitorSettings.Enabled = value;
            RemoteMonitorSettingsService.Save(_remoteMonitorSettings);
            OnPropertyChanged();
            ApplyRemoteMonitorState();
        }
    }

    public string RemoteMonitorStatusText
    {
        get
        {
            if (!RemoteMonitor.IsRunning) return "Not running.";
            var addresses = RemoteMonitorService.LocalIPv4Addresses();
            // #97: append the configured token to the suggested URL so the status text itself is
            // the copy-pasteable address someone would actually open on their phone/tablet.
            string suffix = string.IsNullOrEmpty(_remoteMonitorSettings.Token) ? string.Empty : $"?token={_remoteMonitorSettings.Token}";
            return addresses.Count == 0
                ? $"Running on port {RemoteMonitor.Port}, but no LAN address was found."
                : $"Open one of these from another device: {string.Join(", ", addresses.Select(a => $"http://{a}:{RemoteMonitor.Port}/{suffix}"))}";
        }
    }

    /// <summary>Round 12, #97: optional shared token - see RemoteMonitorSettings.Token's remarks.
    /// Applies live (no restart of the listener needed) since RemoteMonitorService.RequiredToken
    /// is just read per-request.</summary>
    public string? RemoteMonitorToken
    {
        get => _remoteMonitorSettings.Token;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_remoteMonitorSettings.Token == normalized) return;
            _remoteMonitorSettings.Token = normalized;
            RemoteMonitorSettingsService.Save(_remoteMonitorSettings);
            RemoteMonitor.RequiredToken = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteMonitorStatusText));
        }
    }

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>#726: live safe-mode detection - read once here (safe mode can't change without
    /// a reboot, so there's nothing to poll), drives a persistent header strip visible on every
    /// tab (see MainWindow.xaml) rather than something scoped to the Startup tab alone.</summary>
    public SafeModeInfo SafeMode { get; } = SafeModeDetectionService.Detect();

    /// <summary>#734: the footer status bar's uptime text - "Uptime 3h" normally, or "Uptime 3h —
    /// but 41 days since your last full restart" once Startup.FastStartupInfo says Fast Startup is
    /// on and the two figures meaningfully disagree (see FastStartupInfo.UptimeReconciliationText).
    /// Composed here (not on StartupViewModel) since it replaces the same footer text that used to
    /// bind directly to Performance.Uptime - see the PropertyChanged wiring in the constructor for
    /// why this keeps ticking live even though FastStartupInfo itself is only read once per Startup
    /// tab load/refresh. Supersedes #659's own simpler FastStartupNoteText below for this same
    /// footer spot (kept as an unused property here in case another card wants a plain on/off
    /// read) since this already reconciles the exact day-count disagreement, not just a static note.</summary>
    public string FooterUptimeText => Startup.FastStartupInfo is { } fastStartup
        ? fastStartup.UptimeReconciliationText(TimeSpan.FromMilliseconds(Environment.TickCount64))
        : $"Uptime {Performance.Uptime}";

    /// <summary>#659: Fast Startup ("hybrid boot") detection - a trivial one-time registry read
    /// (HibernationService.ReadFastStartupEnabled, HiberbootEnabled under
    /// HKLM\SYSTEM\CurrentControlSet\Control\Power), so it's read once here at construction rather
    /// than gated behind a button. Null when the registry value isn't present at all, not assumed
    /// off. Not wired to the footer itself (see FooterUptimeText above for why).</summary>
    public bool? FastStartupEnabled { get; } = HibernationService.ReadFastStartupEnabled();

    public string FastStartupNoteText => FastStartupEnabled == true
        ? "Fast Startup is on - a short uptime after Shut Down can still be a hybrid resume, not a true cold boot."
        : string.Empty;

    /// <summary>Round 12, #87: read-only "where is this app currently storing settings" status
    /// line for the Settings drawer - portable mode is a launch-time decision (AppPaths.Initialize,
    /// from App.xaml.cs), not something this drawer can toggle live.</summary>
    public string AppPathsModeText => AppPaths.IsPortable
        ? $"Portable ({AppPaths.SettingsDirectory})"
        : $"%AppData%\\TaskManagerPlus (normal mode - relaunch with --portable, or drop a portable.marker file next to the exe, for portable mode)";

    // suggestions.md #998: "technician mode" - portable-mode-only per-machine data partitioning.
    // A plain read of AppPaths' static state each time (not cached), since it can only actually
    // change via this same drawer's own controls.
    public bool IsPortableMode => AppPaths.IsPortable;
    public string CurrentMachineFingerprint => AppPaths.MachineFingerprint;

    private const string SharedFolderOption = "(shared/flat folder - default)";

    public List<string> MachineOptions => new[] { SharedFolderOption }.Concat(AppPaths.ListMachines()).ToList();

    /// <summary>Switching this takes effect on next launch - AppPaths.SwitchMachine's remarks
    /// explain why a live hot-swap isn't attempted (most services already cached their own settings
    /// path at construction, long before this could fire).</summary>
    public string SelectedMachineOption
    {
        get => AppPaths.SelectedMachine ?? SharedFolderOption;
        set
        {
            string? machine = value == SharedFolderOption ? null : value;
            if (AppPaths.SelectedMachine == machine) return;
            AppPaths.SwitchMachine(machine);
            OnPropertyChanged();
        }
    }

    public RelayCommand AddCurrentMachineCommand { get; }

    /// <summary>suggestions.md #1000: the discoverable "Keyboard shortcuts" sheet - a plain,
    /// non-owned Window construction directly from the ViewModel, the same pattern
    /// ToggleMiniDashboard already establishes for MiniDashboardWindow below.</summary>
    public RelayCommand OpenKeyboardShortcutsCommand { get; }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public RelayCommand ToggleSettingsCommand { get; }

    // Round 11, #80/#81: window-level preferences (always-on-top, Ctrl+1..9 tab shortcuts) - see
    // UiPreferences' remarks for why these live separately from ThemeColors.
    private readonly UiPreferences _uiPreferences = UiPreferencesService.Load();

    public bool AlwaysOnTop
    {
        get => _uiPreferences.AlwaysOnTop;
        set
        {
            if (_uiPreferences.AlwaysOnTop == value) return;
            _uiPreferences.AlwaysOnTop = value;
            UiPreferencesService.Save(_uiPreferences);
            OnPropertyChanged();
        }
    }

    /// <summary>Round 12, #85: minimize-to-tray toggle - see UiPreferences.MinimizeToTray's remarks.</summary>
    public bool MinimizeToTray
    {
        get => _uiPreferences.MinimizeToTray;
        set
        {
            if (_uiPreferences.MinimizeToTray == value) return;
            _uiPreferences.MinimizeToTray = value;
            UiPreferencesService.Save(_uiPreferences);
            OnPropertyChanged();
        }
    }

    /// <summary>Round 12, #86: Ctrl+Alt+T global hotkey opt-out - see UiPreferences.GlobalHotkeyEnabled's remarks.</summary>
    public bool GlobalHotkeyEnabled
    {
        get => _uiPreferences.GlobalHotkeyEnabled;
        set
        {
            if (_uiPreferences.GlobalHotkeyEnabled == value) return;
            _uiPreferences.GlobalHotkeyEnabled = value;
            UiPreferencesService.Save(_uiPreferences);
            OnPropertyChanged();
        }
    }

    /// <summary>#991: "Plain English mode" - see UiPreferences.PlainEnglishMode's remarks. Read
    /// from anywhere in the visual tree via AncestorType=Window (SummaryView's finding rows, the
    /// remediation review dialog, the Settings drawer's own copy of this toggle) rather than
    /// threading a UiPreferences reference through every ViewModel that renders a finding.</summary>
    public bool PlainEnglishMode
    {
        get => _uiPreferences.PlainEnglishMode;
        set
        {
            if (_uiPreferences.PlainEnglishMode == value) return;
            _uiPreferences.PlainEnglishMode = value;
            UiPreferencesService.Save(_uiPreferences);
            OnPropertyChanged();
        }
    }

    /// <summary>#999: "Offline mode" - see UiPreferences.OfflineMode's remarks. The three gated
    /// services each re-read UiPreferencesService.Load() at call time rather than watching this
    /// property, so the toggle takes effect immediately without any extra wiring.</summary>
    public bool OfflineMode
    {
        get => _uiPreferences.OfflineMode;
        set
        {
            if (_uiPreferences.OfflineMode == value) return;
            _uiPreferences.OfflineMode = value;
            UiPreferencesService.Save(_uiPreferences);
            OnPropertyChanged();
        }
    }

    /// <summary>Round 12, #88: read-only GitHub Releases update check, notify-only - see
    /// UpdateCheckService's remarks. Checked once on startup (Task.Run, no polling) rather than
    /// repeated, since a new release doesn't appear mid-session.</summary>
    private string? _updateAvailableText;
    public string? UpdateAvailableText { get => _updateAvailableText; private set => SetProperty(ref _updateAvailableText, value); }

    private string _updateUrl = "https://github.com/MasstarVT/TaskManagerPlus/releases/latest";
    public string UpdateUrl { get => _updateUrl; private set => SetProperty(ref _updateUrl, value); }

    public RelayCommand OpenUpdateUrlCommand { get; }

    // suggestions.md #997: "N unattended scans since you last looked, M new findings" - computed
    // once at startup (UnattendedScanService.CheckAndMarkSeen, a plain file-tracker comparison, not
    // a poller) and dismissible, same "quiet unless there's something to say" shape as the update
    // banner above.
    private string? _unattendedScanBannerText;
    public string? UnattendedScanBannerText { get => _unattendedScanBannerText; private set => SetProperty(ref _unattendedScanBannerText, value); }
    public RelayCommand DismissUnattendedScanBannerCommand { get; }

    // suggestions.md #997: "Set up nightly diagnostic scan" - a Settings-drawer frequency picker
    // (daily/weekly) plus an hour-of-day, backing ScheduledTaskService.CreateRecurringAsync via
    // UnattendedScanService. Not persisted as its own settings file - the created (or removed)
    // Scheduled Task itself is the durable state, same "the external artifact IS the state" shape
    // #979's queued-fix scheduled tasks already take.
    private bool _unattendedScanWeekly;
    public bool UnattendedScanWeekly { get => _unattendedScanWeekly; set => SetProperty(ref _unattendedScanWeekly, value); }

    private int _unattendedScanHour = 2;
    public int UnattendedScanHour { get => _unattendedScanHour; set => SetProperty(ref _unattendedScanHour, Math.Clamp(value, 0, 23)); }

    private bool _unattendedScanScrub;
    public bool UnattendedScanScrub { get => _unattendedScanScrub; set => SetProperty(ref _unattendedScanScrub, value); }

    private string _unattendedScanStatusText = string.Empty;
    public string UnattendedScanStatusText { get => _unattendedScanStatusText; private set => SetProperty(ref _unattendedScanStatusText, value); }

    public AsyncRelayCommand SetupUnattendedScanCommand { get; }
    public AsyncRelayCommand RemoveUnattendedScanCommand { get; }

    // Round 12, #85/#86: tray icon + global hotkey - both owned here (not MainWindow.xaml.cs
    // directly) so MainWindow just wires window events to these, keeping the P/Invoke and
    // WinForms-interop details out of the code-behind file.
    public GlobalHotkeyService Hotkey { get; } = new();

    /// <summary>#80: the tab header each of Ctrl+1..Ctrl+9 jumps to, in order - used when the user
    /// hasn't customized ui-preferences.json's TabShortcuts list. This is a flat list of leaf-tab
    /// names, not strip positions: since the strip became six groups (#1001-range UI overhaul),
    /// these names live one level down inside their groups and the list's order is unrelated to
    /// anything visual - it's simply "the nine (plus spares) most useful jumps". Only indices 0-8
    /// are ever read (MainWindow.xaml.cs's PreviewKeyDown handler only maps Ctrl+1..Ctrl+9); the
    /// names past index 8 are inert for the default order but kept so a customized
    /// ui-preferences.json still has the fuller name list to choose from.</summary>
    public static readonly string[] DefaultTabShortcutOrder =
        {
            "Summary", "CPU", "Memory", "Storage", "Network", "GPU", "Energy & Thermals", "Responsiveness", "Processes", "Services",
            "Startup", "System", "Stability", "Windows Health", "Troubleshoot",
        };

    public IReadOnlyList<string> TabShortcutOrder =>
        _uiPreferences.TabShortcuts.Count > 0 ? _uiPreferences.TabShortcuts : DefaultTabShortcutOrder;

    // #98/#99: pin-to-top compact overlay / second-monitor mini dashboard - one window instance,
    // toggled open/closed, rather than two separate features (a "pinned" main window and a
    // "detached" second window are the same small always-on-top view in practice).
    private Views.MiniDashboardWindow? _miniDashboard;
    public bool IsMiniDashboardOpen => _miniDashboard is not null;
    public RelayCommand ToggleMiniDashboardCommand { get; }

    public MainViewModel()
    {
        // Processes now needs ProcessHistory/LeakWatch (both already field-initialized above) for
        // #401/#406, so it's constructed here rather than as a field initializer of its own.
        Processes = new ProcessesViewModel(ProcessHistory, LeakWatch);

        // EnergyThermals now needs to be constructed before Cpu/Storage (both take a reference
        // to it - Cpu for its thermal-throttle flag, Storage for its per-drive temperature list -
        // see each view-model's remarks) and before Summary as before (#64's Health Check card).
        // #233: Cpu also takes Responsiveness (for its deep-idle-exit-latency flag). Responsiveness
        // itself is built here first (needing Processes, itself a field initializer already
        // constructed above) rather than as a field initializer, so it's a real instance by the
        // time Cpu/Network construct - the same "reach a sibling ViewModel via constructor
        // reference" pattern #221's Network/Responsiveness wiring already established.
        // #260: Responsiveness now also takes Performance (a field initializer above, so already a
        // real instance here) for the run-queue-pressure card's Processor Queue Length reading.
        Responsiveness = new ResponsivenessViewModel(Processes, Performance);
        // #261: gives the Processes tab read access to Responsiveness's shared scheduler sweep for
        // the per-process wait-reason breakdown - see ProcessesViewModel.Responsiveness's remarks
        // for why this is a settable property rather than a constructor parameter (Processes is
        // itself a field initializer, constructed before Responsiveness exists).
        Processes.Responsiveness = Responsiveness;
        EnergyThermals = new EnergyThermalsViewModel(Performance);
        Cpu = new CpuViewModel(Performance, EnergyThermals, Processes, Responsiveness);
        // #633: needs EnergyThermalsViewModel for the inferred non-stock-Vcore evidence input to
        // its combined undervolt/overclock instability flag.
        Stability = new StabilityViewModel(EnergyThermals);
        Memory = new MemoryViewModel(Performance, Processes, LeakWatch, ProcessHistory);
        Storage = new StorageViewModel(Performance, EnergyThermals, Processes);
        // #221: Responsiveness is a field initializer (declared/constructed before this
        // constructor body runs - see its own declaration above), so it's already a real instance
        // here, letting Network take the same "reach a sibling ViewModel via constructor reference"
        // pattern Cpu/Storage already take for EnergyThermals.
        Network = new NetworkViewModel(Performance, Responsiveness);
        // #678: takes Performance too now, for the bottleneck verdict's CPU-core-saturation evidence.
        Gpu = new GpuViewModel(Processes, EnergyThermals, Performance);
        Logging = new LoggingViewModel(Performance, EnergyThermals);
        StressTest = new StressTestViewModel(Performance, EnergyThermals, Gpu);
        // #295: Responsiveness is a field initializer (constructed above), so it's already a real
        // instance here - Summary reads Responsiveness.SystemScore directly rather than
        // duplicating the composite-score math. #storage: likewise for the DriveHealthVerdict tile.
        RulesEngine = new RulesEngineService(Performance);
        Summary = new SummaryViewModel(Performance, Processes, Services, EnergyThermals, SystemSpecs, Network, Stability, Responsiveness, Storage, ProcessHistory, RulesEngine);
        RulesEditor = new RulesEditorViewModel(RulesEngine, Performance, EnergyThermals, SystemSpecs, Services, Processes);
        BackgroundHealthCollector = new BackgroundHealthCollectorService(Performance, EnergyThermals, Services, Processes);
        Troubleshoot = new TroubleshootViewModel(Performance, Processes, Logging, EnergyThermals, SystemSpecs, Services, RulesEngine, BackgroundHealthCollector);
        // #1000: constructed after Summary/RulesEditor/Troubleshoot - the command-palette reach
        // extension needs live references to all three (current findings, loaded rules, Timeline/
        // Glossary sub-pages), not just the original four collections.
        Search = new GlobalSearchViewModel(Processes, Services, Startup, SystemSpecs, Summary, RulesEditor, Troubleshoot);
        Events = new EventsViewModel(Processes, Services);

        // #943: edge-triggered thermal/throttle event logging for the Timeline panel - a
        // PropertyChanged subscription on the already-constructed Cpu/EnergyThermals view-models,
        // not a new poll timer. Wired here (not inside TroubleshootViewModel) so it keeps logging
        // even if the Timeline panel is never opened this session.
        _thermalEventLog.Attach(Cpu, EnergyThermals);

        RemoteMonitor = new RemoteMonitorService(BuildRemoteMetricsSnapshot) { RequiredToken = _remoteMonitorSettings.Token };
        ToggleRemoteMonitorCommand = new RelayCommand(_ => IsRemoteMonitorEnabled = !IsRemoteMonitorEnabled);
        ApplyRemoteMonitorState();

        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleMiniDashboardCommand = new RelayCommand(_ => ToggleMiniDashboard());

        OpenUpdateUrlCommand = new RelayCommand(_ =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateUrl) { UseShellExecute = true }); }
            catch { /* best-effort - the banner text still shows the version either way */ }
        });
        _ = CheckForUpdateAsync();

        // suggestions.md #997: one plain synchronous file-tracker comparison at startup - cheap
        // (a directory listing plus a couple of small JSON reads), same cost class as
        // UiPreferencesService.Load() above, not worth a Task.Run.
        UnattendedScanBannerText = UnattendedScanService.CheckAndMarkSeen();
        DismissUnattendedScanBannerCommand = new RelayCommand(_ => UnattendedScanBannerText = null);
        SetupUnattendedScanCommand = new AsyncRelayCommand(async () =>
        {
            var frequency = UnattendedScanWeekly ? ScheduledTaskFrequency.Weekly : ScheduledTaskFrequency.Daily;
            var (success, error) = await UnattendedScanService.SetupScheduledScanAsync(
                frequency, TimeSpan.FromHours(UnattendedScanHour), DayOfWeek.Sunday, UnattendedScanScrub);
            UnattendedScanStatusText = success
                ? $"Scheduled: runs {(UnattendedScanWeekly ? "weekly (Sunday)" : "daily")} at {UnattendedScanHour:00}:00, writing results under {UnattendedScanService.UnattendedScansDirectory}."
                : $"Couldn't set up the scheduled task: {error}";
        });
        RemoveUnattendedScanCommand = new AsyncRelayCommand(async () =>
        {
            var (success, error) = await UnattendedScanService.RemoveScheduledScanAsync();
            UnattendedScanStatusText = success ? "Scheduled scan removed." : $"Couldn't remove the scheduled task: {error}";
        });

        AddCurrentMachineCommand = new RelayCommand(_ =>
        {
            AppPaths.RegisterCurrentMachine();
            OnPropertyChanged(nameof(MachineOptions));
            OnPropertyChanged(nameof(SelectedMachineOption));
        });

        OpenKeyboardShortcutsCommand = new RelayCommand(_ => new Views.KeyboardShortcutsWindow().Show());

        ApplyThemeToPerformance();
        Theme.ColorsChanged += ApplyThemeToPerformance;

        ApplyAxisThemeToPerformance();
        Theme.ThemeModeChanged += ApplyAxisThemeToPerformance;

        ApplyAxisThemeToEnergyThermals();
        Theme.ThemeModeChanged += ApplyAxisThemeToEnergyThermals;

        ApplyAxisThemeToCpu();
        Theme.ThemeModeChanged += ApplyAxisThemeToCpu;

        ApplyAxisThemeToStability();
        Theme.ThemeModeChanged += ApplyAxisThemeToStability;

        // Round 18, #371: Storage tab's event-153 retry-trend chart - same ColumnSeries theming
        // shape as Stability's Reliability History chart above.
        ApplyAxisThemeToStorage();
        Theme.ThemeModeChanged += ApplyAxisThemeToStorage;

        // #141: crash/error markers on the CPU/RAM/Disk/Network charts - reuses whatever
        // StabilityViewModel's own on-demand refresh already read, no new poll of its own. Applied
        // once now (in case Stability already finished its own initial fire-and-forget refresh
        // before this wiring ran) and again every time Stability refreshes after that.
        ApplyStabilityMarkersToPerformance();
        Stability.Refreshed += ApplyStabilityMarkersToPerformance;

        ApplyAxisThemeToStartup();
        Theme.ThemeModeChanged += ApplyAxisThemeToStartup;

        ApplyAxisThemeToLogging();
        Theme.ThemeModeChanged += ApplyAxisThemeToLogging;

        ApplyAxisThemeToResponsiveness();
        Theme.ThemeModeChanged += ApplyAxisThemeToResponsiveness;

        // #674: GPU tab's VRAM-spillover trend chart.
        ApplyAxisThemeToGpu();
        Theme.ThemeModeChanged += ApplyAxisThemeToGpu;

        ApplyAxisThemeToBaselines();
        Theme.ThemeModeChanged += ApplyAxisThemeToBaselines;

        ApplyAxisThemeToBackgroundHealth();
        Theme.ThemeModeChanged += ApplyAxisThemeToBackgroundHealth;

        // #734: keep the footer's uptime text live - Performance.Uptime ticks every second, and
        // Startup.FastStartupInfo changes once per Startup tab load/refresh, either of which
        // should refresh FooterUptimeText's bound text (same "re-raise a composed property when
        // one of its inputs changes" pattern MainWindow.xaml.cs's tray tooltip update uses).
        Performance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerformanceViewModel.Uptime)) OnPropertyChanged(nameof(FooterUptimeText)); };
        Startup.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StartupViewModel.FastStartupInfo)) OnPropertyChanged(nameof(FooterUptimeText)); };
    }

    private void ApplyThemeToPerformance()
        => Performance.ApplyColors(Theme.Cpu, Theme.Ram, Theme.Disk, Theme.NetworkReceive, Theme.NetworkSend);

    /// <summary>
    /// Chart axis text/gridlines and the network chart's legend/tooltip are SkiaSharp paints
    /// that live outside WPF's resource system, so a theme-family switch (DynamicResource-driven)
    /// can't reach them on its own - pull the freshly-repainted brushes straight out of the app's
    /// resource dictionary (the same one ThemeViewModel.ApplyPalette writes into) and push them
    /// into PerformanceViewModel.
    /// </summary>
    private void ApplyAxisThemeToPerformance()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Performance.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"), TextOf("BgElevatedBrush"));
    }

    private void ApplyAxisThemeToEnergyThermals()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        EnergyThermals.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    /// <summary>#603: repaints CpuViewModel's own throttle-dwell stacked-bar chart axes - same
    /// SkiaSharp-outside-WPF-resources gap as every other chart axis theme hook.</summary>
    private void ApplyAxisThemeToCpu()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Cpu.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    /// <summary>#674: repaints GpuViewModel's VRAM-spillover trend chart axes.</summary>
    private void ApplyAxisThemeToGpu()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Gpu.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToStability()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Stability.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToStorage()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Storage.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    /// <summary>#141: RecentEvents is already the Stability tab's own Critical/Error digest (see
    /// EventLogService.Query) - just reshaped into the (Time, Level, Text) tuple
    /// PerformanceViewModel.SetEventMarkers wants.</summary>
    private void ApplyStabilityMarkersToPerformance()
        => Performance.SetEventMarkers(Stability.RecentEvents.Select(e =>
            (e.TimeCreated, e.Level, $"{e.ProviderName} {e.EventId}")));

    private void ApplyAxisThemeToStartup()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Startup.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToResponsiveness()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Responsiveness.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
        // #300: incident-replay chart axes - same theming call shape, separate axis pair.
        Responsiveness.ApplyIncidentReplayAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToLogging()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Logging.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToBaselines()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Troubleshoot.Baselines.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToBackgroundHealth()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Troubleshoot.BackgroundHealth.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private RemoteMetricsSnapshot BuildRemoteMetricsSnapshot() => new()
    {
        MachineName = Environment.MachineName,
        TimestampUtc = DateTime.UtcNow,
        CpuPercent = Performance.CpuCurrentPercent,
        HasCpuTemp = EnergyThermals.CpuPackageTempC.HasValue,
        CpuTempC = EnergyThermals.CpuPackageTempC ?? 0,
        RamPercent = Performance.RamPercent,
        DiskPercent = Performance.DiskPercent,
        NetworkReceiveBps = Performance.NetworkReceiveBps,
        NetworkSendBps = Performance.NetworkSendBps,
        Uptime = Performance.Uptime,
    };

    private void ApplyRemoteMonitorState()
    {
        if (IsRemoteMonitorEnabled)
        {
            var (success, error) = RemoteMonitor.Start(_remoteMonitorSettings.Port);
            if (!success)
            {
                // Couldn't bind the port (already in use, blocked, ...) - don't silently claim
                // it's running; leave the toggle on (so the user sees it's supposed to be) but
                // the status text will show "Not running" since RemoteMonitor.IsRunning is false.
                System.Diagnostics.Debug.WriteLine($"RemoteMonitorService failed to start: {error}");
            }
        }
        else
        {
            RemoteMonitor.Stop();
        }
        OnPropertyChanged(nameof(RemoteMonitorStatusText));
    }

    /// <summary>#88: fires once at startup - see UpdateCheckService's remarks for why this is
    /// safe to await inline (network I/O is already async; a slow/offline check just leaves
    /// UpdateAvailableText null rather than blocking anything).</summary>
    private async Task CheckForUpdateAsync()
    {
        var (tag, url) = await UpdateCheckService.CheckForNewerReleaseAsync();
        if (tag is null) return;

        UpdateAvailableText = $"A newer version is available: {tag}";
        if (!string.IsNullOrWhiteSpace(url)) UpdateUrl = url!;
    }

    private void ToggleMiniDashboard()
    {
        if (_miniDashboard is not null)
        {
            _miniDashboard.Close();
            return;
        }

        _miniDashboard = new Views.MiniDashboardWindow(this);
        _miniDashboard.Closed += (_, _) =>
        {
            _miniDashboard = null;
            OnPropertyChanged(nameof(IsMiniDashboardOpen));
        };
        _miniDashboard.Show();
        OnPropertyChanged(nameof(IsMiniDashboardOpen));
    }

    public void Dispose()
    {
        Theme.ColorsChanged -= ApplyThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToEnergyThermals;
        Theme.ThemeModeChanged -= ApplyAxisThemeToCpu;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStability;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStorage;
        Stability.Refreshed -= ApplyStabilityMarkersToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStartup;
        Theme.ThemeModeChanged -= ApplyAxisThemeToLogging;
        Theme.ThemeModeChanged -= ApplyAxisThemeToResponsiveness;
        Theme.ThemeModeChanged -= ApplyAxisThemeToGpu;
        Theme.ThemeModeChanged -= ApplyAxisThemeToBaselines;
        Theme.ThemeModeChanged -= ApplyAxisThemeToBackgroundHealth;
        BackgroundHealthCollector.Dispose();
        Processes.Dispose();
        Memory.Dispose();
        Performance.Dispose();
        Services.Dispose();
        EnergyThermals.Dispose();
        Responsiveness.Dispose();
        Cpu.Dispose();
        Network.Dispose();
        Gpu.Dispose();
        Logging.Dispose();
        StressTest.Dispose();
        Summary.Dispose();
        Stability.Dispose();
        Events.Dispose();
        LeakWatch.Dispose();
        ProcessHistory.Flush();
        RulesEditor.Dispose();
        RulesEngine.Dispose();
        Troubleshoot.Dispose();
        _miniDashboard?.Close();
        RemoteMonitor.Dispose();
        Hotkey.Dispose();
    }
}
