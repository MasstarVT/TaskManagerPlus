using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
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

    // ---- Round 14, #324: live predicted-failure watch -------------------------------------
    // Piggybacked onto PerformanceViewModel's existing tick (Sampled) rather than a new heavy
    // timer - see PerformanceViewModel.Sampled's remarks. DriveFailureAlerts is a standing,
    // Summary-tab-visible HealthIssue list (mirrored into SummaryViewModel.HealthIssues) that only
    // grows/shrinks on an actual transition, so it doesn't flicker every tick.
    public ObservableCollection<HealthIssue> DriveFailureAlerts { get; } = new();
    private readonly Dictionary<int, bool> _lastPredictFailure = new();
    private int _failureWatchTickCounter;
    private bool _failureWatchRunning;

    // ---- #325/#326/#327: SMART history journal, trend chart, wear-rate projection ---------
    public ObservableCollection<SmartHistoryChange> SmartHistoryChanges { get; } = new();

    private string _smartHistoryStatusText = string.Empty;
    public string SmartHistoryStatusText { get => _smartHistoryStatusText; private set => SetProperty(ref _smartHistoryStatusText, value); }

    // #326: glow/core series pair sharing one ObservableCollection<double> each, same styling as
    // PerformanceViewModel.LineOf - hidden until at least three snapshots exist for this disk.
    private readonly ObservableCollection<double> _reallocatedHistory = new();
    private readonly ObservableCollection<double> _pendingHistory = new();
    public ISeries[] SmartTrendSeries { get; private set; } = Array.Empty<ISeries>();

    private bool _showSmartTrendChart;
    public bool ShowSmartTrendChart { get => _showSmartTrendChart; private set => SetProperty(ref _showSmartTrendChart, value); }

    private string _smartTrendRangeText = string.Empty;
    public string SmartTrendRangeText { get => _smartTrendRangeText; private set => SetProperty(ref _smartTrendRangeText, value); }

    // #327
    private bool _showWearProjection;
    public bool ShowWearProjection { get => _showWearProjection; private set => SetProperty(ref _showWearProjection, value); }

    private string _wearProjectionText = string.Empty;
    public string WearProjectionText { get => _wearProjectionText; private set => SetProperty(ref _wearProjectionText, value); }

    // ---- #328: per-drive health verdict ----------------------------------------------------
    public ObservableCollection<DriveHealthVerdict> DriveHealthVerdicts { get; } = new();

    // ---- #329: Windows' own disk-diagnosis events ------------------------------------------
    public ObservableCollection<DiskDiagnosisEvent> DiskDiagnosisEvents { get; } = new();

    private string _diskDiagnosisStatusText = "Not checked";
    public string DiskDiagnosisStatusText { get => _diskDiagnosisStatusText; private set => SetProperty(ref _diskDiagnosisStatusText, value); }

    private bool _isCheckingDiskDiagnosis;
    public bool IsCheckingDiskDiagnosis { get => _isCheckingDiskDiagnosis; private set => SetProperty(ref _isCheckingDiskDiagnosis, value); }

    public AsyncRelayCommand CheckDiskDiagnosisCommand { get; }

    // ---- #330/#331/#336: unified bad-sector view + pending-sector re-check ----------------
    private BadSectorSummary? _badSectorSummary;
    public BadSectorSummary? BadSectorSummary { get => _badSectorSummary; private set => SetProperty(ref _badSectorSummary, value); }

    private string _badSectorStatusText = string.Empty;
    public string BadSectorStatusText { get => _badSectorStatusText; private set => SetProperty(ref _badSectorStatusText, value); }

    private bool _isCheckingBadSectors;
    public bool IsCheckingBadSectors { get => _isCheckingBadSectors; private set => SetProperty(ref _isCheckingBadSectors, value); }

    public AsyncRelayCommand CheckBadSectorsCommand { get; }

    private bool _isRecheckingPendingSectors;
    public bool IsRecheckingPendingSectors { get => _isRecheckingPendingSectors; private set => SetProperty(ref _isRecheckingPendingSectors, value); }

    private string _pendingSectorRecheckText = string.Empty;
    public string PendingSectorRecheckText { get => _pendingSectorRecheckText; private set => SetProperty(ref _pendingSectorRecheckText, value); }

    public AsyncRelayCommand RecheckPendingSectorsCommand { get; }

    // #336: bad-block/retry event correlation, folded into the same bad-sector card.
    private string _badBlockEventCorrelationText = string.Empty;
    public string BadBlockEventCorrelationText { get => _badBlockEventCorrelationText; private set => SetProperty(ref _badBlockEventCorrelationText, value); }

    // ---- #332/#335: read-only surface scan + bad-LBA-to-file mapping ----------------------
    public ObservableCollection<SurfaceScanResult> SurfaceScanResults { get; } = new();

    private double _surfaceScanStallThresholdMs = 500;
    public double SurfaceScanStallThresholdMs { get => _surfaceScanStallThresholdMs; set => SetProperty(ref _surfaceScanStallThresholdMs, value); }

    private bool _isSurfaceScanning;
    public bool IsSurfaceScanning { get => _isSurfaceScanning; private set => SetProperty(ref _isSurfaceScanning, value); }

    private double _surfaceScanProgressPercent;
    public double SurfaceScanProgressPercent { get => _surfaceScanProgressPercent; private set => SetProperty(ref _surfaceScanProgressPercent, value); }

    private string _surfaceScanStatusText = "Not scanned";
    public string SurfaceScanStatusText { get => _surfaceScanStatusText; private set => SetProperty(ref _surfaceScanStatusText, value); }

    private CancellationTokenSource? _surfaceScanCts;
    public AsyncRelayCommand StartSurfaceScanCommand { get; }
    public RelayCommand CancelSurfaceScanCommand { get; }

    // ---- #333: file-level read verification ------------------------------------------------
    private string _fileVerificationRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
    public string FileVerificationRoot { get => _fileVerificationRoot; set => SetProperty(ref _fileVerificationRoot, value); }

    public ObservableCollection<FileVerificationFailure> FileVerificationFailures { get; } = new();

    private bool _isVerifyingFiles;
    public bool IsVerifyingFiles { get => _isVerifyingFiles; private set => SetProperty(ref _isVerifyingFiles, value); }

    private string _fileVerificationStatusText = "Not checked";
    public string FileVerificationStatusText { get => _fileVerificationStatusText; private set => SetProperty(ref _fileVerificationStatusText, value); }

    private CancellationTokenSource? _fileVerificationCts;
    public AsyncRelayCommand VerifyFilesCommand { get; }
    public RelayCommand CancelFileVerificationCommand { get; }

    // ---- #334: ATA short/extended self-test (structure in place; issuance stubbed) --------
    private bool _showAtaSelfTest;
    public bool ShowAtaSelfTest { get => _showAtaSelfTest; private set => SetProperty(ref _showAtaSelfTest, value); }

    public string AtaSelfTestEstimatedDurationText => AtaSelfTestService.EstimatedDurationText;

    private string _ataSelfTestStatusText = string.Empty;
    public string AtaSelfTestStatusText { get => _ataSelfTestStatusText; private set => SetProperty(ref _ataSelfTestStatusText, value); }

    public RelayCommand RunAtaShortSelfTestCommand { get; }
    public RelayCommand RunAtaExtendedSelfTestCommand { get; }

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

        // #329
        CheckDiskDiagnosisCommand = new AsyncRelayCommand(CheckDiskDiagnosisAsync, () => !IsCheckingDiskDiagnosis);

        // #330/#336: needs a disk (for the SMART-side counts) and a drive letter (for chkdsk/
        // $BadClus) - gated on a disk being selected, same as the raw-attribute read above.
        CheckBadSectorsCommand = new AsyncRelayCommand(CheckBadSectorsAsync, () => !IsCheckingBadSectors && SelectedSmartDisk is not null);

        // #331: only meaningful once a pending-sector count is already on screen.
        RecheckPendingSectorsCommand = new AsyncRelayCommand(RecheckPendingSectorsAsync, () => !IsRecheckingPendingSectors && SelectedSmartDisk is not null);

        // #332
        StartSurfaceScanCommand = new AsyncRelayCommand(StartSurfaceScanAsync, () => !IsSurfaceScanning && SelectedSmartDisk is not null);
        CancelSurfaceScanCommand = new RelayCommand(() => _surfaceScanCts?.Cancel(), () => IsSurfaceScanning);

        // #333
        VerifyFilesCommand = new AsyncRelayCommand(VerifyFilesAsync, () => !IsVerifyingFiles && Directory.Exists(FileVerificationRoot));
        CancelFileVerificationCommand = new RelayCommand(() => _fileVerificationCts?.Cancel(), () => IsVerifyingFiles);

        // #334
        RunAtaShortSelfTestCommand = new RelayCommand(() => RunAtaSelfTest(extended: false), () => ShowAtaSelfTest && SelectedSmartDisk is not null);
        RunAtaExtendedSelfTestCommand = new RelayCommand(() => RunAtaSelfTest(extended: true), () => ShowAtaSelfTest && SelectedSmartDisk is not null);

        // #324: subscribe to the shared sampler's tick rather than owning a new heavy timer -
        // see PerformanceViewModel.Sampled's remarks.
        Performance.Sampled += OnPerformanceSampled;

        _ = Task.Run(() =>
        {
            var pools = StorageSpacesService.List();
            var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            var hddDrives = fixedDrives
                .Select(d => d.Name.TrimEnd('\\'))
                .Where(letter => DiskFragmentationService.GetMediaType(letter) == "HDD")
                .ToList();
            var disks = SystemSpecsService.ListDisksForSmart();

            // #328: cheap base facts (predicted failure, driver health) for every disk, computed
            // once at startup - refined per-disk once that disk's SMART/NVMe data is actually read
            // (see ApplySmartRawResult/ApplyNvmeHealth below).
            var failureFlags = SystemSpecsService.ReadDiskFailureFlags().ToDictionary(f => f.Index);
            var poolsForVerdict = pools;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var p in pools) StoragePools.Add(p);
                foreach (var letter in hddDrives) HddVolumes.Add(new FragmentationRow { DriveLetter = letter });
                foreach (var (index, model) in disks) SmartDiskOptions.Add(new SmartDiskOption { Index = index, Model = model });
                foreach (var d in fixedDrives) ThroughputDriveOptions.Add(d.Name.TrimEnd('\\'));
                SelectedThroughputDrive = ThroughputDriveOptions.FirstOrDefault();

                foreach (var (index, model) in disks)
                {
                    failureFlags.TryGetValue(index, out var flag);
                    var verdict = new DriveHealthVerdict { Index = index, Model = model };
                    ApplyBaseVerdictFacts(verdict, flag, poolsForVerdict);
                    DriveHealthVerdicts.Add(verdict);
                    if (flag is not null) _lastPredictFailure[index] = flag.PredictFailure == true;
                }
            });
        });
    }

    /// <summary>#324: fired once per PerformanceViewModel sample tick. Throttled to roughly once
    /// every 10 ticks (rather than every tick) so a fast poll interval doesn't turn this into a WMI
    /// round trip several times a second, and skipped entirely while a previous check is still in
    /// flight.</summary>
    private void OnPerformanceSampled()
    {
        if (_failureWatchRunning) return;
        if (++_failureWatchTickCounter < 10) return;
        _failureWatchTickCounter = 0;
        _failureWatchRunning = true;

        _ = Task.Run(() =>
        {
            try
            {
                var flags = SystemSpecsService.ReadDiskFailureFlags();
                System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyFailureWatch(flags));
            }
            finally
            {
                _failureWatchRunning = false;
            }
        });
    }

    /// <summary>#324: on a false->true transition, fires a tray toast and adds a persistent
    /// Summary-tab HealthIssue; on a true->false transition (recovered, or the drive was replaced),
    /// clears any standing alert for that disk so the Summary tab doesn't keep showing a resolved
    /// warning forever.</summary>
    private void ApplyFailureWatch(List<DiskFailureFlag> flags)
    {
        foreach (var flag in flags)
        {
            bool nowFailing = flag.PredictFailure == true || string.Equals(flag.DriverHealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase);
            bool wasFailing = _lastPredictFailure.TryGetValue(flag.Index, out var prev) && prev;
            _lastPredictFailure[flag.Index] = nowFailing;

            string model = SmartDiskOptions.FirstOrDefault(d => d.Index == flag.Index)?.Model ?? $"Disk {flag.Index}";

            if (nowFailing && !wasFailing)
            {
                string message = $"{model} now shows a SMART failure prediction - back up its data soon.";
                ToastService.Show("Drive failure predicted", message, isCritical: true);
                DriveFailureAlerts.Add(new HealthIssue { Message = message, IsCritical = true });
            }
            else if (!nowFailing && wasFailing)
            {
                var toRemove = DriveFailureAlerts.Where(i => i.Message.StartsWith(model, StringComparison.Ordinal)).ToList();
                foreach (var i in toRemove) DriveFailureAlerts.Remove(i);
            }

            // Keep the #328 verdict list's predicted-failure reason in sync with this same live watch.
            var verdict = DriveHealthVerdicts.FirstOrDefault(v => v.Index == flag.Index);
            if (verdict is not null) RecomputeVerdict(verdict, flag);
        }
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
        NvmeHealthLog? nvmeHealthLog = null;
        try
        {
            var result = await Task.Run(() => SmartRawAttributeService.Read(disk.Index, disk.Model));
            ApplySmartRawResult(result);

            // #328: refine this disk's verdict now that real SMART-attribute facts are on hand.
            var smartFacts = FactsFor(disk.Index);
            var pendingAttr = Find(result.Attributes, 0xC5);
            smartFacts.CriticalAttributeCount = SmartTriageTiles.Count(t => t.IsCritical);
            smartFacts.PendingSectors = pendingAttr is null ? 0 : (int)Math.Min(pendingAttr.RawValue, int.MaxValue);
            var smartVerdict = DriveHealthVerdicts.FirstOrDefault(v => v.Index == disk.Index);
            if (smartVerdict is not null) RecomputeVerdict(smartVerdict, smartFacts, StoragePools.ToList());

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
                if (healthLog.Available) nvmeHealthLog = healthLog;

                var identify = await Task.Run(() => NvmeHealthLogService.ReadIdentify(disk.Index));
                ApplyNvmeIdentify(identify);

                // #328: fold NVMe critical-warning/media-error facts into the same verdict.
                var nvmeFacts = FactsFor(disk.Index);
                nvmeFacts.NvmeCriticalWarningCount = NvmeCriticalWarnings.Count(w => w.IsSet);
                nvmeFacts.NvmeMediaErrors = NvmeMediaErrorsPresent;
                var nvmeVerdict = DriveHealthVerdicts.FirstOrDefault(v => v.Index == disk.Index);
                if (nvmeVerdict is not null) RecomputeVerdict(nvmeVerdict, nvmeFacts, StoragePools.ToList());
            }

            // #325/#326/#327: persist this snapshot, show what's changed since last run, and
            // (re)build the trend chart + wear-rate projection - all from the same read above.
            RecordSmartHistoryAndProject(disk, result, nvmeHealthLog);

            // #334: ATA self-test structure only applies to a non-NVMe (ATA/SATA) drive that
            // actually answered the raw-attribute read above.
            ShowAtaSelfTest = !ShowNvmeHealth && !result.Unavailable;
            AtaSelfTestStatusText = string.Empty;
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

        // #325/#326/#327/#334: reset the history/trend/projection/self-test display so a previous
        // disk's data doesn't linger while the newly selected disk's data is still being read.
        SmartHistoryChanges.Clear();
        SmartHistoryStatusText = string.Empty;
        ShowSmartTrendChart = false;
        SmartTrendRangeText = string.Empty;
        SmartTrendSeries = Array.Empty<ISeries>();
        OnPropertyChanged(nameof(SmartTrendSeries));
        ShowWearProjection = false;
        WearProjectionText = string.Empty;
        ShowAtaSelfTest = false;
        AtaSelfTestStatusText = string.Empty;
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

    // ================================================================================
    // #328: per-drive health verdict - the individual facts arrive at different times (predicted
    // failure/pool status at startup, critical-attribute/pending-sector counts only once SMART is
    // read, NVMe critical-warning/media-error counts only once that log is read), so each disk's
    // running facts are tracked separately and the verdict is recomputed from the full set every
    // time any one of them changes, rather than trying to patch Level/Reasons incrementally.
    // ================================================================================

    private sealed class VerdictFacts
    {
        public bool? PredictFailure;
        public int CriticalAttributeCount;
        public int PendingSectors;
        public int NvmeCriticalWarningCount;
        public bool NvmeMediaErrors;
    }

    private readonly Dictionary<int, VerdictFacts> _verdictFacts = new();

    private VerdictFacts FactsFor(int index)
    {
        if (!_verdictFacts.TryGetValue(index, out var f)) _verdictFacts[index] = f = new VerdictFacts();
        return f;
    }

    /// <summary>Seeds a disk's verdict from the cheap, always-available facts (predicted failure +
    /// pool membership) at startup - refined later, per disk, once that disk's SMART/NVMe data is
    /// actually read (see the RecomputeVerdict calls inside ReadSmartDetailsAsync).</summary>
    private void ApplyBaseVerdictFacts(DriveHealthVerdict verdict, DiskFailureFlag? flag, List<StorageSpaceInfo> pools)
    {
        var facts = FactsFor(verdict.Index);
        facts.PredictFailure = flag?.PredictFailure;
        RecomputeVerdict(verdict, facts, pools);
    }

    private void RecomputeVerdict(DriveHealthVerdict verdict, DiskFailureFlag flag)
    {
        var facts = FactsFor(verdict.Index);
        facts.PredictFailure = flag.PredictFailure;
        RecomputeVerdict(verdict, facts, StoragePools.ToList());
    }

    /// <summary>Deliberately three-state and reason-listing rather than a made-up numeric score -
    /// see DriveHealthVerdict's remarks. "Replace" fires only on the two unambiguous signals
    /// (predicted failure, confirmed NVMe media errors); everything else is "Watch".</summary>
    private static void RecomputeVerdict(DriveHealthVerdict verdict, VerdictFacts facts, List<StorageSpaceInfo> pools)
    {
        verdict.Reasons.Clear();
        bool replace = false;
        bool watch = false;

        if (facts.PredictFailure == true)
        {
            verdict.Reasons.Add("SMART failure prediction flag is set");
            replace = true;
        }
        if (facts.NvmeMediaErrors)
        {
            verdict.Reasons.Add("NVMe media/data integrity errors reported");
            replace = true;
        }
        if (facts.NvmeCriticalWarningCount > 0)
        {
            verdict.Reasons.Add($"{facts.NvmeCriticalWarningCount} NVMe critical warning bit(s) set");
            watch = true;
        }
        if (facts.PendingSectors > 0)
        {
            verdict.Reasons.Add($"{facts.PendingSectors} current pending sector(s) (SMART C5)");
            watch = true;
        }
        if (facts.CriticalAttributeCount > 0)
        {
            verdict.Reasons.Add($"{facts.CriticalAttributeCount} critical SMART attribute(s) non-zero");
            watch = true;
        }
        if (pools.Any(p => p.IsHealthWarning))
        {
            verdict.Reasons.Add("A Storage Spaces pool on this system reports a health warning");
            watch = true;
        }

        verdict.Level = replace ? DriveHealthLevel.Replace : watch ? DriveHealthLevel.Watch : DriveHealthLevel.Healthy;
    }

    // ================================================================================
    // #325/#326/#327: SMART history journal, trend chart, wear-rate projection.
    // ================================================================================

    private const float TrendCoreStrokeWidth = 2f;
    private const float TrendGlowStrokeWidth = 7f;

    /// <summary>#326: same glow/core line-series-pair styling as PerformanceViewModel.LineOf (a
    /// thick, translucent glow stroke drawn first, then a crisp 2px core stroke with a top-to-bottom
    /// gradient fill on top) - duplicated here rather than shared, since PerformanceViewModel's
    /// helper is private and this chart's data source (a persisted snapshot history, not a live
    /// poll) doesn't belong on that class.</summary>
    private static (LineSeries<double> Glow, LineSeries<double> Core) TrendLineOf(ObservableCollection<double> values, SKColor color, string name)
    {
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), TrendGlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
            IsHoverable = false,
            IsVisibleAtLegend = false,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, TrendCoreStrokeWidth),
            Fill = new LinearGradientPaint(color.WithAlpha(90), color.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
        };
        return (glow, core);
    }

    private void RecordSmartHistoryAndProject(SmartDiskOption disk, SmartRawResult result, NvmeHealthLog? nvmeLog)
    {
        if (result.Unavailable)
        {
            SmartHistoryStatusText = "No raw SMART data available - nothing to record.";
            return;
        }

        var hostWritten = Find(result.Attributes, 0xF1) ?? Find(result.Attributes, 0xF9);
        var lifeLeft = Find(result.Attributes, 0xE9) ?? Find(result.Attributes, 0xE7);

        var entry = new SmartHistoryEntry
        {
            DiskKey = $"{disk.Index}:{disk.Model}",
            Timestamp = DateTime.Now,
            Reallocated = Find(result.Attributes, 0x05) is { } a05 ? (int)a05.RawValue : 0,
            PendingSector = Find(result.Attributes, 0xC5) is { } aC5 ? (int)aC5.RawValue : 0,
            OfflineUncorrectable = Find(result.Attributes, 0xC6) is { } aC6 ? (int)aC6.RawValue : 0,
            ReportedUncorrectable = Find(result.Attributes, 0xBB) is { } aBB ? (int)aBB.RawValue : 0,
            UdmaCrcErrors = Find(result.Attributes, 0xC7) is { } aC7 ? (int)aC7.RawValue : 0,
            NvmePercentageUsed = nvmeLog?.PercentageUsed,
            NvmeAvailableSparePercent = nvmeLog?.AvailableSparePercent,
            NvmeDataUnitsWrittenTb = nvmeLog?.DataUnitsWrittenTb,
            HostWrittenBytes = hostWritten is null ? null : hostWritten.RawValue * (double)result.BytesPerSector,
            SataLifeLeftPercent = lifeLeft?.Current,
        };

        var changes = SmartHistoryService.RecordIfNew(entry);
        SmartHistoryChanges.Clear();
        foreach (var c in changes) SmartHistoryChanges.Add(c);
        SmartHistoryStatusText = changes.Count == 0
            ? "No change since the last recorded snapshot for this disk (or this is the first snapshot ever taken for it)."
            : $"{changes.Count} attribute(s) changed since the last recorded snapshot.";

        // #326: trend chart, hidden until at least three snapshots exist for this disk.
        var history = SmartHistoryService.ForDisk(entry.DiskKey);
        if (history.Count >= 3)
        {
            _reallocatedHistory.Clear();
            _pendingHistory.Clear();
            foreach (var h in history)
            {
                _reallocatedHistory.Add(h.Reallocated);
                _pendingHistory.Add(h.PendingSector);
            }
            var (reallocGlow, reallocCore) = TrendLineOf(_reallocatedHistory, new SKColor(0xE0, 0x57, 0x57), "Reallocated");
            var (pendingGlow, pendingCore) = TrendLineOf(_pendingHistory, new SKColor(0xE0, 0xA9, 0x30), "Pending");
            SmartTrendSeries = new ISeries[] { reallocGlow, reallocCore, pendingGlow, pendingCore };
            OnPropertyChanged(nameof(SmartTrendSeries));
            SmartTrendRangeText = $"{history.Count} snapshots, {history[0].Timestamp:d} – {history[^1].Timestamp:d}.";
            ShowSmartTrendChart = true;
        }
        else
        {
            ShowSmartTrendChart = false;
            SmartTrendRangeText = history.Count == 0 ? string.Empty : $"{history.Count} snapshot(s) recorded so far - the trend chart needs at least 3.";
        }

        // #327: wear-rate projection from the same recorded history.
        WearProjectionText = ComputeWearProjection(history);
        ShowWearProjection = WearProjectionText.Length > 0;
    }

    /// <summary>#327: TB written per day from the recorded history, projected forward to the date
    /// the drive reaches 100% Percentage Used (NVMe) or 0% life left (SATA SSD). Always captioned
    /// as an extrapolation from recent behavior, never a warranty statement - a drive's real write
    /// rate can change at any time.</summary>
    private static string ComputeWearProjection(List<SmartHistoryEntry> history)
    {
        if (history.Count < 2) return string.Empty;

        var first = history[0];
        var last = history[^1];
        double days = (last.Timestamp - first.Timestamp).TotalDays;
        if (days < 1) return string.Empty; // not enough elapsed time between snapshots for a meaningful rate

        if (last.NvmePercentageUsed is { } usedNow && first.NvmePercentageUsed is { } usedFirst)
        {
            double usedPerDay = (usedNow - usedFirst) / days;
            if (usedPerDay <= 0)
                return "NVMe percentage-used hasn't increased across the recorded history - no meaningful wear-out date to project yet.";
            double daysRemaining = (100 - usedNow) / usedPerDay;
            var projected = DateTime.Now.AddDays(daysRemaining);
            return $"At the recent rate of {usedPerDay:0.###}%/day, this drive is projected to reach 100% Percentage Used around {projected:d} (in ~{daysRemaining:0} days) - extrapolation from your recent write rate, not a warranty statement.";
        }

        if (last.SataLifeLeftPercent is { } lifeNow && first.SataLifeLeftPercent is { } lifeFirst)
        {
            double lifePerDay = (lifeFirst - lifeNow) / days;
            if (lifePerDay <= 0)
                return "SATA SSD life-left percentage hasn't decreased across the recorded history - no meaningful wear-out date to project yet.";
            double daysRemaining = lifeNow / lifePerDay;
            var projected = DateTime.Now.AddDays(daysRemaining);
            return $"At the recent rate of {lifePerDay:0.###}%/day, this drive is projected to reach 0% life left around {projected:d} (in ~{daysRemaining:0} days) - extrapolation from your recent write rate, not a warranty statement.";
        }

        if (last.HostWrittenBytes is { } bytesNow && first.HostWrittenBytes is { } bytesFirst && bytesNow > bytesFirst)
        {
            double tbPerDay = (bytesNow - bytesFirst) / days / 1_000_000_000_000.0;
            return $"Writing approximately {tbPerDay:0.###} TB/day over the recorded history (no life-left/percentage-used figure available on this drive to project a wear-out date from) - extrapolation from your recent write rate, not a warranty statement.";
        }

        return string.Empty;
    }

    // ================================================================================
    // #329: Windows' own disk-diagnosis events.
    // ================================================================================

    private async Task CheckDiskDiagnosisAsync()
    {
        IsCheckingDiskDiagnosis = true;
        DiskDiagnosisStatusText = "Checking Windows disk-diagnosis events...";
        DiskDiagnosisEvents.Clear();
        try
        {
            var events = await Task.Run(() => DiskDiagnosisEventService.ReadDiskDiagnosisEvents());
            foreach (var e in events) DiskDiagnosisEvents.Add(e);
            DiskDiagnosisStatusText = events.Count == 0
                ? "No disk-diagnosis or predicted-failure events found in the last 30 days."
                : $"{events.Count} event(s) in the last 30 days (most recent first).";
        }
        catch (Exception ex)
        {
            DiskDiagnosisStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCheckingDiskDiagnosis = false;
        }
    }

    // ================================================================================
    // #330/#331/#336: unified bad-sector view, pending-sector re-check, bad-block/retry
    // event correlation.
    // ================================================================================

    private async Task CheckBadSectorsAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        IsCheckingBadSectors = true;
        BadSectorStatusText = "Checking SMART counters, the latest chkdsk report, and $BadClus allocation...";
        BadBlockEventCorrelationText = string.Empty;
        try
        {
            // SMART side reuses whatever raw attributes are already on screen for this disk (the
            // same precondition the pending-sector re-check below shares) rather than re-reading
            // them a second time here.
            int? reallocated = Find(SmartRawAttributes, 0x05) is { } ra ? (int)ra.RawValue : null;
            int? pending = Find(SmartRawAttributes, 0xC5) is { } pa ? (int)pa.RawValue : null;
            int? offlineUncorr = Find(SmartRawAttributes, 0xC6) is { } oa ? (int)oa.RawValue : null;

            var (chkdskKb, chkdskDate) = await Task.Run(() => BadSectorService.ReadLatestChkdskBadSectors());

            // $BadClus is a volume-level figure - resolve the first fixed volume physically hosted
            // on this disk, the same association ClusterMappingService's #335 lookup uses.
            var volume = await Task.Run(() => ClusterMappingService.ResolveVolumeForDisk(disk.Index));
            long? badClusBytes = null;
            string? badClusVolume = null;
            if (volume is { } v)
            {
                badClusBytes = await BadSectorService.ReadBadClusAllocatedBytesAsync(v.DriveLetter);
                badClusVolume = $"{v.DriveLetter}:";
            }

            var summary = new BadSectorSummary
            {
                SmartReallocated = reallocated,
                SmartPending = pending,
                SmartOfflineUncorrectable = offlineUncorr,
                ChkdskBadSectorsKb = chkdskKb,
                ChkdskReportDate = chkdskDate,
                BadClusAllocatedBytes = badClusBytes,
                BadClusVolume = badClusVolume,
            };
            BadSectorSummary = summary;

            BadSectorStatusText = !summary.HasAnySource
                ? "None of the three sources reported anything for this disk (SMART attributes weren't read yet, chkdsk has never logged a report, and $BadClus couldn't be read)."
                : summary.SourcesDisagree
                    ? "These sources disagree about whether this disk/volume has bad sectors - shown as-is below, not silently reconciled."
                    : "All available sources checked.";

            // #336: bad-block/retry event correlation, folded into the same card.
            var badBlockEvents = await Task.Run(() => DiskDiagnosisEventService.ReadBadBlockAndRetryEvents());
            int badBlockCount = badBlockEvents.Count(e => e.EventId == 7);
            int retryCount = badBlockEvents.Count(e => e.EventId == 153);
            bool escalate = pending is > 0 && retryCount > 0;
            BadBlockEventCorrelationText = badBlockEvents.Count == 0
                ? "No bad-block or I/O-retry events (System log, source Disk) found in the last 30 days."
                : $"{badBlockCount} bad-block event(s), {retryCount} I/O-retry event(s) in the last 30 days." +
                  (escalate ? " Both rising pending sectors AND recent retry events are present here - a stronger deterioration signal than either alone." : string.Empty);
        }
        catch (Exception ex)
        {
            BadSectorStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCheckingBadSectors = false;
        }
    }

    /// <summary>#331: "there is no simple documented Windows API to force a re-read of specific
    /// LBAs" - this re-reads the whole SMART table now and diffs Current Pending Sector/Reallocated
    /// against the values on screen before the click, labelled accurately as a re-check rather than
    /// a targeted sector re-read.</summary>
    private async Task RecheckPendingSectorsAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        ulong before = Find(SmartRawAttributes, 0xC5)?.RawValue ?? 0;
        ulong reallocatedBefore = Find(SmartRawAttributes, 0x05)?.RawValue ?? 0;

        IsRecheckingPendingSectors = true;
        PendingSectorRecheckText = "Re-checking SMART now (read-only - re-reads the whole SMART table; this does not target specific sectors)...";
        try
        {
            var result = await Task.Run(() => SmartRawAttributeService.Read(disk.Index, disk.Model));
            if (result.Unavailable)
            {
                PendingSectorRecheckText = $"Re-check failed: {result.UnavailableReason}";
                return;
            }

            ulong after = Find(result.Attributes, 0xC5)?.RawValue ?? 0;
            ulong reallocatedAfter = Find(result.Attributes, 0x05)?.RawValue ?? 0;

            long pendingDelta = (long)after - (long)before;
            long reallocatedDelta = (long)reallocatedAfter - (long)reallocatedBefore;
            PendingSectorRecheckText = pendingDelta == 0 && reallocatedDelta == 0
                ? $"No change: pending sectors still {after}, reallocated still {reallocatedAfter}. Click \"Read SMART details\" above for a full grid refresh."
                : $"Pending sectors {before} → {after} ({(pendingDelta > 0 ? "+" : string.Empty)}{pendingDelta}), reallocated {reallocatedBefore} → {reallocatedAfter} ({(reallocatedDelta > 0 ? "+" : string.Empty)}{reallocatedDelta}). Click \"Read SMART details\" above for a full grid refresh.";
        }
        catch (Exception ex)
        {
            PendingSectorRecheckText = $"Re-check failed: {ex.Message}";
        }
        finally
        {
            IsRecheckingPendingSectors = false;
        }
    }

    // ================================================================================
    // #332/#335: read-only surface scan + bad-LBA-to-file mapping.
    // ================================================================================

    private async Task StartSurfaceScanAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        _surfaceScanCts = new CancellationTokenSource();
        var token = _surfaceScanCts.Token;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        IsSurfaceScanning = true;
        SurfaceScanProgressPercent = 0;
        SurfaceScanResults.Clear();
        SurfaceScanStatusText = "Scanning - reads only, never writes. This can take hours on a large HDD.";
        try
        {
            var (success, message, _) = await Task.Run(() => SurfaceScanService.Scan(
                disk.Index,
                SurfaceScanStallThresholdMs,
                problem => dispatcher?.Invoke(() =>
                {
                    // Cap the grid so a badly failing drive can't turn this into an unbounded UI list.
                    if (SurfaceScanResults.Count < 500) SurfaceScanResults.Add(problem);
                }),
                progress => dispatcher?.Invoke(() =>
                {
                    SurfaceScanProgressPercent = progress.TotalLba > 0 ? Math.Min(100.0, (double)progress.CurrentLba / progress.TotalLba * 100.0) : 0;
                    SurfaceScanStatusText = progress.TotalLba > 0
                        ? $"Scanning... {SurfaceScanProgressPercent:0.0}% ({progress.CurrentLba:N0} / {progress.TotalLba:N0} sectors), {progress.ProblemsFound} problem(s) found so far."
                        : $"Scanning... {progress.CurrentLba:N0} sectors read so far, {progress.ProblemsFound} problem(s) found (total size unknown).";
                }),
                token), token);

            SurfaceScanStatusText = message;
            SurfaceScanProgressPercent = 100;

            // #335: best-effort LBA -> owning-file resolution, only after the scan itself finishes
            // - doing this inline during the scan would slow the scan for a feature that's purely
            // informational afterwards.
            if (success && SurfaceScanResults.Count > 0)
            {
                var volume = await Task.Run(() => ClusterMappingService.ResolveVolumeForDisk(disk.Index));
                if (volume is { } v)
                {
                    var resolved = new List<SurfaceScanResult>();
                    foreach (var r in SurfaceScanResults)
                    {
                        token.ThrowIfCancellationRequested();
                        r.OwningFile = await ClusterMappingService.ResolveOwningFileAsync(v.DriveLetter, r.StartLba, 512, v.BytesPerCluster);
                        resolved.Add(r);
                    }
                    SurfaceScanResults.Clear();
                    foreach (var r in resolved) SurfaceScanResults.Add(r);
                }
                else
                {
                    SurfaceScanStatusText += " (No assigned drive letter found on this disk - bad-LBA-to-file mapping unavailable.)";
                }
            }
        }
        catch (OperationCanceledException)
        {
            SurfaceScanStatusText = $"Scan cancelled - {SurfaceScanResults.Count} problem(s) found before cancelling.";
        }
        catch (Exception ex)
        {
            SurfaceScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsSurfaceScanning = false;
            _surfaceScanCts?.Dispose();
            _surfaceScanCts = null;
        }
    }

    // ================================================================================
    // #333: file-level read verification.
    // ================================================================================

    private async Task VerifyFilesAsync()
    {
        string root = FileVerificationRoot;
        if (!Directory.Exists(root))
        {
            FileVerificationStatusText = "Path not found.";
            return;
        }

        _fileVerificationCts = new CancellationTokenSource();
        var token = _fileVerificationCts.Token;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        IsVerifyingFiles = true;
        FileVerificationFailures.Clear();
        FileVerificationStatusText = "Verifying (reading every byte of every file under this path)...";
        try
        {
            var (checkedCount, failures) = await Task.Run(() => FileVerificationService.Verify(
                root,
                count =>
                {
                    if (count % 25 == 0)
                        dispatcher?.Invoke(() => FileVerificationStatusText = $"Verifying... {count:N0} file(s) checked so far.");
                },
                token), token);

            foreach (var f in failures) FileVerificationFailures.Add(f);
            FileVerificationStatusText = failures.Count == 0
                ? $"{checkedCount:N0} file(s) verified - every byte read back successfully."
                : $"{checkedCount:N0} file(s) checked - {failures.Count} failed to read.";
        }
        catch (OperationCanceledException)
        {
            FileVerificationStatusText = $"Verification cancelled - {FileVerificationFailures.Count} failure(s) found so far.";
        }
        catch (Exception ex)
        {
            FileVerificationStatusText = $"Verification failed: {ex.Message}";
        }
        finally
        {
            IsVerifyingFiles = false;
            _fileVerificationCts?.Dispose();
            _fileVerificationCts = null;
        }
    }

    // ================================================================================
    // #334: ATA short/extended self-test - structure/UI in place, issuance stubbed (see
    // AtaSelfTestService's remarks).
    // ================================================================================

    private void RunAtaSelfTest(bool extended)
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;
        var (_, message) = AtaSelfTestService.TriggerSelfTest(disk.Index, extended);
        AtaSelfTestStatusText = message;
    }
}
