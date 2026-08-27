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
            return addresses.Count == 0
                ? $"Running on port {RemoteMonitor.Port}, but no LAN address was found."
                : $"Open one of these from another device: {string.Join(", ", addresses.Select(a => $"http://{a}:{RemoteMonitor.Port}/"))}";
        }
    }

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public RelayCommand ToggleSettingsCommand { get; }

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
        Cpu = new CpuViewModel(Performance, EnergyThermals);
        Memory = new MemoryViewModel(Performance, Processes);
        Storage = new StorageViewModel(Performance, EnergyThermals);
        Network = new NetworkViewModel(Performance);
        Logging = new LoggingViewModel(Performance, EnergyThermals);
        Summary = new SummaryViewModel(Performance, Processes, Services, EnergyThermals, SystemSpecs, Network, Stability);
        Search = new GlobalSearchViewModel(Processes, Services, Startup, SystemSpecs);

        RemoteMonitor = new RemoteMonitorService(BuildRemoteMetricsSnapshot);
        ToggleRemoteMonitorCommand = new RelayCommand(_ => IsRemoteMonitorEnabled = !IsRemoteMonitorEnabled);
        ApplyRemoteMonitorState();

        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleMiniDashboardCommand = new RelayCommand(_ => ToggleMiniDashboard());

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
        Logging.Dispose();
        Summary.Dispose();
        _miniDashboard?.Close();
        RemoteMonitor.Dispose();
    }
}
