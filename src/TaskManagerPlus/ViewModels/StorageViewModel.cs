using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One fixed volume's on-demand HDD fragmentation check (#86) - a row per fixed drive
/// that reports as an HDD (SSDs are hidden entirely, since fragmentation isn't meaningful there).</summary>
public sealed class FragmentationRow : ObservableObject
{
    public string DriveLetter { get; init; } = string.Empty;

    private string _statusText = "Not checked";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }

    private bool _isWarning;
    public bool IsWarning { get => _isWarning; set => SetProperty(ref _isWarning, value); }
}

/// <summary>One disk selectable in the round 9 (#38) on-demand SMART-details picker.</summary>
public sealed class SmartDiskOption
{
    public int Index { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Display => $"Disk {Index} — {Model}";
}

/// <summary>One label/value row in the round 9 (#38) SMART attribute table.</summary>
public sealed record SmartAttributeRow(string Label, string Value);

/// <summary>One tile in the round 10 (#306) critical-attribute triage strip - "quick flag, not a
/// verdict": a non-zero reallocated/pending/uncorrectable/CRC count is worth watching, not on its
/// own a reason to replace a drive.</summary>
public sealed class SmartTriageTile
{
    public string Title { get; init; } = string.Empty;
    public string ValueText { get; init; } = "—";
    public bool IsCritical { get; init; }
}

/// <summary>One row in the round 9 (#39) largest-files/folders scan result.</summary>
public sealed class LargestItemRow
{
    public string Path { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}

/// <summary>Round 13, #314: one decoded bit of the NVMe Health Log's Critical Warning byte - a
/// named row with a red badge when set, rather than a raw hex mask. All six are always shown (not
/// just the set ones) once a health log has been read, so the card reads as a checklist.</summary>
public sealed class NvmeWarningRow
{
    public string Label { get; init; } = string.Empty;
    public bool IsSet { get; init; }
}

/// <summary>
/// Backs the Storage tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer. Also takes the shared
/// EnergyThermalsViewModel purely to re-present its already-polled Temperatures list filtered
/// down to Storage hardware (#17) - no new sensor sampling here.
/// </summary>
public sealed class StorageViewModel : ObservableObject
{
    public PerformanceViewModel Performance { get; }

    /// <summary>Exposed purely so StorageView.xaml can bind to the round 9 (#43) NVMe
    /// controller-vs-flash-die temperature split directly - StorageViewModel doesn't poll it
    /// itself (EnergyThermalsViewModel already does, on its own 1.5s timer).</summary>
    public EnergyThermalsViewModel EnergyThermals { get; }

    /// <summary>Per-drive temperature readings (#17), filtered from EnergyThermalsViewModel's
    /// already-polled Temperatures collection. A fresh ListCollectionView (not
    /// CollectionViewSource.GetDefaultView, which would return the same shared default view the
    /// Energy &amp; Thermals tab's own binding uses, and setting a Filter on it would wrongly
    /// filter that tab's list too) so the two tabs' views of the same underlying collection stay
    /// independent.</summary>
    public ICollectionView DriveTemperatures { get; }

    // #85: Storage Spaces / RAID member health rollup - queried once (like CpuTopologyService's
    // static topology query), not on a timer, since pool membership can't change without an
    // explicit admin action. Empty (and the card hidden) on the large majority of systems that
    // don't use Storage Spaces at all.
    public ObservableCollection<StorageSpaceInfo> StoragePools { get; } = new();

    // #86: fixed HDD volumes eligible for an on-demand fragmentation check - SSDs never appear
    // here at all (fragmentation isn't a meaningful concept for them).
    public ObservableCollection<FragmentationRow> HddVolumes { get; } = new();
    public AsyncRelayCommand CheckFragmentationCommand { get; }

    // Round 9, #38: on-demand full SMART attribute table, per disk.
    public ObservableCollection<SmartDiskOption> SmartDiskOptions { get; } = new();

    private SmartDiskOption? _selectedSmartDisk;
    public SmartDiskOption? SelectedSmartDisk { get => _selectedSmartDisk; set => SetProperty(ref _selectedSmartDisk, value); }

    public ObservableCollection<SmartAttributeRow> SmartDetails { get; } = new();

    private string _smartStatusText = string.Empty;
    public string SmartStatusText { get => _smartStatusText; private set => SetProperty(ref _smartStatusText, value); }

    public AsyncRelayCommand ReadSmartDetailsCommand { get; }

    // Round 10, #301-#312: on-demand raw ATA SMART attribute table (the real 30-entry table from
    // MSStorageDriver_ATAPISmartData, not just the driver-summarised MSFT_StorageReliabilityCounter
    // fields above) plus derived triage/temperature/power-on/wear-cycle/endurance summaries - all
    // populated from the same ReadSmartDetailsCommand action above rather than a separate polling
    // loop, per SmartRawAttributeService.Read.
    public ObservableCollection<SmartRawAttribute> SmartRawAttributes { get; } = new();

    private string _smartRawStatusText = "Not read";
    public string SmartRawStatusText { get => _smartRawStatusText; private set => SetProperty(ref _smartRawStatusText, value); }

    private string _smartVendorProfileText = string.Empty;
    public string SmartVendorProfileText { get => _smartVendorProfileText; private set => SetProperty(ref _smartVendorProfileText, value); }

    private string _smartAvailabilityNote = string.Empty;
    public string SmartAvailabilityNote { get => _smartAvailabilityNote; private set => SetProperty(ref _smartAvailabilityNote, value); }

    // #306: five-tile triage strip - pinned above the raw attribute grid.
    public ObservableCollection<SmartTriageTile> SmartTriageTiles { get; } = new();

    // #307
    private string _smartTemperatureSummary = string.Empty;
    public string SmartTemperatureSummary { get => _smartTemperatureSummary; private set => SetProperty(ref _smartTemperatureSummary, value); }

    // #308
    private string _smartPowerOnSummary = string.Empty;
    public string SmartPowerOnSummary { get => _smartPowerOnSummary; private set => SetProperty(ref _smartPowerOnSummary, value); }

    // #309: HDD-only load/unload + start/stop wear card - hidden entirely for SSD/NVMe media.
    private bool _showSmartWearCycles;
    public bool ShowSmartWearCycles { get => _showSmartWearCycles; private set => SetProperty(ref _showSmartWearCycles, value); }

    private string _smartLoadUnloadText = string.Empty;
    public string SmartLoadUnloadText { get => _smartLoadUnloadText; private set => SetProperty(ref _smartLoadUnloadText, value); }

    private string _smartStartStopText = string.Empty;
    public string SmartStartStopText { get => _smartStartStopText; private set => SetProperty(ref _smartStartStopText, value); }

    // #310/#311: Endurance card - TBW/WAF estimate + SSD wear-levelling detail cross-checked
    // against the driver's own Wear% summary.
    private bool _showSmartEndurance;
    public bool ShowSmartEndurance { get => _showSmartEndurance; private set => SetProperty(ref _showSmartEndurance, value); }

    private string _smartEnduranceTbwText = string.Empty;
    public string SmartEnduranceTbwText { get => _smartEnduranceTbwText; private set => SetProperty(ref _smartEnduranceTbwText, value); }

    private string _smartEnduranceWafText = string.Empty;
    public string SmartEnduranceWafText { get => _smartEnduranceWafText; private set => SetProperty(ref _smartEnduranceWafText, value); }

    private string _smartWearLevelText = string.Empty;
    public string SmartWearLevelText { get => _smartWearLevelText; private set => SetProperty(ref _smartWearLevelText, value); }

    private string _smartWearWarningText = string.Empty;
    public string SmartWearWarningText { get => _smartWearWarningText; private set => SetProperty(ref _smartWearWarningText, value); }

    // Round 13, #313-#319: NVMe SMART/Health Information Log (page 0x02) - populated alongside the
    // raw SMART read above (same ReadSmartDetailsCommand action) whenever the selected disk's
    // BusType is NVMe; hidden entirely otherwise.
    private bool _showNvmeHealth;
    public bool ShowNvmeHealth { get => _showNvmeHealth; private set => SetProperty(ref _showNvmeHealth, value); }

    private string _nvmeHealthStatusText = "Not read";
    public string NvmeHealthStatusText { get => _nvmeHealthStatusText; private set => SetProperty(ref _nvmeHealthStatusText, value); }

    // #314
    public ObservableCollection<NvmeWarningRow> NvmeCriticalWarnings { get; } = new();

    // #315
    private string _nvmeSpareText = string.Empty;
    public string NvmeSpareText { get => _nvmeSpareText; private set => SetProperty(ref _nvmeSpareText, value); }

    private string _nvmeSpareThresholdText = string.Empty;
    public string NvmeSpareThresholdText { get => _nvmeSpareThresholdText; private set => SetProperty(ref _nvmeSpareThresholdText, value); }

    private bool _nvmeSpareBelowThreshold;
    public bool NvmeSpareBelowThreshold { get => _nvmeSpareBelowThreshold; private set => SetProperty(ref _nvmeSpareBelowThreshold, value); }

    private string _nvmePercentUsedText = string.Empty;
    public string NvmePercentUsedText { get => _nvmePercentUsedText; private set => SetProperty(ref _nvmePercentUsedText, value); }

    // #316
    private string _nvmeDataUnitsText = string.Empty;
    public string NvmeDataUnitsText { get => _nvmeDataUnitsText; private set => SetProperty(ref _nvmeDataUnitsText, value); }

    private string _nvmeAverageIoSizeText = string.Empty;
    public string NvmeAverageIoSizeText { get => _nvmeAverageIoSizeText; private set => SetProperty(ref _nvmeAverageIoSizeText, value); }

    // #317
    private string _nvmeMediaErrorText = string.Empty;
    public string NvmeMediaErrorText { get => _nvmeMediaErrorText; private set => SetProperty(ref _nvmeMediaErrorText, value); }

    private bool _nvmeMediaErrorsPresent;
    public bool NvmeMediaErrorsPresent { get => _nvmeMediaErrorsPresent; private set => SetProperty(ref _nvmeMediaErrorsPresent, value); }

    // #318
    private string _nvmeTemperatureText = string.Empty;
    public string NvmeTemperatureText { get => _nvmeTemperatureText; private set => SetProperty(ref _nvmeTemperatureText, value); }

    private string _nvmeThrottleText = string.Empty;
    public string NvmeThrottleText { get => _nvmeThrottleText; private set => SetProperty(ref _nvmeThrottleText, value); }

    // #319
    private string _nvmePowerText = string.Empty;
    public string NvmePowerText { get => _nvmePowerText; private set => SetProperty(ref _nvmePowerText, value); }

    // #320: Error Information Log (page 0x01) - separate round trip, behind its own button.
    public ObservableCollection<NvmeErrorLogEntry> NvmeErrorLog { get; } = new();

    private string _nvmeErrorLogStatusText = "Not read";
    public string NvmeErrorLogStatusText { get => _nvmeErrorLogStatusText; private set => SetProperty(ref _nvmeErrorLogStatusText, value); }

    private bool _isReadingNvmeErrorLog;
    public bool IsReadingNvmeErrorLog { get => _isReadingNvmeErrorLog; private set => SetProperty(ref _isReadingNvmeErrorLog, value); }

    public AsyncRelayCommand ReadNvmeErrorLogCommand { get; }

    // #321: Device Self-test Log (page 0x06) + Short/Extended self-test triggers.
    public ObservableCollection<NvmeSelfTestResult> NvmeSelfTestResults { get; } = new();

    private string _nvmeSelfTestStatusText = "Not read";
    public string NvmeSelfTestStatusText { get => _nvmeSelfTestStatusText; private set => SetProperty(ref _nvmeSelfTestStatusText, value); }

    private bool _isReadingNvmeSelfTestLog;
    public bool IsReadingNvmeSelfTestLog { get => _isReadingNvmeSelfTestLog; private set => SetProperty(ref _isReadingNvmeSelfTestLog, value); }

    public AsyncRelayCommand ReadNvmeSelfTestLogCommand { get; }

    private string _nvmeSelfTestTriggerStatusText = string.Empty;
    public string NvmeSelfTestTriggerStatusText { get => _nvmeSelfTestTriggerStatusText; private set => SetProperty(ref _nvmeSelfTestTriggerStatusText, value); }

    public RelayCommand RunNvmeShortSelfTestCommand { get; }
    public RelayCommand RunNvmeExtendedSelfTestCommand { get; }

    // #322: Identify Controller facts + best-effort APST (autonomous power state) detail.
    private string _nvmeIdentifyText = string.Empty;
    public string NvmeIdentifyText { get => _nvmeIdentifyText; private set => SetProperty(ref _nvmeIdentifyText, value); }

    private string _nvmeApstText = string.Empty;
    public string NvmeApstText { get => _nvmeApstText; private set => SetProperty(ref _nvmeApstText, value); }

    // Round 13, #323: MSFT_StorageReliabilityCounter latency-maximum tiles - pure WMI, applies to
    // any disk (not NVMe-specific), populated alongside the raw SMART read for the selected disk.
    private bool _showReliabilityLatencyTiles;
    public bool ShowReliabilityLatencyTiles { get => _showReliabilityLatencyTiles; private set => SetProperty(ref _showReliabilityLatencyTiles, value); }

    private string _reliabilityReadLatencyText = "—";
    public string ReliabilityReadLatencyText { get => _reliabilityReadLatencyText; private set => SetProperty(ref _reliabilityReadLatencyText, value); }

    private string _reliabilityWriteLatencyText = "—";
    public string ReliabilityWriteLatencyText { get => _reliabilityWriteLatencyText; private set => SetProperty(ref _reliabilityWriteLatencyText, value); }

    private string _reliabilityFlushLatencyText = "—";
    public string ReliabilityFlushLatencyText { get => _reliabilityFlushLatencyText; private set => SetProperty(ref _reliabilityFlushLatencyText, value); }

    private string _reliabilityResetStatusText = string.Empty;
    public string ReliabilityResetStatusText { get => _reliabilityResetStatusText; private set => SetProperty(ref _reliabilityResetStatusText, value); }

    public AsyncRelayCommand ResetReliabilityCountersCommand { get; }

    // Round 9, #39: largest files/folders scanner - on-demand, depth-capped.
    private string _largestItemsRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
    public string LargestItemsRoot { get => _largestItemsRoot; set => SetProperty(ref _largestItemsRoot, value); }

    public ObservableCollection<LargestItemRow> LargestItems { get; } = new();

    private bool _isScanningLargestItems;
    public bool IsScanningLargestItems { get => _isScanningLargestItems; private set => SetProperty(ref _isScanningLargestItems, value); }

    private string _largestItemsStatusText = "Not scanned";
    public string LargestItemsStatusText { get => _largestItemsStatusText; private set => SetProperty(ref _largestItemsStatusText, value); }

    public AsyncRelayCommand ScanLargestItemsCommand { get; }

    // Round 9, #41: on-demand simple sequential throughput test.
    public ObservableCollection<string> ThroughputDriveOptions { get; } = new();

    private string? _selectedThroughputDrive;
    public string? SelectedThroughputDrive { get => _selectedThroughputDrive; set => SetProperty(ref _selectedThroughputDrive, value); }

    private bool _isThroughputTesting;
    public bool IsThroughputTesting { get => _isThroughputTesting; private set => SetProperty(ref _isThroughputTesting, value); }

    private string _throughputResultText = "Not tested";
    public string ThroughputResultText { get => _throughputResultText; private set => SetProperty(ref _throughputResultText, value); }

    public AsyncRelayCommand RunThroughputTestCommand { get; }

    public StorageViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        Performance = performance;
        EnergyThermals = energyThermals;

        var view = new ListCollectionView((IList)energyThermals.Temperatures)
        {
            Filter = o => o is SensorReading r && r.HardwareType == HardwareType.Storage,
        };
        DriveTemperatures = view;

        CheckFragmentationCommand = new AsyncRelayCommand(param => CheckFragmentationAsync(param as FragmentationRow));
        ReadSmartDetailsCommand = new AsyncRelayCommand(ReadSmartDetailsAsync, () => SelectedSmartDisk is not null);
        ScanLargestItemsCommand = new AsyncRelayCommand(ScanLargestItemsAsync, () => !IsScanningLargestItems && Directory.Exists(LargestItemsRoot));
        RunThroughputTestCommand = new AsyncRelayCommand(RunThroughputTestAsync, () => !IsThroughputTesting && SelectedThroughputDrive is not null);

        // #320/#321: separate log-page round trips, each gated on an NVMe disk actually having
        // been read via ReadSmartDetailsCommand above (ShowNvmeHealth).
        ReadNvmeErrorLogCommand = new AsyncRelayCommand(ReadNvmeErrorLogAsync, () => ShowNvmeHealth && !IsReadingNvmeErrorLog && SelectedSmartDisk is not null);
        ReadNvmeSelfTestLogCommand = new AsyncRelayCommand(ReadNvmeSelfTestLogAsync, () => ShowNvmeHealth && !IsReadingNvmeSelfTestLog && SelectedSmartDisk is not null);
        RunNvmeShortSelfTestCommand = new RelayCommand(() => RunNvmeSelfTest(extended: false), () => ShowNvmeHealth && SelectedSmartDisk is not null);
        RunNvmeExtendedSelfTestCommand = new RelayCommand(() => RunNvmeSelfTest(extended: true), () => ShowNvmeHealth && SelectedSmartDisk is not null);

        // #323: pure WMI, any disk - independent of the NVMe gating above.
        ResetReliabilityCountersCommand = new AsyncRelayCommand(ResetReliabilityCountersAsync, () => SelectedSmartDisk is not null);

        _ = Task.Run(() =>
        {
            var pools = StorageSpacesService.List();
            var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            var hddDrives = fixedDrives
                .Select(d => d.Name.TrimEnd('\\'))
                .Where(letter => DiskFragmentationService.GetMediaType(letter) == "HDD")
                .ToList();
            var disks = SystemSpecsService.ListDisksForSmart();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var p in pools) StoragePools.Add(p);
                foreach (var letter in hddDrives) HddVolumes.Add(new FragmentationRow { DriveLetter = letter });
                foreach (var (index, model) in disks) SmartDiskOptions.Add(new SmartDiskOption { Index = index, Model = model });
                foreach (var d in fixedDrives) ThroughputDriveOptions.Add(d.Name.TrimEnd('\\'));
                SelectedThroughputDrive = ThroughputDriveOptions.FirstOrDefault();
            });
        });
    }

    private async Task CheckFragmentationAsync(FragmentationRow? row)
    {
        if (row is null || row.IsChecking) return;
        row.IsChecking = true;
        row.StatusText = "Analyzing (this can take a while on a large drive)...";
        try
        {
            var (success, percent, message) = await DiskFragmentationService.Analyze(row.DriveLetter);
            row.StatusText = message;
            row.IsWarning = success && percent is { } p && p >= 10;
        }
        catch (Exception ex)
        {
            row.StatusText = $"Failed: {ex.Message}";
            row.IsWarning = false;
        }
        finally
        {
            row.IsChecking = false;
        }
    }

    private async Task ReadSmartDetailsAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        SmartStatusText = "Reading...";
        SmartDetails.Clear();
        ResetSmartRawDisplay();
        try
        {
            var rows = await Task.Run(() => SystemSpecsService.ReadSmartDetails(disk.Index));
            foreach (var (label, value) in rows) SmartDetails.Add(new SmartAttributeRow(label, value));
            SmartStatusText = rows.Count == 0
                ? "No SMART reliability counters reported by this drive/driver."
                : $"{rows.Count} attributes read.";
        }
        catch (Exception ex)
        {
            SmartStatusText = $"Failed: {ex.Message}";
        }

        // Round 10, #301-#312: the raw attribute table + derived summaries, tied into this same
        // on-demand action rather than a separate polling loop.
        SmartRawStatusText = "Reading raw SMART attributes...";
        try
        {
            var result = await Task.Run(() => SmartRawAttributeService.Read(disk.Index, disk.Model));
            ApplySmartRawResult(result);

            // Round 13, #313/#322: bundle the NVMe health-log-page-0x02 read (and Identify
            // Controller) into this same on-demand action - both are cheap single round trips, and
            // CLAUDE.md's "bundle a cheap read into the existing on-demand flow rather than a new
            // timer" guidance applies directly. #320/#321 (error log, self-test log/trigger) stay
            // separate buttons since each is its own heavier log-page round trip.
            ShowNvmeHealth = result.BusType == "NVMe";
            if (ShowNvmeHealth)
            {
                NvmeHealthStatusText = "Reading NVMe SMART/Health Information Log (page 0x02)...";
                var healthLog = await Task.Run(() => NvmeHealthLogService.ReadHealthLog(disk.Index));
                ApplyNvmeHealth(healthLog);

                var identify = await Task.Run(() => NvmeHealthLogService.ReadIdentify(disk.Index));
                ApplyNvmeIdentify(identify);
            }
        }
        catch (Exception ex)
        {
            SmartRawStatusText = $"Failed: {ex.Message}";
        }

        // Round 13, #323: reliability-counter latency maxima - pure WMI, independent of media/bus
        // type, so read regardless of whether ShowNvmeHealth ended up true above.
        try
        {
            var (readLatency, writeLatency, flushLatency) = await Task.Run(() => SystemSpecsService.ReadReliabilityLatencies(disk.Index));
            ApplyReliabilityLatencies(readLatency, writeLatency, flushLatency);
        }
        catch
        {
            ApplyReliabilityLatencies(null, null, null);
        }
    }

    private void ResetSmartRawDisplay()
    {
        SmartRawAttributes.Clear();
        SmartTriageTiles.Clear();
        SmartVendorProfileText = string.Empty;
        SmartAvailabilityNote = string.Empty;
        SmartTemperatureSummary = string.Empty;
        SmartPowerOnSummary = string.Empty;
        ShowSmartWearCycles = false;
        SmartLoadUnloadText = string.Empty;
        SmartStartStopText = string.Empty;
        ShowSmartEndurance = false;
        SmartEnduranceTbwText = string.Empty;
        SmartEnduranceWafText = string.Empty;
        SmartWearLevelText = string.Empty;
        SmartWearWarningText = string.Empty;

        // Round 13: reset NVMe + reliability-latency display so a previous disk's data doesn't
        // linger while the new disk's data is still being read (or if it turns out non-NVMe).
        ShowNvmeHealth = false;
        NvmeHealthStatusText = "Not read";
        NvmeCriticalWarnings.Clear();
        NvmeSpareText = string.Empty;
        NvmeSpareThresholdText = string.Empty;
        NvmeSpareBelowThreshold = false;
        NvmePercentUsedText = string.Empty;
        NvmeDataUnitsText = string.Empty;
        NvmeAverageIoSizeText = string.Empty;
        NvmeMediaErrorText = string.Empty;
        NvmeMediaErrorsPresent = false;
        NvmeTemperatureText = string.Empty;
        NvmeThrottleText = string.Empty;
        NvmePowerText = string.Empty;
        NvmeIdentifyText = string.Empty;
        NvmeApstText = string.Empty;
        NvmeErrorLog.Clear();
        NvmeErrorLogStatusText = "Not read";
        NvmeSelfTestResults.Clear();
        NvmeSelfTestStatusText = "Not read";
        NvmeSelfTestTriggerStatusText = string.Empty;
        ApplyReliabilityLatencies(null, null, null);
    }

    private void ApplySmartRawResult(SmartRawResult result)
    {
        SmartVendorProfileText = result.VendorProfileName is null
            ? "No vendor-specific attribute map matched this drive's model - generic ATA-8 attribute names shown."
            : $"Vendor profile: {result.VendorProfileName} (attribute names/raw decoding for reassigned IDs use this vendor's convention).";

        if (result.Unavailable)
        {
            SmartRawStatusText = "No raw SMART data available.";
            SmartAvailabilityNote = result.UnavailableReason;
            return;
        }

        SmartAvailabilityNote = $"Source: {result.SourceDescription}.";
        foreach (var attr in result.Attributes) SmartRawAttributes.Add(attr);
        SmartRawStatusText = result.Attributes.Count == 0
            ? "The SMART data blob was read but contained no populated attribute entries."
            : $"{result.Attributes.Count} raw attributes decoded, sorted by margin to failure.";

        BuildTriageTiles(result.Attributes);
        SmartTemperatureSummary = BuildTemperatureSummary(result.Attributes);
        SmartPowerOnSummary = BuildPowerOnSummary(result.Attributes);
        BuildWearCycleSummary(result.Attributes, result.MediaType);
        BuildEnduranceSummary(result.Attributes, result.DriverWearPercent, result.BytesPerSector);
    }

    private static SmartRawAttribute? Find(IReadOnlyList<SmartRawAttribute> attrs, byte id) => attrs.FirstOrDefault(a => a.Id == id);

    /// <summary>#306: five fixed tiles for the attributes that actually predict failure.
    /// Informational only - see the "quick flag, not a verdict" caption in StorageView.xaml.</summary>
    private void BuildTriageTiles(IReadOnlyList<SmartRawAttribute> attrs)
    {
        (byte Id, string Title)[] triage =
        {
            (0x05, "Reallocated"),
            (0xC5, "Current Pending"),
            (0xC6, "Offline Uncorr."),
            (0xBB, "Reported Uncorr."),
            (0xC7, "UDMA CRC"),
        };
        foreach (var (id, title) in triage)
        {
            var attr = Find(attrs, id);
            SmartTriageTiles.Add(new SmartTriageTile
            {
                Title = title,
                ValueText = attr is null ? "—" : attr.RawValue.ToString("N0"),
                IsCritical = attr is not null && attr.RawValue != 0,
            });
        }
    }

    /// <summary>#307: attributes 0xC2/0xBE pack current/lifetime-min/lifetime-max temperature as
    /// three 16-bit little-endian words on most drives - a best-effort decode (word layout varies
    /// by vendor/firmware, so this is captioned as such), not the live LibreHardwareMonitor sensor
    /// the drive-temperature card above already charts.</summary>
    private static string BuildTemperatureSummary(IReadOnlyList<SmartRawAttribute> attrs)
    {
        var attr = Find(attrs, 0xC2) ?? Find(attrs, 0xBE);
        if (attr is null) return string.Empty;

        int current = attr.RawBytes[0];
        int min = attr.RawBytes[2] | (attr.RawBytes[3] << 8);
        int max = attr.RawBytes[4] | (attr.RawBytes[5] << 8);

        if (min == 0 && max == 0)
            return $"Now {current}°C (attribute {attr.IdHex}) - lifetime min/max not reported by this drive's firmware.";
        return $"Now {current}°C / Lifetime min {min}°C / Lifetime max {max}°C (attribute {attr.IdHex}, best-effort word decode - layout varies by vendor).";
    }

    /// <summary>#308: converts Power-On Hours (0x09) into a rough calendar duration and pairs it
    /// with Power Cycle Count (0x0C) to derive an average hours-per-cycle figure.</summary>
    private static string BuildPowerOnSummary(IReadOnlyList<SmartRawAttribute> attrs)
    {
        var hoursAttr = Find(attrs, 0x09);
        if (hoursAttr is null) return string.Empty;

        uint hours = (uint)(hoursAttr.RawBytes[0] | (hoursAttr.RawBytes[1] << 8) | (hoursAttr.RawBytes[2] << 16) | (hoursAttr.RawBytes[3] << 24));
        int years = (int)(hours / 8760);
        int months = (int)((hours % 8760) / 730);
        string duration = years > 0 ? $"{years} y {months} mo" : months > 0 ? $"{months} mo" : $"{hours} h";
        string text = $"{duration} ({hours:N0} h) power-on";

        var cyclesAttr = Find(attrs, 0x0C);
        if (cyclesAttr is not null)
        {
            uint cycles = (uint)(cyclesAttr.RawBytes[0] | (cyclesAttr.RawBytes[1] << 8) | (cyclesAttr.RawBytes[2] << 16) | (cyclesAttr.RawBytes[3] << 24));
            text += cycles > 0
                ? $" · {cycles:N0} power cycles · avg {(double)hours / cycles:0.#} h/cycle"
                : " · 0 power cycles recorded";
        }
        return text;
    }

    /// <summary>#309: Load/Unload Cycle Count (0xC1) and Start/Stop Count (0x04) against this
    /// vendor's typical (not per-model) rated maximum - HDD-only, hidden entirely for SSD/NVMe
    /// where head-parking cycles aren't a meaningful wear metric.</summary>
    private void BuildWearCycleSummary(IReadOnlyList<SmartRawAttribute> attrs, string mediaType)
    {
        if (mediaType != "HDD") return;

        var loadUnload = Find(attrs, 0xC1);
        var startStop = Find(attrs, 0x04);
        if (loadUnload is null && startStop is null) return;

        var profile = SmartVendorProfiles.Match(SelectedSmartDisk?.Model ?? string.Empty);
        ShowSmartWearCycles = true;
        SmartLoadUnloadText = FormatCycleAgainstRating(loadUnload, profile?.TypicalLoadUnloadRatedMax);
        SmartStartStopText = FormatCycleAgainstRating(startStop, profile?.TypicalStartStopRatedMax);
    }

    private static string FormatCycleAgainstRating(SmartRawAttribute? attr, int? typicalRatedMax)
    {
        if (attr is null) return "Not reported";
        string countText = attr.RawValue.ToString("N0");
        if (typicalRatedMax is null) return $"{countText} (no typical rating available for this vendor)";

        double fraction = attr.RawValue / (double)typicalRatedMax.Value;
        string flag = fraction >= 0.8 ? " ⚠ approaching typical rated life" : string.Empty;
        return $"{countText} / ~{typicalRatedMax.Value:N0} typical rated max{flag}";
    }

    /// <summary>#310/#311: host TBW + an approximate write-amplification factor where a
    /// NAND-writes attribute also exists, plus SSD wear-levelling detail cross-checked against the
    /// driver's own MSFT_StorageReliabilityCounter.Wear summary. Shown only when at least one
    /// endurance-family attribute is present (effectively SSD/NVMe media).</summary>
    private void BuildEnduranceSummary(IReadOnlyList<SmartRawAttribute> attrs, int? driverWearPercent, int bytesPerSector)
    {
        var hostWritten = Find(attrs, 0xF1) ?? Find(attrs, 0xF9);
        var nandWritten = Find(attrs, 0xF5) ?? Find(attrs, 0xE2);
        var wearLeveling = Find(attrs, 0xAD);
        var avgErase = Find(attrs, 0xB1);
        var maxErase = Find(attrs, 0xE8);
        var lifeLeft = Find(attrs, 0xE9) ?? Find(attrs, 0xE7);

        if (hostWritten is null && wearLeveling is null && lifeLeft is null && avgErase is null && maxErase is null)
            return; // no endurance-family attributes at all - not an SSD, or this driver doesn't report them

        ShowSmartEndurance = true;

        if (hostWritten is not null)
        {
            double bytesWritten = hostWritten.RawValue * (double)bytesPerSector;
            SmartEnduranceTbwText = $"{Formatting.FormatBytes(bytesWritten)} written over the drive's life (host writes, attribute {hostWritten.IdHex}).";

            if (nandWritten is not null && hostWritten.RawValue > 0)
            {
                double waf = nandWritten.RawValue / (double)hostWritten.RawValue;
                SmartEnduranceWafText = $"Write amplification ≈ {waf:0.00}x (estimate - depends on a vendor-specific NAND-writes attribute pair, {nandWritten.IdHex}/{hostWritten.IdHex}).";
            }
        }
        else
        {
            SmartEnduranceTbwText = "Host LBAs Written not reported by this drive/driver.";
        }

        if (wearLeveling is not null || avgErase is not null || maxErase is not null || lifeLeft is not null)
        {
            var parts = new List<string>();
            if (wearLeveling is not null) parts.Add($"Wear leveling {wearLeveling.Current}");
            if (avgErase is not null) parts.Add($"Avg. block erase {avgErase.RawDisplay}");
            if (maxErase is not null) parts.Add($"Max block erase {maxErase.RawDisplay}");
            if (lifeLeft is not null) parts.Add($"Life left {lifeLeft.Current}%");
            SmartWearLevelText = string.Join(" · ", parts);

            if (lifeLeft is not null && driverWearPercent.HasValue)
            {
                int derivedWear = 100 - lifeLeft.Current;
                int delta = Math.Abs(derivedWear - driverWearPercent.Value);
                SmartWearWarningText = delta > 10
                    ? $"Raw attributes imply ~{derivedWear}% worn, but the driver's own Wear summary reports {driverWearPercent.Value}% - usually a stale driver summary, not a real disagreement. Quick flag, not a verdict."
                    : string.Empty;
            }
        }
    }

    /// <summary>#314-#319: turns one NvmeHealthLog read into the card's display strings/rows.
    /// Unavailable (non-NVMe controller doesn't answer the IOCTL, or the query otherwise failed)
    /// is stated plainly rather than leaving the card blank with no explanation.</summary>
    private void ApplyNvmeHealth(NvmeHealthLog log)
    {
        NvmeCriticalWarnings.Clear();
        if (!log.Available)
        {
            NvmeHealthStatusText = $"NVMe health log unavailable: {log.UnavailableReason}";
            return;
        }

        NvmeHealthStatusText = "NVMe SMART/Health Information Log (page 0x02) read.";

        // #314: all six always shown (not just the set ones) so the card reads as a checklist.
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "Available spare below threshold", IsSet = log.SpareBelowThreshold });
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "Temperature exceeded a critical threshold", IsSet = log.TemperatureExceeded });
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "NVM subsystem reliability degraded", IsSet = log.ReliabilityDegraded });
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "Media placed in read-only mode - back up now", IsSet = log.MediaReadOnly });
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "Volatile memory backup device failed", IsSet = log.VolatileBackupFailed });
        NvmeCriticalWarnings.Add(new NvmeWarningRow { Label = "Persistent Memory Region read-only", IsSet = log.PmrReadOnly });

        // #315: not clamped - Percentage Used legitimately exceeds 100 on a drive past rated endurance.
        NvmeSpareText = $"{log.AvailableSparePercent}%";
        NvmeSpareThresholdText = $"{log.AvailableSpareThresholdPercent}%";
        NvmeSpareBelowThreshold = log.AvailableSpareBelowOwnThreshold;
        NvmePercentUsedText = log.PercentageUsed > 100 ? $"{log.PercentageUsed}% (past rated endurance)" : $"{log.PercentageUsed}%";

        // #316
        string avgRead = log.AverageReadIoBytes is { } ar ? Formatting.FormatBytes(ar) : "n/a (no read commands)";
        string avgWrite = log.AverageWriteIoBytes is { } aw ? Formatting.FormatBytes(aw) : "n/a (no write commands)";
        NvmeDataUnitsText = $"{log.DataUnitsReadTb:0.###} TB read / {log.DataUnitsWrittenTb:0.###} TB written over the drive's life ({log.HostReadCommands:N0} host read / {log.HostWriteCommands:N0} host write commands).";
        NvmeAverageIoSizeText = $"Average I/O size: {avgRead} per read, {avgWrite} per write.";

        // #317: the one unambiguous "this drive is losing data" NVMe signal available without a vendor tool.
        NvmeMediaErrorsPresent = log.MediaAndDataIntegrityErrors != 0;
        string errorLogNote = log.ErrorInfoLogEntryCount > 0
            ? $" {log.ErrorInfoLogEntryCount:N0} entries in the Error Information Log (read it below for detail)."
            : " No entries in the Error Information Log.";
        NvmeMediaErrorText = (log.MediaAndDataIntegrityErrors == 0
            ? "0 media and data integrity errors."
            : $"{log.MediaAndDataIntegrityErrors:N0} media and data integrity error(s) reported.") + errorLogNote;

        // #318: composite + per-sensor temperature, plus cumulative throttle-time counters that
        // survive reboots (unlike the live LibreHardwareMonitorLib reading above this card).
        string tempText = log.CompositeTemperatureC is { } c ? $"{c:0.#}°C composite" : "Composite temperature not reported";
        var sensorParts = log.TemperatureSensorsKelvin
            .Select((k, i) => (Index: i + 1, Kelvin: k))
            .Where(t => t.Kelvin != 0)
            .Select(t => $"Sensor {t.Index}: {t.Kelvin - 273.15:0.#}°C")
            .ToList();
        NvmeTemperatureText = sensorParts.Count > 0 ? $"{tempText} · {string.Join(" · ", sensorParts)}" : tempText;
        NvmeThrottleText = $"Lifetime time above warning threshold: {log.WarningCompositeTempTimeMinutes:N0} min · above critical threshold: {log.CriticalCompositeTempTimeMinutes:N0} min · thermal-mgmt state 1: {log.ThermalMgmtTemp1TransitionCount:N0} transitions / {log.ThermalMgmtTemp1TotalTimeSeconds:N0}s · state 2: {log.ThermalMgmtTemp2TransitionCount:N0} transitions / {log.ThermalMgmtTemp2TotalTimeSeconds:N0}s.";

        // #319
        NvmePowerText = $"{log.PowerOnHours:N0} power-on hours · {log.PowerCycles:N0} power cycles · {log.UnsafeShutdowns:N0} unsafe shutdowns · {log.ControllerBusyTimeMinutes:N0} min controller busy time.";
    }

    /// <summary>#322: Identify Controller facts + best-effort APST detail.</summary>
    private void ApplyNvmeIdentify(NvmeIdentifyInfo info)
    {
        if (!info.Available)
        {
            NvmeIdentifyText = $"Identify Controller unavailable: {info.UnavailableReason}";
            NvmeApstText = string.Empty;
            return;
        }

        string mdtsText = info.MdtsRaw == 0 ? "no limit reported" : $"2^{info.MdtsRaw} pages";
        NvmeIdentifyText = $"{info.ModelNumber} · Serial {info.SerialNumber} · Firmware {info.FirmwareRevision} · {info.NamespaceCount} namespace(s) · MDTS {mdtsText}.";

        if (!info.ApstSupported)
            NvmeApstText = "Autonomous Power State Transition (APST): not supported by this controller.";
        else if (!info.ApstFeatureQuerySucceeded)
            NvmeApstText = $"APST: supported ({info.PowerStateCount} power states) - current enable/configured-state detail unavailable (Get Features follow-up query failed).";
        else
            NvmeApstText = $"APST: supported and {(info.ApstEnabled ? "enabled" : "disabled")} - {info.ApstConfiguredStateCount} of {info.PowerStateCount} power states configured for autonomous transition. Where a drive \"disappears after idle\", this is usually the first place to look.";
    }

    /// <summary>#323: applies a reliability-latency read (or a reset's null clear) to the tiles.</summary>
    private void ApplyReliabilityLatencies(long? readMs, long? writeMs, long? flushMs)
    {
        ReliabilityReadLatencyText = readMs?.ToString("N0") ?? "—";
        ReliabilityWriteLatencyText = writeMs?.ToString("N0") ?? "—";
        ReliabilityFlushLatencyText = flushMs?.ToString("N0") ?? "—";
        ShowReliabilityLatencyTiles = readMs.HasValue || writeMs.HasValue || flushMs.HasValue;
    }

    /// <summary>#320: on-demand Error Information Log (page 0x01) read - a separate log-page round
    /// trip from the health-log bundle above, so it stays behind its own button.</summary>
    private async Task ReadNvmeErrorLogAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        IsReadingNvmeErrorLog = true;
        NvmeErrorLogStatusText = "Reading Error Information Log (page 0x01)...";
        NvmeErrorLog.Clear();
        try
        {
            var (entries, available, reason) = await Task.Run(() => NvmeHealthLogService.ReadErrorLog(disk.Index));
            if (!available)
            {
                NvmeErrorLogStatusText = $"Failed: {reason}";
            }
            else
            {
                foreach (var entry in entries) NvmeErrorLog.Add(entry);
                NvmeErrorLogStatusText = entries.Count == 0
                    ? "No entries - an empty error log is the normal, expected result on a healthy drive."
                    : $"{entries.Count} error log entr{(entries.Count == 1 ? "y" : "ies")} (most recent first).";
            }
        }
        catch (Exception ex)
        {
            NvmeErrorLogStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsReadingNvmeErrorLog = false;
        }
    }

    /// <summary>#321: on-demand Device Self-test Log (page 0x06) read - the last 20 results plus
    /// whatever self-test is currently in progress, if any.</summary>
    private async Task ReadNvmeSelfTestLogAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        IsReadingNvmeSelfTestLog = true;
        NvmeSelfTestStatusText = "Reading Device Self-test Log (page 0x06)...";
        NvmeSelfTestResults.Clear();
        try
        {
            var (results, currentOp, completion, available, reason) = await Task.Run(() => NvmeHealthLogService.ReadSelfTestLog(disk.Index));
            if (!available)
            {
                NvmeSelfTestStatusText = $"Failed: {reason}";
            }
            else
            {
                foreach (var result in results) NvmeSelfTestResults.Add(result);
                string progress = currentOp.StartsWith("No self-test", StringComparison.Ordinal) ? currentOp : $"{currentOp} ({completion}% complete)";
                NvmeSelfTestStatusText = results.Count == 0
                    ? $"{progress}. No past self-test results recorded."
                    : $"{progress}. {results.Count} past result(s) shown (most recent first).";
            }
        }
        catch (Exception ex)
        {
            NvmeSelfTestStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsReadingNvmeSelfTestLog = false;
        }
    }

    /// <summary>#321: Short/Extended self-test trigger - see NvmeHealthLogService.TriggerSelfTest's
    /// remarks for why this is currently a stated "not yet wired to hardware" rather than an
    /// unverified admin-command encoding sent to a physical controller.</summary>
    private void RunNvmeSelfTest(bool extended)
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;
        var (_, message) = NvmeHealthLogService.TriggerSelfTest(disk.Index, extended);
        NvmeSelfTestTriggerStatusText = message;
    }

    /// <summary>#323: invokes MSFT_StorageReliabilityCounter.ResetStatistics() then immediately
    /// re-reads the three latency tiles so they reflect the reset rather than showing stale
    /// pre-reset maxima until the next full "Read SMART details" click.</summary>
    private async Task ResetReliabilityCountersAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        ReliabilityResetStatusText = "Resetting...";
        try
        {
            string message = await Task.Run(() => SystemSpecsService.ResetReliabilityCounters(disk.Index));
            ReliabilityResetStatusText = message;
            var (readLatency, writeLatency, flushLatency) = await Task.Run(() => SystemSpecsService.ReadReliabilityLatencies(disk.Index));
            ApplyReliabilityLatencies(readLatency, writeLatency, flushLatency);
        }
        catch (Exception ex)
        {
            ReliabilityResetStatusText = $"Failed: {ex.Message}";
        }
    }

    private async Task ScanLargestItemsAsync()
    {
        if (IsScanningLargestItems) return;
        string root = LargestItemsRoot;
        if (!Directory.Exists(root))
        {
            LargestItemsStatusText = "Path not found.";
            return;
        }

        IsScanningLargestItems = true;
        LargestItemsStatusText = "Scanning (depth-capped, this may take a moment on a large tree)...";
        LargestItems.Clear();
        try
        {
            var results = await Task.Run(() => LargestItemsService.Scan(root, maxDepth: 6, topN: 30));
            foreach (var item in results)
            {
                LargestItems.Add(new LargestItemRow
                {
                    Path = item.Path,
                    SizeText = Formatting.FormatBytes(item.SizeBytes),
                    Kind = item.IsDirectory ? "Folder" : "File",
                });
            }
            LargestItemsStatusText = results.Count == 0
                ? "Nothing found (or everything under this path was inaccessible)."
                : $"Top {results.Count} items under {root} (depth-capped, folders shown with their recursive total).";
        }
        catch (Exception ex)
        {
            LargestItemsStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningLargestItems = false;
        }
    }

    private async Task RunThroughputTestAsync()
    {
        string? drive = SelectedThroughputDrive;
        if (drive is null || IsThroughputTesting) return;

        IsThroughputTesting = true;
        ThroughputResultText = "Testing (writing then reading a temp file)...";
        try
        {
            var result = await StorageThroughputService.RunTestAsync(drive);
            ThroughputResultText = result.Message;
        }
        catch (Exception ex)
        {
            ThroughputResultText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsThroughputTesting = false;
        }
    }
}
