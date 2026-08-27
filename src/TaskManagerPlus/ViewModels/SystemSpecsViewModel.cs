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

    /// <summary>SMART/WMI health badge text, disk rows only ("OK", "Failure predicted", ...).
    /// Empty for rows (memory/GPU) that don't have a health concept.</summary>
    public string HealthText { get; init; } = string.Empty;

    /// <summary>True when HealthText should render in the warning color.</summary>
    public bool IsHealthWarning { get; init; }
}

/// <summary>Row shown in the per-volume free-space list.</summary>
public sealed class VolumeRow
{
    public string Primary { get; init; } = string.Empty;
    public string Secondary { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public double PercentUsed { get; init; }

    /// <summary>True when the file system dirty bit is set (#29) - needs a chkdsk pass.</summary>
    public bool IsDirty { get; init; }
}

public sealed class SystemSpecsViewModel : ObservableObject
{
    private readonly SystemSpecsService _service = new();

    public ObservableCollection<SpecRow> MemoryModules { get; } = new();
    public ObservableCollection<SpecRow> Gpus { get; } = new();
    public ObservableCollection<SpecRow> Disks { get; } = new();
    public ObservableCollection<VolumeRow> Volumes { get; } = new();
    public ObservableCollection<SpecRow> OutdatedDrivers { get; } = new();
    public ObservableCollection<SpecRow> RecentUpdates { get; } = new();
    public ObservableCollection<SpecRow> AntivirusProducts { get; } = new();

    private bool _multipleActiveAvWarning;
    public bool MultipleActiveAvWarning { get => _multipleActiveAvWarning; private set => SetProperty(ref _multipleActiveAvWarning, value); }

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

    private string _secureBootText = "Unknown";
    public string SecureBootText { get => _secureBootText; private set => SetProperty(ref _secureBootText, value); }

    private bool _secureBootWarning;
    public bool SecureBootWarning { get => _secureBootWarning; private set => SetProperty(ref _secureBootWarning, value); }

    private string _tpmText = "Unknown";
    public string TpmText { get => _tpmText; private set => SetProperty(ref _tpmText, value); }

    private bool _tpmWarning;
    public bool TpmWarning { get => _tpmWarning; private set => SetProperty(ref _tpmWarning, value); }

    private string _vbsText = "Unknown";
    public string VbsText { get => _vbsText; private set => SetProperty(ref _vbsText, value); }

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
            string.IsNullOrWhiteSpace(specs.OsInstallDate) ? null
                : specs.OsInstallAgeDays is { } days ? $"Installed {specs.OsInstallDate} ({days:N0} days ago)" : $"Installed {specs.OsInstallDate}",
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
        // #16: "N of M slots populated" when the slot count is known - a quick, otherwise
        // invisible signal that there's room to add more RAM without an upgrade.
        RamDetails = specs.TotalMemorySlots is { } slots && slots > 0
            ? $"{specs.MemoryModules.Count} of {slots} slots populated"
            : specs.MemoryModules.Count switch
            {
                0 => "No modules detected",
                1 => "1 module installed",
                var n => $"{n} modules installed",
            };

        MemoryModules.Clear();
        foreach (var m in specs.MemoryModules)
        {
            // #16: flag when the module is running below its own rated speed (XMP/DOCP not
            // enabled) - a common, otherwise invisible "why is my PC slower than it should be" cause.
            string? speedText = m.SpeedMhz > 0
                ? (m.ConfiguredSpeedMhz > 0 && m.ConfiguredSpeedMhz < m.SpeedMhz
                    ? $"{m.ConfiguredSpeedMhz:0} MHz running (rated {m.SpeedMhz:0})"
                    : $"{m.SpeedMhz:0} MHz")
                : null;

            MemoryModules.Add(new SpecRow
            {
                Primary = m.Location,
                Secondary = string.Join(" ", new[] { m.MemoryType, speedText, m.Manufacturer }.Where(s => !string.IsNullOrWhiteSpace(s))),
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
                HealthText = d.HealthStatus,
                IsHealthWarning = d.IsHealthWarning,
            });
        }

        Volumes.Clear();
        foreach (var v in specs.Volumes)
        {
            Volumes.Add(new VolumeRow
            {
                Primary = v.Name,
                Secondary = v.Label,
                SizeText = $"{Formatting.FormatBytes(v.FreeBytes)} free of {Formatting.FormatBytes(v.TotalBytes)}",
                PercentUsed = v.PercentUsed,
                IsDirty = v.IsDirty == true,
            });
        }

        var security = specs.Security;
        (SecureBootText, SecureBootWarning) = security.SecureBootEnabled switch
        {
            true => ("On", false),
            false => ("Off", true),
            null => ("Unknown", false),
        };
        (TpmText, TpmWarning) = (security.TpmPresent, security.TpmReady) switch
        {
            (false, _) => ("Not present", true),
            (true, true) => (string.IsNullOrEmpty(security.TpmVersion) ? "Ready" : $"Ready (v{security.TpmVersion})", false),
            (true, false) => ("Present, not ready", true),
            (true, null) => (string.IsNullOrEmpty(security.TpmVersion) ? "Present" : $"Present (v{security.TpmVersion})", false),
            (null, _) => ("Unknown", false),
        };
        VbsText = security.VbsRunning switch
        {
            true => security.VbsServicesRunning.Count > 0 ? $"Running ({string.Join(", ", security.VbsServicesRunning)})" : "Running",
            false => "Off",
            null => "Unknown",
        };

        OutdatedDrivers.Clear();
        foreach (var d in specs.OutdatedDrivers)
        {
            OutdatedDrivers.Add(new SpecRow
            {
                Primary = d.DeviceName,
                Secondary = string.Join(" ", new[] { d.Manufacturer, d.DriverVersion }.Where(s => !string.IsNullOrWhiteSpace(s))),
                SizeText = d.DriverDate is { } date ? date.ToShortDateString() : string.Empty,
            });
        }

        RecentUpdates.Clear();
        foreach (var u in specs.RecentUpdates)
        {
            RecentUpdates.Add(new SpecRow
            {
                Primary = u.HotFixId,
                Secondary = u.Description,
                SizeText = u.InstalledOn is { } installed ? installed.ToShortDateString() : string.Empty,
            });
        }

        AntivirusProducts.Clear();
        foreach (var a in specs.AntivirusProducts)
        {
            AntivirusProducts.Add(new SpecRow
            {
                Primary = a.Name,
                HealthText = a.LooksEnabled ? "Active" : "Inactive",
                IsHealthWarning = !a.LooksEnabled,
            });
        }
        MultipleActiveAvWarning = specs.MultipleActiveAvWarning;
    }
}
