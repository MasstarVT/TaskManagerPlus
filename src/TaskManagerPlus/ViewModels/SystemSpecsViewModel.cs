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

/// <summary>#440/#442/#443/#446: one row in the richer per-module memory grid - replaces the plain
/// SpecRow the Memory modules card used to bind to, since a DIMM now carries far more than a
/// title/subtitle/size (part number, serial, rank, voltages, form factor, technology, ECC and
/// mismatch flags). Every *Text field is pre-formatted here (not in the view) the same way
/// SystemSpecsViewModel.Apply already formats every other card's text, so the XAML stays plain
/// bindings.</summary>
public sealed class MemoryModuleRow
{
    public string Location { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string SpeedText { get; init; } = string.Empty;
    public string ManufacturerText { get; init; } = string.Empty;
    public string PartNumberText { get; init; } = string.Empty;
    public string SerialNumberText { get; init; } = string.Empty;
    public string FormFactorAndTechnologyText { get; init; } = string.Empty;
    public string RankAndVoltageText { get; init; } = string.Empty;
    public string ChannelText { get; init; } = string.Empty;

    /// <summary>#442: "XMP/EXPO profile may be available: ..." - empty (hidden) when the module is
    /// already running at its rated speed.</summary>
    public string XmpHintText { get; init; } = string.Empty;

    /// <summary>#443: "quick flag, not a verdict" - see MemoryModuleInfo.MismatchReason.</summary>
    public bool IsMismatched { get; init; }
    public string MismatchText { get; init; } = string.Empty;
}

/// <summary>#445: one physical DIMM slot on the board - populated (with a short module summary) or
/// empty, for the slot-map/upgrade-headroom strip. Built purely from MemoryModules + the array's
/// TotalMemorySlots, no new query.</summary>
public sealed class MemorySlotRow
{
    public int SlotNumber { get; init; }
    public bool IsPopulated { get; init; }
    public string SummaryText { get; init; } = string.Empty;
}

public sealed class SystemSpecsViewModel : ObservableObject
{
    private readonly SystemSpecsService _service = new();

    // #440/#442/#443/#446: richer per-module memory grid - see MemoryModuleRow's remarks.
    public ObservableCollection<MemoryModuleRow> MemoryModules { get; } = new();

    // #445: populated-vs-empty slot map, built from MemoryModules + TotalMemorySlots - no new query.
    public ObservableCollection<MemorySlotRow> MemorySlots { get; } = new();

    private string _memoryArrayMaxCapacityText = string.Empty;
    public string MemoryArrayMaxCapacityText { get => _memoryArrayMaxCapacityText; private set => SetProperty(ref _memoryArrayMaxCapacityText, value); }

    // #446: ECC status - see MemoryDiagnosticsService.DescribeEcc.
    private string _memoryEccStatusText = "Unknown";
    public string MemoryEccStatusText { get => _memoryEccStatusText; private set => SetProperty(ref _memoryEccStatusText, value); }

    // #444: channel population - see MemoryDiagnosticsService.CheckChannelPopulation.
    private string _memoryChannelText = string.Empty;
    public string MemoryChannelText { get => _memoryChannelText; private set => SetProperty(ref _memoryChannelText, value); }

    private bool _memoryChannelWarning;
    public bool MemoryChannelWarning { get => _memoryChannelWarning; private set => SetProperty(ref _memoryChannelWarning, value); }

    // #447: corrected-memory-error events (WHEA-Logger 47) - same figure the Stability tab shows,
    // read independently (see EventLogService.ReadCorrectedMemoryErrors's remarks).
    private int _correctedMemoryErrorCount;
    public int CorrectedMemoryErrorCount { get => _correctedMemoryErrorCount; private set => SetProperty(ref _correctedMemoryErrorCount, value); }

    private string _lastCorrectedMemoryErrorText = "None in the last 30 days";
    public string LastCorrectedMemoryErrorText { get => _lastCorrectedMemoryErrorText; private set => SetProperty(ref _lastCorrectedMemoryErrorText, value); }

    // #448/#449: Windows Memory Diagnostic launcher + last-run result.
    public RelayCommand RunMemoryDiagnosticCommand { get; }

    private string? _memoryDiagnosticLaunchStatusText;
    public string? MemoryDiagnosticLaunchStatusText { get => _memoryDiagnosticLaunchStatusText; private set => SetProperty(ref _memoryDiagnosticLaunchStatusText, value); }

    private string _memoryDiagnosticResultText = "Never run";
    public string MemoryDiagnosticResultText { get => _memoryDiagnosticResultText; private set => SetProperty(ref _memoryDiagnosticResultText, value); }

    private bool _memoryDiagnosticFailed;
    public bool MemoryDiagnosticFailed { get => _memoryDiagnosticFailed; private set => SetProperty(ref _memoryDiagnosticFailed, value); }

    // #451: single RAM health rollup card.
    private string _ramHealthVerdictText = "Unknown";
    public string RamHealthVerdictText { get => _ramHealthVerdictText; private set => SetProperty(ref _ramHealthVerdictText, value); }

    private bool _ramHealthIsWarning;
    public bool RamHealthIsWarning { get => _ramHealthIsWarning; private set => SetProperty(ref _ramHealthIsWarning, value); }

    public ObservableCollection<string> RamHealthFindings { get; } = new();
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

    /// <summary>Set when RefreshAsync's WMI/registry inventory query fails outright - empty/null
    /// the rest of the time. Mirrors the "...failed: {message}" convention this app's other
    /// on-demand actions already use rather than letting the exception propagate uncaught out of
    /// an async void command handler.</summary>
    private string? _refreshErrorText;
    public string? RefreshErrorText { get => _refreshErrorText; private set => SetProperty(ref _refreshErrorText, value); }

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

    // #441: CAS latency/primary timings - see SystemSpecsService.ReadMemoryTimingsText for why
    // this is "Unknown" on almost every system today (LibreHardwareMonitorLib doesn't currently
    // expose SPD timing sensors on any chipset backend), and why the lookup is still real rather
    // than a hardcoded string.
    private string _memoryTimingsText = "Unknown";
    public string MemoryTimingsText { get => _memoryTimingsText; private set => SetProperty(ref _memoryTimingsText, value); }

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

        // #448: launches mdsched.exe, which shows its own "restart now / on next restart" dialog -
        // this app never waits on it, so a plain synchronous RelayCommand is enough (no async work
        // of this app's own to await).
        RunMemoryDiagnosticCommand = new RelayCommand(_ =>
        {
            var (success, error) = MemoryDiagnosticLauncherService.Launch();
            MemoryDiagnosticLaunchStatusText = success
                ? "Windows Memory Diagnostic launched - choose \"Restart now\" or \"Check for problems the next time I start my computer\" in its window. The test itself takes 10-40 minutes and runs entirely offline (before Windows loads)."
                : $"Couldn't launch Windows Memory Diagnostic: {error}";
        });

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            // #440/#441/#447/#449/#451 added several genuinely slow synchronous reads to
            // QueryAsync (raw SMBIOS parse, a one-shot LibreHardwareMonitorLib sample, and three
            // targeted event-log queries) on top of its pre-existing synchronous WMI sweep -
            // wrapped in Task.Run here (the same "keep WMI/perf-counter/event-log work off the UI
            // thread" pattern CLAUDE.md documents and StabilityViewModel.RefreshAsync already
            // follows for its own EventLogService.Query() call) so a Refresh click can no longer
            // visibly freeze the UI thread.
            var specs = await Task.Run(() => _service.QueryAsync());
            var (uptimeMonth, uptimeYear) = await Task.Run(() => BootPerformanceService.ComputeLongestUptimeRecords());
            Apply(specs);
            LongestUptimeThisMonthText = uptimeMonth is { } m ? FormatUptime(m) : "Not enough boot history yet";
            LongestUptimeThisYearText = uptimeYear is { } y ? FormatUptime(y) : "Not enough boot history yet";
            RefreshErrorText = null;
        }
        catch (Exception ex)
        {
            RefreshErrorText = $"Couldn't refresh system specs: {ex.Message}";
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
        MemoryTimingsText = specs.MemoryTimingsText;
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

            // #440: SMBIOS extras - each piece stays blank/hidden when no SMBIOS match was found
            // for this module (raw table unreadable, or a locator that didn't line up).
            string formFactorTech = string.Join(" · ", new[] { m.FormFactor, m.MemoryTechnology }
                .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Unknown"));

            var rankVoltageParts = new List<string>();
            if (m.RankCount is { } rank) rankVoltageParts.Add(rank == 1 ? "Single rank" : $"{rank}-rank");
            if (m.ConfiguredVoltageV is { } cv) rankVoltageParts.Add($"{cv:0.00} V");
            else if (m.MaxVoltageV is { } mv) rankVoltageParts.Add($"rated max {mv:0.00} V");

            MemoryModules.Add(new MemoryModuleRow
            {
                Location = m.Location,
                SizeText = Formatting.FormatBytes(m.CapacityBytes),
                SpeedText = string.Join(" ", new[] { m.MemoryType, speedText }.Where(s => !string.IsNullOrWhiteSpace(s))),
                ManufacturerText = manufacturerText,
                PartNumberText = string.IsNullOrWhiteSpace(m.PartNumber) ? string.Empty : $"P/N {m.PartNumber}",
                SerialNumberText = string.IsNullOrWhiteSpace(m.SerialNumber) ? string.Empty : $"S/N {m.SerialNumber}",
                FormFactorAndTechnologyText = formFactorTech,
                RankAndVoltageText = string.Join(" · ", rankVoltageParts),
                ChannelText = string.IsNullOrEmpty(m.ChannelLabel) ? string.Empty : $"Channel {m.ChannelLabel}",
                XmpHintText = MemoryDiagnosticsService.DescribeXmpHint(m) ?? string.Empty,
                IsMismatched = m.IsMismatched,
                MismatchText = m.MismatchReason,
            });
        }

        // #445: slot map - populated slots (one per module) followed by empty slots up to
        // TotalMemorySlots, when that count is known; falls back to just the populated modules
        // (no trailing empty slots) when the array's total slot count itself isn't known.
        MemorySlots.Clear();
        int slotNumber = 1;
        foreach (var m in specs.MemoryModules)
        {
            string summary = string.Join(" · ", new[] { Formatting.FormatBytes(m.CapacityBytes), m.MemoryType }.Where(s => !string.IsNullOrWhiteSpace(s) && s != "Unknown"));
            MemorySlots.Add(new MemorySlotRow { SlotNumber = slotNumber++, IsPopulated = true, SummaryText = summary });
        }
        if (specs.TotalMemorySlots is { } totalSlots)
        {
            while (slotNumber <= totalSlots)
                MemorySlots.Add(new MemorySlotRow { SlotNumber = slotNumber++, IsPopulated = false, SummaryText = "Empty" });
        }

        MemoryArrayMaxCapacityText = specs.MemoryArrayMaxCapacityBytes is { } maxCap
            ? $"Array maximum: {Formatting.FormatBytes(maxCap)}"
            : string.Empty;

        // #446/#444
        MemoryEccStatusText = specs.MemoryEccStatusText;
        MemoryChannelText = specs.MemoryChannelText;
        MemoryChannelWarning = specs.MemoryChannelWarning;

        // #447
        CorrectedMemoryErrorCount = specs.CorrectedMemoryErrorCount;
        LastCorrectedMemoryErrorText = specs.LastCorrectedMemoryError is { } lastCorrected
            ? $"Last: {lastCorrected:g}" : "None in the last 30 days";

        // #449
        if (specs.MemoryDiagnosticResult is { } diag)
        {
            MemoryDiagnosticFailed = diag.Passed == false;
            MemoryDiagnosticResultText = diag.Passed switch
            {
                true => $"Passed - no errors found ({diag.TimeCreated:g})",
                false => $"Errors found ({diag.TimeCreated:g})",
                null => $"Ran {diag.TimeCreated:g} - result text not recognized",
            };
        }
        else
        {
            MemoryDiagnosticFailed = false;
            MemoryDiagnosticResultText = "Never run";
        }

        // #451
        RamHealthVerdictText = specs.RamHealth.Verdict;
        RamHealthIsWarning = specs.RamHealth.IsWarning;
        RamHealthFindings.Clear();
        foreach (var f in specs.RamHealth.Findings) RamHealthFindings.Add(f);

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
