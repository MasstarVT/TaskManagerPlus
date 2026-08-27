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

    // Round 9, #37/#40/#42/#44 - four more per-volume facts, see VolumeDiagnosticsService.
    public string BitLockerStatus { get; init; } = "Unknown";
    public bool BitLockerOn { get; init; }
    public string RecycleBinText { get; init; } = string.Empty;
    public string ShadowCopyText { get; init; } = string.Empty;
    public string TrimText { get; init; } = string.Empty;
    public bool TrimWarning { get; init; }

    /// <summary>RecycleBinText/ShadowCopyText/TrimText pre-joined (blank ones dropped) - simpler
    /// for the view to bind to than a WPF MultiBinding/converter for three optional strings.</summary>
    public string ExtraFactsText => string.Join("  •  ",
        new[] { RecycleBinText, ShadowCopyText, TrimText }.Where(s => !string.IsNullOrEmpty(s)));
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
    public ObservableCollection<SpecRow> RecentlyInstalledSoftware { get; } = new();
    public ObservableCollection<SpecRow> UsbDevices { get; } = new();

    private string _pageFileLocationText = string.Empty;
    public string PageFileLocationText { get => _pageFileLocationText; private set => SetProperty(ref _pageFileLocationText, value); }

    private bool _pageFileLocationWarning;
    public bool PageFileLocationWarning { get => _pageFileLocationWarning; private set => SetProperty(ref _pageFileLocationWarning, value); }

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

    // #92: BIOS age hint - "worth checking for an update", not a verified "update available" flag.
    private bool _biosAgeWarning;
    public bool BiosAgeWarning { get => _biosAgeWarning; private set => SetProperty(ref _biosAgeWarning, value); }

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

    // Round 10, #57/#58/#61: chassis form factor, Windows edition & activation, chipset driver.
    private string _chassisType = "Unknown";
    public string ChassisType { get => _chassisType; private set => SetProperty(ref _chassisType, value); }

    private string _activationStatus = "Unknown";
    public string ActivationStatus { get => _activationStatus; private set => SetProperty(ref _activationStatus, value); }

    private string _chipsetDriverText = "Unknown";
    public string ChipsetDriverText { get => _chipsetDriverText; private set => SetProperty(ref _chipsetDriverText, value); }

    // Round 10, #59: installed .NET runtime versions.
    public ObservableCollection<string> DotNetRuntimes { get; } = new();

    // Round 10, #60: active display inventory.
    public ObservableCollection<SpecRow> Monitors { get; } = new();

    // Round 10, #62: longest-uptime records, derived from the existing boot-history.json.
    private string _longestUptimeThisMonthText = "Not enough boot history yet";
    public string LongestUptimeThisMonthText { get => _longestUptimeThisMonthText; private set => SetProperty(ref _longestUptimeThisMonthText, value); }

    private string _longestUptimeThisYearText = "Not enough boot history yet";
    public string LongestUptimeThisYearText { get => _longestUptimeThisYearText; private set => SetProperty(ref _longestUptimeThisYearText, value); }

    // Round 10, #63: Windows Defender exclusion list - read-only viewer.
    public ObservableCollection<string> DefenderExclusions { get; } = new();

    private string _defenderExclusionsStatusText = "Unknown";
    public string DefenderExclusionsStatusText { get => _defenderExclusionsStatusText; private set => SetProperty(ref _defenderExclusionsStatusText, value); }

    // Round 10, #64: one-click "copy hardware IDs" for support tickets.
    private string _hardwareIdsText = string.Empty;
    public RelayCommand CopyHardwareIdsCommand { get; }

    // Round 11, #73: Windows Update/servicing reboot-pending flag, read for the System tab and
    // for SummaryViewModel's Health Check rule.
    private bool _rebootPending;
    public bool RebootPending { get => _rebootPending; private set => SetProperty(ref _rebootPending, value); }

    public AsyncRelayCommand RefreshCommand { get; }

    public SystemSpecsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyHardwareIdsCommand = new RelayCommand(_ =>
        {
            try { if (_hardwareIdsText.Length > 0) System.Windows.Clipboard.SetText(_hardwareIdsText); }
            catch { /* clipboard can be held by another app - best-effort */ }
        }, _ => _hardwareIdsText.Length > 0);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var specs = await Task.Run(() => _service.Query());
            var (uptimeMonth, uptimeYear) = await Task.Run(() => BootPerformanceService.ComputeLongestUptimeRecords());
            Apply(specs);
            LongestUptimeThisMonthText = uptimeMonth is { } m ? FormatUptime(m) : "Not enough boot history yet";
            LongestUptimeThisYearText = uptimeYear is { } y ? FormatUptime(y) : "Not enough boot history yet";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FormatUptime(TimeSpan span)
        => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{span.Hours}h {span.Minutes}m";

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
        // #92: appended age hint - "worth checking for an update" past ~3 years, not a verified
        // "an update exists" claim (Windows has no cross-vendor way to know that).
        BiosVersion = string.IsNullOrWhiteSpace(specs.BiosVersion) ? "Unknown" : specs.BiosVersion;
        if (specs.BiosAgeDays is { } biosAge)
        {
            BiosVersion += $"  ({biosAge:N0} days old — check for updates on your motherboard/OEM support page)";
            BiosAgeWarning = biosAge >= 3 * 365;
        }
        else
        {
            BiosAgeWarning = false;
        }

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

            // #36: resolve a raw JEDEC manufacturer code to a friendly name where this app's small
            // known-code table has a match - see JedecManufacturerLookup's remarks. A value that's
            // already a readable brand name (the common case on modern firmware) passes through
            // unchanged.
            string manufacturerText = JedecManufacturerLookup.Resolve(m.Manufacturer);

            MemoryModules.Add(new SpecRow
            {
                Primary = m.Location,
                Secondary = string.Join(" ", new[] { m.MemoryType, speedText, manufacturerText }.Where(s => !string.IsNullOrWhiteSpace(s))),
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
            // #65: SSD wear/life-used percentage, appended to the same secondary line as media/
            // interface type rather than a new column - see DiskInfo.WearPercent's remarks for
            // why this is best-effort and frequently unavailable.
            string? wearText = d.WearPercent is { } wear ? $"{wear}% life used" : null;

            Disks.Add(new SpecRow
            {
                Primary = string.IsNullOrWhiteSpace(d.Model) ? "Unknown disk" : d.Model,
                Secondary = string.Join(" · ", new[] { d.MediaType, d.InterfaceType, wearText }.Where(s => !string.IsNullOrWhiteSpace(s))),
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
                // #37: BitLocker - "Not applicable"/"Unknown" both render as the neutral (non-on)
                // badge state; only a confirmed "On" gets the encrypted badge.
                BitLockerStatus = v.BitLockerStatus,
                BitLockerOn = v.BitLockerStatus.StartsWith("On", StringComparison.OrdinalIgnoreCase),
                // #40: Recycle Bin size - blank (hidden) rather than "0 B" when the check itself
                // couldn't run, distinct from a genuinely empty bin.
                RecycleBinText = v.RecycleBinBytes is { } rb ? $"Recycle Bin: {Formatting.FormatBytes(rb)}" : string.Empty,
                // #42: shadow copy usage - blank when VSS isn't configured on this volume at all
                // (the common case), not shown as a false "0 B".
                ShadowCopyText = v.ShadowCopyBytes is { } sc ? $"Shadow copies: {Formatting.FormatBytes(sc)}" : string.Empty,
                // #44: TRIM - only meaningful (and only populated) for SSD volumes.
                TrimText = v.TrimEnabled switch { true => "TRIM: enabled", false => "TRIM: disabled", null => string.Empty },
                TrimWarning = v.TrimEnabled == false,
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
        RebootPending = specs.RebootPending;

        // #68: recently installed third-party software - correlates with "when did the problem
        // start". Install-only (see InstalledSoftwareInfo's remarks) - Windows keeps no log of
        // uninstalls to pair with it.
        RecentlyInstalledSoftware.Clear();
        foreach (var s in specs.RecentlyInstalledSoftware)
        {
            RecentlyInstalledSoftware.Add(new SpecRow
            {
                Primary = s.Name,
                Secondary = s.Publisher,
                SizeText = s.InstallDate.ToShortDateString(),
            });
        }

        // #69: USB devices with a Windows-reported enumeration/driver problem - per-device power
        // draw isn't shown (no reliable public API for it, see UsbDeviceInfo's remarks).
        UsbDevices.Clear();
        foreach (var u in specs.UsbDevices)
        {
            UsbDevices.Add(new SpecRow
            {
                Primary = u.Name,
                HealthText = u.HasError ? $"Error {u.ConfigManagerErrorCode}" : "OK",
                IsHealthWarning = u.HasError,
            });
        }

        // #70: page file location vs. the boot drive's media type - a page file left on a slower
        // secondary HDD (or the reverse) is a common, silent slowdown cause on multi-drive systems.
        if (specs.PageFileLocation is { } pf)
        {
            PageFileLocationText = pf.IsSameAsBootDrive
                ? $"{pf.DriveLetter} ({pf.MediaType}) - same as boot drive"
                : $"{pf.DriveLetter} ({pf.MediaType}) - different from boot drive";
            PageFileLocationWarning = pf.MediaType == "HDD";
        }
        else
        {
            PageFileLocationText = "Unknown";
            PageFileLocationWarning = false;
        }

        // #57/#58/#61: chassis form factor, Windows edition & activation, chipset driver.
        ChassisType = specs.ChassisType;
        ActivationStatus = specs.ActivationStatus;
        ChipsetDriverText = specs.ChipsetDriverText;

        // #59: installed .NET runtime versions - a plain filesystem scan, empty when no .NET
        // shared-framework install is found at all (this app's own runtime is self-contained-agnostic
        // here; this just reports whatever's actually on disk).
        DotNetRuntimes.Clear();
        foreach (var r in specs.DotNetRuntimes) DotNetRuntimes.Add(r);

        // #60: active display inventory - reuses SpecRow/SpecRowTemplate like Memory/Graphics/Storage.
        Monitors.Clear();
        foreach (var m in specs.Monitors)
        {
            Monitors.Add(new SpecRow
            {
                Primary = m.Name,
                Secondary = m.ConnectionType,
                SizeText = m.WidthPx > 0 && m.HeightPx > 0
                    ? $"{m.WidthPx}×{m.HeightPx}" + (m.RefreshHz > 0 ? $" @ {m.RefreshHz} Hz" : string.Empty)
                    : "Unknown",
            });
        }

        // #63: Defender exclusions - null (inaccessible/Tamper-Protection-blocked) is distinct from
        // an empty (successfully read, genuinely none configured) list.
        DefenderExclusions.Clear();
        if (specs.DefenderExclusions is null)
        {
            DefenderExclusionsStatusText = "Unknown - inaccessible (Tamper Protection or policy may be blocking this even elevated).";
        }
        else if (specs.DefenderExclusions.Count == 0)
        {
            DefenderExclusionsStatusText = "No exclusions configured.";
        }
        else
        {
            DefenderExclusionsStatusText = $"{specs.DefenderExclusions.Count} exclusion(s) configured:";
            foreach (var e in specs.DefenderExclusions) DefenderExclusions.Add(e);
        }

        // #64: "copy hardware IDs" - system product + CPU + GPU identifiers, for a support ticket.
        _hardwareIdsText = string.Join(Environment.NewLine, new[]
        {
            $"System: {SystemModel}" + (string.IsNullOrWhiteSpace(specs.SystemUuid) ? string.Empty : $" (UUID {specs.SystemUuid})"),
            $"CPU: {CpuName}" + (string.IsNullOrWhiteSpace(specs.CpuIdentifier) ? string.Empty : $" (ID {specs.CpuIdentifier})"),
            "GPU: " + (specs.Gpus.Count > 0 ? string.Join("; ", specs.Gpus.Select(g => g.Name)) : "Unknown"),
        });
    }
}
