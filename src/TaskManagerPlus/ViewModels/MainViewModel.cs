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
    public EnergyThermalsViewModel EnergyThermals { get; } = new();

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
        // Cpu/Memory/Storage/Network/Logging are constructed before Summary, since Summary's
        // Health Check card (#64) needs a live Network reference to read from.
        Cpu = new CpuViewModel(Performance);
        Memory = new MemoryViewModel(Performance, Processes);
        Storage = new StorageViewModel(Performance);
        Network = new NetworkViewModel(Performance);
        Logging = new LoggingViewModel(Performance, EnergyThermals);
        Summary = new SummaryViewModel(Performance, Processes, Services, EnergyThermals, SystemSpecs, Network);

        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);

        ApplyThemeToPerformance();
        Theme.ColorsChanged += ApplyThemeToPerformance;

        ApplyAxisThemeToPerformance();
        Theme.ThemeModeChanged += ApplyAxisThemeToPerformance;

        ApplyAxisThemeToEnergyThermals();
        Theme.ThemeModeChanged += ApplyAxisThemeToEnergyThermals;
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

    public void Dispose()
    {
        Theme.ColorsChanged -= ApplyThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToPerformance;
        Theme.ThemeModeChanged -= ApplyAxisThemeToEnergyThermals;
        Processes.Dispose();
        Performance.Dispose();
        Services.Dispose();
        EnergyThermals.Dispose();
        Network.Dispose();
        Logging.Dispose();
        Summary.Dispose();
    }
}
