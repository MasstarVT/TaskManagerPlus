using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>Row shown in the memory-modules / GPU / disk lists - a display string plus a size for the right-aligned column.</summary>
public sealed class SpecRow
{
    public string Primary { get; init; } = string.Empty;
    public string Secondary { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
}

public sealed class SystemSpecsViewModel : ObservableObject
{
    private readonly SystemSpecsService _service = new();

    public ObservableCollection<SpecRow> MemoryModules { get; } = new();
    public ObservableCollection<SpecRow> Gpus { get; } = new();
    public ObservableCollection<SpecRow> Disks { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string _osName = string.Empty;
    public string OsName { get => _osName; private set => SetProperty(ref _osName, value); }

    private string _osDetails = string.Empty;
    public string OsDetails { get => _osDetails; private set => SetProperty(ref _osDetails, value); }

    private string _computerName = string.Empty;
    public string ComputerName { get => _computerName; private set => SetProperty(ref _computerName, value); }

    private string _systemModel = string.Empty;
    public string SystemModel { get => _systemModel; private set => SetProperty(ref _systemModel, value); }

    private string _systemType = string.Empty;
    public string SystemType { get => _systemType; private set => SetProperty(ref _systemType, value); }

    private string _motherboard = string.Empty;
    public string Motherboard { get => _motherboard; private set => SetProperty(ref _motherboard, value); }

    private string _biosVersion = string.Empty;
    public string BiosVersion { get => _biosVersion; private set => SetProperty(ref _biosVersion, value); }

    private string _cpuName = string.Empty;
    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }

    private string _cpuDetails = string.Empty;
    public string CpuDetails { get => _cpuDetails; private set => SetProperty(ref _cpuDetails, value); }

    private string _ramTotal = string.Empty;
    public string RamTotal { get => _ramTotal; private set => SetProperty(ref _ramTotal, value); }

    private string _ramDetails = string.Empty;
    public string RamDetails { get => _ramDetails; private set => SetProperty(ref _ramDetails, value); }

    public AsyncRelayCommand RefreshCommand { get; }

    public SystemSpecsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var specs = await Task.Run(() => _service.Query());
            Apply(specs);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(SystemSpecs specs)
    {
        OsName = string.IsNullOrWhiteSpace(specs.OsName) ? "Unknown OS" : specs.OsName;
        OsDetails = string.Join("  •  ", new[]
        {
            string.IsNullOrWhiteSpace(specs.OsVersion) ? null : $"Version {specs.OsVersion}",
            string.IsNullOrWhiteSpace(specs.OsArchitecture) ? null : specs.OsArchitecture,
            string.IsNullOrWhiteSpace(specs.OsInstallDate) ? null : $"Installed {specs.OsInstallDate}",
        }.Where(s => s is not null));

        ComputerName = specs.ComputerName;
        SystemModel = string.Join(" ", new[] { specs.Manufacturer, specs.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(SystemModel)) SystemModel = "Unknown model";
        SystemType = specs.SystemType;

        Motherboard = string.Join(" ", new[] { specs.MotherboardManufacturer, specs.MotherboardProduct }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(Motherboard)) Motherboard = "Unknown motherboard";
        BiosVersion = string.IsNullOrWhiteSpace(specs.BiosVersion) ? "Unknown" : specs.BiosVersion;

        CpuName = string.IsNullOrWhiteSpace(specs.CpuName) ? "Unknown CPU" : specs.CpuName;
        CpuDetails = $"{specs.CpuPhysicalCores} cores, {specs.CpuLogicalProcessors} logical processors  •  Max speed {specs.CpuMaxClockGhz:0.00} GHz";

        RamTotal = Formatting.FormatBytes(specs.RamTotalBytes);
        RamDetails = specs.MemoryModules.Count switch
        {
            0 => "No modules detected",
            1 => "1 module installed",
            var n => $"{n} modules installed",
        };

        MemoryModules.Clear();
        foreach (var m in specs.MemoryModules)
        {
            MemoryModules.Add(new SpecRow
            {
                Primary = m.Location,
                Secondary = string.Join(" ", new[]
                {
                    m.MemoryType,
                    m.SpeedMhz > 0 ? $"{m.SpeedMhz:0} MHz" : null,
                    m.Manufacturer,
                }.Where(s => !string.IsNullOrWhiteSpace(s))),
                SizeText = Formatting.FormatBytes(m.CapacityBytes),
            });
        }

        Gpus.Clear();
        foreach (var g in specs.Gpus)
        {
            Gpus.Add(new SpecRow
            {
                Primary = string.IsNullOrWhiteSpace(g.Name) ? "Unknown GPU" : g.Name,
                Secondary = string.IsNullOrWhiteSpace(g.DriverVersion) ? string.Empty : $"Driver {g.DriverVersion}",
                SizeText = g.AdapterRamBytes > 0 ? Formatting.FormatBytes(g.AdapterRamBytes) : string.Empty,
            });
        }

        Disks.Clear();
        foreach (var d in specs.Disks)
        {
            Disks.Add(new SpecRow
            {
                Primary = string.IsNullOrWhiteSpace(d.Model) ? "Unknown disk" : d.Model,
                Secondary = string.Join(" ", new[] { d.MediaType, d.InterfaceType }.Where(s => !string.IsNullOrWhiteSpace(s))),
                SizeText = Formatting.FormatBytes(d.SizeBytes),
            });
        }
    }
}
