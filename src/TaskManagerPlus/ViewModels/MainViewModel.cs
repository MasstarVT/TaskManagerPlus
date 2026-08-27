using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using TaskManagerPlus.Common;

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

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public RelayCommand ToggleSettingsCommand { get; }

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

        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);

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

    public void Dispose()
    {
        Theme.ColorsChanged -= ApplyThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToEnergyThermals;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStability;
        Theme.ThemeModeChanged -= ApplyAxisThemeToStartup;
        Processes.Dispose();
        Performance.Dispose();
        Services.Dispose();
        EnergyThermals.Dispose();
        Cpu.Dispose();
        Network.Dispose();
        Logging.Dispose();
        Summary.Dispose();
    }
}
