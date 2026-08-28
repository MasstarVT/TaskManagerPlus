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
    public ProcessesViewModel Processes { get; } = new();
    public PerformanceViewModel Performance { get; } = new();
    public ServicesViewModel Services { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public SystemSpecsViewModel SystemSpecs { get; } = new();
    public StabilityViewModel Stability { get; } = new();
    public SummaryViewModel Summary { get; }

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

    public LoggingViewModel Logging { get; }

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

    /// <summary>Round 12, #87: read-only "where is this app currently storing settings" status
    /// line for the Settings drawer - portable mode is a launch-time decision (AppPaths.Initialize,
    /// from App.xaml.cs), not something this drawer can toggle live.</summary>
    public string AppPathsModeText => AppPaths.IsPortable
        ? $"Portable ({AppPaths.SettingsDirectory})"
        : $"%AppData%\\TaskManagerPlus (normal mode - relaunch with --portable, or drop a portable.marker file next to the exe, for portable mode)";

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

    /// <summary>Round 12, #88: read-only GitHub Releases update check, notify-only - see
    /// UpdateCheckService's remarks. Checked once on startup (Task.Run, no polling) rather than
    /// repeated, since a new release doesn't appear mid-session.</summary>
    private string? _updateAvailableText;
    public string? UpdateAvailableText { get => _updateAvailableText; private set => SetProperty(ref _updateAvailableText, value); }

    private string _updateUrl = "https://github.com/MasstarVT/TaskManagerPlus/releases/latest";
    public string UpdateUrl { get => _updateUrl; private set => SetProperty(ref _updateUrl, value); }

    public RelayCommand OpenUpdateUrlCommand { get; }

    // Round 12, #85/#86: tray icon + global hotkey - both owned here (not MainWindow.xaml.cs
    // directly) so MainWindow just wires window events to these, keeping the P/Invoke and
    // WinForms-interop details out of the code-behind file.
    public GlobalHotkeyService Hotkey { get; } = new();

    /// <summary>#80: the tab header each of Ctrl+1..Ctrl+9 jumps to, in order - falls back to this
    /// app's first nine tabs (in their normal strip order) when the user hasn't customized
    /// ui-preferences.json's TabShortcuts list.</summary>
    public static readonly string[] DefaultTabShortcutOrder =
        { "Summary", "CPU", "Memory", "Storage", "Network", "GPU", "Energy & Thermals", "Processes", "Services" };

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
        // EnergyThermals now needs to be constructed before Cpu/Storage (both take a reference
        // to it - Cpu for its thermal-throttle flag, Storage for its per-drive temperature list -
        // see each view-model's remarks) and before Summary as before (#64's Health Check card).
        EnergyThermals = new EnergyThermalsViewModel(Performance);
        Cpu = new CpuViewModel(Performance, EnergyThermals, Processes);
        Memory = new MemoryViewModel(Performance, Processes);
        Storage = new StorageViewModel(Performance, EnergyThermals);
        Network = new NetworkViewModel(Performance);
        Gpu = new GpuViewModel(Processes);
        Logging = new LoggingViewModel(Performance, EnergyThermals);
        Summary = new SummaryViewModel(Performance, Processes, Services, EnergyThermals, SystemSpecs, Network, Stability);
        Search = new GlobalSearchViewModel(Processes, Services, Startup, SystemSpecs);

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

        ApplyThemeToPerformance();
        Theme.ColorsChanged += ApplyThemeToPerformance;

        ApplyAxisThemeToPerformance();
        Theme.ThemeModeChanged += ApplyAxisThemeToPerformance;

        ApplyAxisThemeToEnergyThermals();
        Theme.ThemeModeChanged += ApplyAxisThemeToEnergyThermals;

        ApplyAxisThemeToStability();
        Theme.ThemeModeChanged += ApplyAxisThemeToStability;

        ApplyAxisThemeToStartup();
        Theme.ThemeModeChanged += ApplyAxisThemeToStartup;

        ApplyAxisThemeToLogging();
        Theme.ThemeModeChanged += ApplyAxisThemeToLogging;
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

    private void ApplyAxisThemeToStability()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Stability.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToStartup()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Startup.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
    }

    private void ApplyAxisThemeToLogging()
    {
        var resources = Application.Current.Resources;
        Color TextOf(string key) => (resources[key] as SolidColorBrush)?.Color ?? Colors.Gray;

        Logging.ApplyAxisTheme(TextOf("TextSecondaryBrush"), TextOf("BorderBrush2"));
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
        Theme.ThemeModeChanged -= ApplyAxisThemeToStability;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStartup;
        Theme.ThemeModeChanged -= ApplyAxisThemeToLogging;
        Processes.Dispose();
        Performance.Dispose();
        Services.Dispose();
        EnergyThermals.Dispose();
        Cpu.Dispose();
        Network.Dispose();
        Gpu.Dispose();
        Logging.Dispose();
        Summary.Dispose();
        _miniDashboard?.Close();
        RemoteMonitor.Dispose();
        Hotkey.Dispose();
    }
}
