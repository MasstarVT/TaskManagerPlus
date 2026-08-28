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

/// <summary>One fixed volume's on-demand fragmentation/MFT-health check (#86, extended by round 17
/// #353/#354). Every fixed volume gets a row (not just HDDs, since #353's brief this round: "MFT
/// fragmentation costs metadata I/O on any medium") - IsHdd gates only the file-fragmentation-
/// percent line/warning color, which genuinely isn't meaningful on an SSD; the MFT-health and
/// free-space-fragmentation lines below it show for every row.</summary>
public sealed class FragmentationRow : ObservableObject
{
    public string DriveLetter { get; init; } = string.Empty;
    public bool IsHdd { get; init; }

    private string _statusText = "Not checked";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }

    private bool _isWarning;
    public bool IsWarning { get => _isWarning; set => SetProperty(ref _isWarning, value); }

    // #353: MFT size/record/fragment counts from the same defrag /A /V report, plus the #350
    // geometry facts' MFT-zone bounds cross-referenced in from the matching VolumeFilesystemRow.
    private string _mftHealthText = string.Empty;
    public string MftHealthText { get => _mftHealthText; set => SetProperty(ref _mftHealthText, value); }

    private bool _mftHealthWarning;
    public bool MftHealthWarning { get => _mftHealthWarning; set => SetProperty(ref _mftHealthWarning, value); }

    // #354: free-space percentage (from DriveInfo, not parsed) + largest contiguous free extent
    // (from the same defrag report).
    private string _freeSpaceFragmentationText = string.Empty;
    public string FreeSpaceFragmentationText { get => _freeSpaceFragmentationText; set => SetProperty(ref _freeSpaceFragmentationText, value); }
}

/// <summary>#352: one fixed volume's persisted daily free-space low-water-mark history, chart
/// series, and linear run-out projection - see StorageViewModel.OnPerformanceSampledForFreeSpace.
/// Hidden (ShowChart/ShowProjection false) until enough daily history exists, and the projection
/// specifically stays hidden whenever the trend is flat or rising, per this round's brief ("never
/// showing an absurd date").</summary>
public sealed class FreeSpaceVolumeRow : ObservableObject
{
    public string DriveLetter { get; init; } = string.Empty;

    private readonly ObservableCollection<double> _freeBytesHistory = new();

    public ISeries[] Series { get; private set; } = Array.Empty<ISeries>();

    private bool _showChart;
    public bool ShowChart { get => _showChart; private set => SetProperty(ref _showChart, value); }

    private string _summaryText = "Not sampled yet";
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    private string _projectionText = string.Empty;
    public string ProjectionText { get => _projectionText; private set => SetProperty(ref _projectionText, value); }

    private bool _showProjection;
    public bool ShowProjection { get => _showProjection; private set => SetProperty(ref _showProjection, value); }

    internal void Apply(List<FreeSpaceDailyPoint> history, long currentFreeBytes, long currentTotalBytes, SKColor chartColor)
    {
        SummaryText = currentTotalBytes > 0
            ? $"{Formatting.FormatBytes(currentFreeBytes)} free of {Formatting.FormatBytes(currentTotalBytes)} ({(currentFreeBytes / (double)currentTotalBytes * 100):0.#}%)"
            : Formatting.FormatBytes(currentFreeBytes);

        _freeBytesHistory.Clear();
        foreach (var p in history) _freeBytesHistory.Add(p.FreeBytes);

        if (history.Count >= 3)
        {
            var (glow, core) = StorageViewModel.TrendLineOf(_freeBytesHistory, chartColor, "Free space");
            Series = new ISeries[] { glow, core };
            ShowChart = true;
        }
        else
        {
            Series = Array.Empty<ISeries>();
            ShowChart = false;
        }
        OnPropertyChanged(nameof(Series));

        ProjectionText = ComputeProjection(history);
        ShowProjection = ProjectionText.Length > 0;
    }

    /// <summary>Two-point (first vs. last recorded day) linear extrapolation, same shape
    /// StorageViewModel.ComputeWearProjection already uses for the SMART wear-rate projection -
    /// hidden (empty string) whenever free space is flat or growing, so a volume that's gaining
    /// space never gets shown an "out of space by ..." date.</summary>
    private static string ComputeProjection(List<FreeSpaceDailyPoint> history)
    {
        if (history.Count < 3) return string.Empty;

        var first = history[0];
        var last = history[^1];
        double days = (last.Date - first.Date).TotalDays;
        if (days < 1) return string.Empty;

        double bytesPerDay = (first.FreeBytes - last.FreeBytes) / days; // positive = shrinking
        if (bytesPerDay <= 0) return string.Empty;

        double daysRemaining = last.FreeBytes / bytesPerDay;
        var projected = DateTime.Today.AddDays(daysRemaining);
        return $"At the recent rate of {Formatting.FormatBytes(bytesPerDay)}/day, this volume is projected to run out of free space around {projected:d} (in ~{daysRemaining:0} days) - extrapolation from recent history, not a guarantee.";
    }
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

/// <summary>One row in the round 9 (#39) largest-files/folders scan result. Round 17, #361 adds the
/// on-disk size (distinct from the logical size for sparse/compressed files and cloud-storage
/// placeholders) and a flags column.</summary>
public sealed class LargestItemRow
{
    public string Path { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string SizeOnDiskText { get; init; } = string.Empty;
    public string FlagsText { get; init; } = string.Empty;
}

/// <summary>Round 13, #314: one decoded bit of the NVMe Health Log's Critical Warning byte - a
/// named row with a red badge when set, rather than a raw hex mask. All six are always shown (not
/// just the set ones) once a health log has been read, so the card reads as a checklist.</summary>
public sealed class NvmeWarningRow
{
    public string Label { get; init; } = string.Empty;
    public bool IsSet { get; init; }
}

/// <summary>Round 18, #363: one bucket of a per-disk latency histogram strip (see
/// DiskLatencyHistoryService.HistogramBucketLabels) - same "Label + Percent" shape
/// PerformanceViewModel.TurboHistogramBucket already uses for the CPU tab's turbo histogram.</summary>
public sealed class LatencyHistogramBucket : ObservableObject
{
    public string Label { get; init; } = string.Empty;

    private double _percent;
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
}

/// <summary>
/// Round 18, #362/#363/#365: one physical disk's live bottleneck-diagnostics row - queue length/
/// latency/throughput/utilization mirrored from PerformanceViewModel.PhysicalDisks each tick, plus
/// this disk's rolling-window latency percentiles and histogram (#363). Updated in place, not
/// rebuilt, by StorageViewModel.OnPerformanceSampledForDiskDiagnostics - same "merge, don't clear
/// and rebuild" shape the rest of this file's per-tick handlers use.
/// </summary>
public sealed class PhysicalDiskDiagnosticsRow : ObservableObject
{
    public string DiskName { get; init; } = string.Empty;

    private double _utilizationPercent;
    public double UtilizationPercent { get => _utilizationPercent; set => SetProperty(ref _utilizationPercent, value); }

    private double _activePercent;
    public double ActivePercent { get => _activePercent; set => SetProperty(ref _activePercent, value); }

    private bool _idleTimeAvailable;
    public bool IdleTimeAvailable { get => _idleTimeAvailable; set => SetProperty(ref _idleTimeAvailable, value); }

    private double _readBps;
    public double ReadBps { get => _readBps; set => SetProperty(ref _readBps, value); }

    private double _writeBps;
    public double WriteBps { get => _writeBps; set => SetProperty(ref _writeBps, value); }

    private double _queueLength;
    public double QueueLength { get => _queueLength; set => SetProperty(ref _queueLength, value); }

    private double _readLatencyMs;
    public double ReadLatencyMs { get => _readLatencyMs; set => SetProperty(ref _readLatencyMs, value); }

    private double _writeLatencyMs;
    public double WriteLatencyMs { get => _writeLatencyMs; set => SetProperty(ref _writeLatencyMs, value); }

    // #363
    public ObservableCollection<LatencyHistogramBucket> HistogramBuckets { get; } = new(
        DiskLatencyHistoryService.HistogramBucketLabels.Select(l => new LatencyHistogramBucket { Label = l }));

    private string _percentileSummaryText = "Collecting samples...";
    public string PercentileSummaryText { get => _percentileSummaryText; set => SetProperty(ref _percentileSummaryText, value); }

    private string _percentileWindowText = string.Empty;
    public string PercentileWindowText { get => _percentileWindowText; set => SetProperty(ref _percentileWindowText, value); }
}

/// <summary>Round 18, #364: one sample where a disk's "Avg. Disk sec/Transfer" exceeded the
/// configurable stall threshold, plus a best-effort "what was likely responsible" from whatever
/// ProcessesViewModel's own poller last measured - see StorageViewModel.FindTopDiskProcessText. A
/// partial correlation, not a guarantee: ProcessesViewModel polls on its own independent timer, so
/// the process reading isn't from this exact instant.</summary>
public sealed class DiskStallEvent
{
    public DateTime TimestampLocal { get; init; }
    public string DiskName { get; init; } = string.Empty;
    public double TransferLatencyMs { get; init; }
    public string TopProcessText { get; init; } = string.Empty;
}

/// <summary>Round 18, #369: wraps one immutable MinifilterVolumeInfo with this row's mutable "deep
/// stack" quick-flag classification (recomputed whenever the user changes the threshold) - same
/// wrap-the-immutable-fact shape VolumeFilesystemRow/NtfsBehaviorSettingRow use elsewhere in this
/// file.</summary>
public sealed class MinifilterVolumeRow : ObservableObject
{
    public MinifilterVolumeInfo Info { get; }
    public MinifilterVolumeRow(MinifilterVolumeInfo info) => Info = info;

    public string VolumeName => Info.VolumeName;
    public IReadOnlyList<MinifilterInstanceInfo> Instances => Info.Instances;
    public int InstanceCount => Info.Instances.Count;

    private bool _isDeepStack;
    public bool IsDeepStack { get => _isDeepStack; set => SetProperty(ref _isDeepStack, value); }
}

/// <summary>
/// Round 15, #337/#338/#339/#343/#345: one fixed, lettered volume's combined filesystem card row -
/// the static MSFT_Volume/physical-sector facts (#345, Facts) plus the mutable NTFS-specific
/// dirty/self-healing/corruption-record/boot-check facts (#337/#338/#339/#343), all read together in
/// one background pass at Storage-tab load (or an explicit Refresh click) - see
/// StorageViewModel.LoadVolumeFilesystemFactsAsync. This replaces the tab's earlier letter-only
/// drive list as the anchor the rest of this round's cards attach to.
/// </summary>
public sealed class VolumeFilesystemRow : ObservableObject
{
    public VolumeFilesystemFacts Facts { get; }
    public string DriveLetter => Facts.DriveLetter;

    public VolumeFilesystemRow(VolumeFilesystemFacts facts)
    {
        Facts = facts;
        // Keeps CorruptionCountText in sync without StorageViewModel having to reach into this
        // row's internals from outside - the same "the row owns its own derived-text wiring"
        // shape SmartTriageTile/FragmentationRow already use, just via a collection instead of a
        // single settable property.
        CorruptionRecords.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CorruptionCountText));
    }

    public string AllocationUnitSizeText => Facts.AllocationUnitSizeBytes is { } a ? Formatting.FormatBytes(a) : "Unknown";
    public string PhysicalSectorSizeText => Facts.PhysicalSectorSizeBytes is { } p ? Formatting.FormatBytes(p) : "Unknown";

    // #337
    private bool? _isDirty;
    public bool? IsDirty { get => _isDirty; set { if (SetProperty(ref _isDirty, value)) OnPropertyChanged(nameof(DirtyText)); } }

    public string DirtyText => IsDirty switch
    {
        true => "Dirty - a chkdsk is already queued for next boot",
        false => "Not dirty",
        null => "Unknown",
    };

    // #338
    private bool? _selfHealingEnabled;
    public bool? SelfHealingEnabled { get => _selfHealingEnabled; set { if (SetProperty(ref _selfHealingEnabled, value)) OnPropertyChanged(nameof(SelfHealingText)); } }

    private bool? _selfHealingWarnOnly;
    public bool? SelfHealingWarnOnly { get => _selfHealingWarnOnly; set { if (SetProperty(ref _selfHealingWarnOnly, value)) OnPropertyChanged(nameof(SelfHealingText)); } }

    public string SelfHealingText
    {
        get
        {
            if (!Facts.IsNtfs) return "Unknown (not an NTFS volume)";
            if (SelfHealingWarnOnly == true) return "Warn only (logs/warns about corruptions, doesn't auto-repair)";
            return SelfHealingEnabled switch { true => "Enabled", false => "Disabled", null => "Unknown" };
        }
    }

    private string _selfHealingActionStatusText = string.Empty;
    public string SelfHealingActionStatusText { get => _selfHealingActionStatusText; set => SetProperty(ref _selfHealingActionStatusText, value); }

    private bool _isTogglingSelfHealing;
    public bool IsTogglingSelfHealing { get => _isTogglingSelfHealing; set => SetProperty(ref _isTogglingSelfHealing, value); }

    // #339
    public ObservableCollection<NtfsCorruptionRecord> CorruptionRecords { get; } = new();
    public string CorruptionCountText => CorruptionRecords.Count == 0
        ? "0 logged corruption records"
        : $"{CorruptionRecords.Count} logged corruption record(s)";

    // #343
    private string _chkntfsText = string.Empty;
    public string ChkntfsText { get => _chkntfsText; set => SetProperty(ref _chkntfsText, value); }

    private string _bootExecuteText = string.Empty;
    public string BootExecuteText { get => _bootExecuteText; set => SetProperty(ref _bootExecuteText, value); }

    private bool _isExcludedFromBootCheck;
    public bool IsExcludedFromBootCheck { get => _isExcludedFromBootCheck; set => SetProperty(ref _isExcludedFromBootCheck, value); }

    private string _bootCheckActionStatusText = string.Empty;
    public string BootCheckActionStatusText { get => _bootCheckActionStatusText; set => SetProperty(ref _bootCheckActionStatusText, value); }

    private bool _isBootCheckActionRunning;
    public bool IsBootCheckActionRunning { get => _isBootCheckActionRunning; set => SetProperty(ref _isBootCheckActionRunning, value); }

    private string _ntfsFactsStatusText = "Loading...";
    public string NtfsFactsStatusText { get => _ntfsFactsStatusText; set => SetProperty(ref _ntfsFactsStatusText, value); }

    // ---- Round 16, #346: USN journal status -------------------------------------------------
    private UsnJournalStatus? _usnJournalStatus;
    public UsnJournalStatus? UsnJournalStatus
    {
        get => _usnJournalStatus;
        set
        {
            if (SetProperty(ref _usnJournalStatus, value))
            {
                OnPropertyChanged(nameof(UsnJournalSummaryText));
                OnPropertyChanged(nameof(UsnJournalRangeText));
                OnPropertyChanged(nameof(UsnJournalSizeText));
                OnPropertyChanged(nameof(ShowUsnJournalWrappedWarning));
            }
        }
    }

    public string UsnJournalSummaryText => UsnJournalStatus switch
    {
        null => "Not read",
        { Available: false } s => $"No active journal - {s.UnavailableReason}",
        { Available: true } s => $"Active - journal ID 0x{s.JournalId:X16}",
    };

    public string UsnJournalRangeText => UsnJournalStatus is { Available: true } s
        ? $"First USN 0x{s.FirstUsn:X} · Next USN 0x{s.NextUsn:X} · Lowest valid USN 0x{s.LowestValidUsn:X}"
        : string.Empty;

    public string UsnJournalSizeText => UsnJournalStatus is { Available: true } s
        ? $"Maximum size {(s.MaximumSizeBytes is { } m ? Formatting.FormatBytes(m) : "Unknown")} · Allocation delta {(s.AllocationDeltaBytes is { } a ? Formatting.FormatBytes(a) : "Unknown")}"
        : string.Empty;

    public bool ShowUsnJournalWrappedWarning => UsnJournalStatus is { Available: true, LikelyWrapped: true };

    // ---- Round 16, #348: create/resize/delete controls ---------------------------------------
    private string _usnMaxSizeMbInput = string.Empty;
    public string UsnMaxSizeMbInput { get => _usnMaxSizeMbInput; set => SetProperty(ref _usnMaxSizeMbInput, value); }

    private string _usnAllocationDeltaMbInput = string.Empty;
    public string UsnAllocationDeltaMbInput { get => _usnAllocationDeltaMbInput; set => SetProperty(ref _usnAllocationDeltaMbInput, value); }

    private bool _isUsnJournalActionRunning;
    public bool IsUsnJournalActionRunning { get => _isUsnJournalActionRunning; set => SetProperty(ref _isUsnJournalActionRunning, value); }

    private string _usnJournalActionStatusText = string.Empty;
    public string UsnJournalActionStatusText { get => _usnJournalActionStatusText; set => SetProperty(ref _usnJournalActionStatusText, value); }

    // ---- Round 16, #350: NTFS geometry facts --------------------------------------------------
    private NtfsGeometryFacts? _geometryFacts;
    public NtfsGeometryFacts? GeometryFacts
    {
        get => _geometryFacts;
        set
        {
            if (SetProperty(ref _geometryFacts, value))
            {
                OnPropertyChanged(nameof(GeometryClusterSectorText));
                OnPropertyChanged(nameof(GeometryMftText));
                OnPropertyChanged(nameof(GeometryLogFileText));
            }
        }
    }

    public string GeometryClusterSectorText => GeometryFacts switch
    {
        null => "Not read",
        { Available: false } g => $"Unknown - {g.UnavailableReason}",
        { Available: true } g =>
            $"Cluster {(g.BytesPerCluster is { } c ? Formatting.FormatBytes(c) : "Unknown")} · " +
            $"Logical sector {(g.BytesPerSector is { } s ? Formatting.FormatBytes(s) : "Unknown")} · " +
            $"Physical sector {(g.BytesPerPhysicalSector is { } p ? Formatting.FormatBytes(p) : "Unknown")}",
    };

    public string GeometryMftText => GeometryFacts is { Available: true } g
        ? $"MFT start LCN 0x{g.MftStartLcn:X} · Zone 0x{g.MftZoneStart:X}–0x{g.MftZoneEnd:X} · " +
          $"Valid data length {(g.MftValidDataLengthBytes is { } v ? Formatting.FormatBytes(v) : "Unknown")}"
        : string.Empty;

    public string GeometryLogFileText => GeometryFacts is { Available: true } g
        ? $"$LogFile size: {(g.LogFileSizeBytes is { } l ? Formatting.FormatBytes(l) : "Unknown (not reported by fsutil on this Windows version)")}"
        : string.Empty;
}

/// <summary>Round 16, #351: wraps one immutable NtfsBehaviorSetting fact with this row's mutable
/// set-action status - same "wrap the immutable fact, add mutable UI state" shape VolumeFilesystemRow
/// uses for VolumeFilesystemFacts.</summary>
public sealed class NtfsBehaviorSettingRow : ObservableObject
{
    public NtfsBehaviorSetting Setting { get; }
    public NtfsBehaviorSettingRow(NtfsBehaviorSetting setting) => Setting = setting;

    private bool _isActionRunning;
    public bool IsActionRunning { get => _isActionRunning; set => SetProperty(ref _isActionRunning, value); }

    private string _actionStatusText = string.Empty;
    public string ActionStatusText { get => _actionStatusText; set => SetProperty(ref _actionStatusText, value); }
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

    // #86, widened by round 17 #353/#354: every fixed volume gets a row now (FragmentationRow.IsHdd
    // gates just the file-fragmentation-percent line - MFT health and free-space fragmentation are
    // shown for SSDs too).
    public ObservableCollection<FragmentationRow> FragmentationRows { get; } = new();
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

    // ================================================================================
    // Round 15, #337/#338/#339/#343/#345: per-volume filesystem facts card - the anchor row list
    // the rest of this round's cards/actions attach to. Replaces the tab's earlier letter-only
    // drive list.
    // ================================================================================
    public ObservableCollection<VolumeFilesystemRow> VolumeFilesystemRows { get; } = new();

    private bool _isLoadingVolumeFilesystemFacts;
    public bool IsLoadingVolumeFilesystemFacts { get => _isLoadingVolumeFilesystemFacts; private set => SetProperty(ref _isLoadingVolumeFilesystemFacts, value); }

    public AsyncRelayCommand RefreshVolumeFilesystemFactsCommand { get; }
    public AsyncRelayCommand ToggleSelfHealingCommand { get; }
    public AsyncRelayCommand ScheduleBootCheckCommand { get; }
    public AsyncRelayCommand CancelBootCheckCommand { get; }

    // ================================================================================
    // #340/#341: online chkdsk /scan runner + MSFT_Volume.Repair (Scan/SpotFix/OfflineScanAndFix) -
    // one shared volume picker for both, since they act on the same target.
    // ================================================================================
    public ObservableCollection<string> ChkdskVolumeOptions { get; } = new();

    private string? _selectedChkdskVolume;
    public string? SelectedChkdskVolume
    {
        get => _selectedChkdskVolume;
        set { if (SetProperty(ref _selectedChkdskVolume, value) && value is not null) UpdateChkdskLastScanText(value); }
    }

    private string _chkdskScanLogText = string.Empty;
    public string ChkdskScanLogText { get => _chkdskScanLogText; private set => SetProperty(ref _chkdskScanLogText, value); }

    private bool _isChkdskScanning;
    public bool IsChkdskScanning { get => _isChkdskScanning; private set => SetProperty(ref _isChkdskScanning, value); }

    private string _chkdskScanStatusText = string.Empty;
    public string ChkdskScanStatusText { get => _chkdskScanStatusText; private set => SetProperty(ref _chkdskScanStatusText, value); }

    // #340: persisted "last scanned: <date> - ..." line, per the selected volume.
    private string _chkdskLastScanText = string.Empty;
    public string ChkdskLastScanText { get => _chkdskLastScanText; private set => SetProperty(ref _chkdskLastScanText, value); }

    private CancellationTokenSource? _chkdskScanCts;
    public AsyncRelayCommand RunChkdskScanCommand { get; }
    public RelayCommand CancelChkdskScanCommand { get; }

    private string _volumeRepairStatusText = string.Empty;
    public string VolumeRepairStatusText { get => _volumeRepairStatusText; private set => SetProperty(ref _volumeRepairStatusText, value); }

    private bool _isRunningVolumeRepair;
    public bool IsRunningVolumeRepair { get => _isRunningVolumeRepair; private set => SetProperty(ref _isRunningVolumeRepair, value); }

    public AsyncRelayCommand RunVolumeScanCommand { get; }
    public AsyncRelayCommand RunVolumeSpotFixCommand { get; }
    public AsyncRelayCommand RunVolumeOfflineFixCommand { get; }

    // ================================================================================
    // #342: filesystem check history - this app's own persisted #340/#341 runs merged with
    // Windows' own event-logged chkdsk reports.
    // ================================================================================
    public ObservableCollection<ChkdskHistoryEntry> ChkdskHistory { get; } = new();

    private string _chkdskHistoryStatusText = string.Empty;
    public string ChkdskHistoryStatusText { get => _chkdskHistoryStatusText; private set => SetProperty(ref _chkdskHistoryStatusText, value); }

    private bool _isLoadingChkdskHistory;
    public bool IsLoadingChkdskHistory { get => _isLoadingChkdskHistory; private set => SetProperty(ref _isLoadingChkdskHistory, value); }

    public AsyncRelayCommand RefreshChkdskHistoryCommand { get; }

    // ================================================================================
    // #344: NTFS corruption event feed (System log, provider "Ntfs").
    // ================================================================================
    public ObservableCollection<NtfsCorruptionEvent> NtfsCorruptionEvents { get; } = new();

    private string _ntfsCorruptionEventsStatusText = "Not checked";
    public string NtfsCorruptionEventsStatusText { get => _ntfsCorruptionEventsStatusText; private set => SetProperty(ref _ntfsCorruptionEventsStatusText, value); }

    private bool _isCheckingNtfsCorruptionEvents;
    public bool IsCheckingNtfsCorruptionEvents { get => _isCheckingNtfsCorruptionEvents; private set => SetProperty(ref _isCheckingNtfsCorruptionEvents, value); }

    public AsyncRelayCommand CheckNtfsCorruptionEventsCommand { get; }

    // ================================================================================
    // Round 16, #346/#348/#350: USN journal status + create/resize/delete + NTFS geometry facts -
    // all folded into the existing per-volume VolumeFilesystemRow card above (see
    // LoadRowNtfsDetailsAsync); the two per-row action commands below are shared across every row,
    // same shape as ToggleSelfHealingCommand/ScheduleBootCheckCommand.
    // ================================================================================
    public AsyncRelayCommand CreateOrResizeUsnJournalCommand { get; }
    public AsyncRelayCommand DeleteUsnJournalCommand { get; }

    // ================================================================================
    // #347: USN churn hot spots - "what is writing to this disk", on demand for one selected volume.
    // ================================================================================
    public ObservableCollection<UsnHotSpotRow> UsnHotSpots { get; } = new();

    private string? _selectedUsnHotSpotDrive;
    public string? SelectedUsnHotSpotDrive { get => _selectedUsnHotSpotDrive; set => SetProperty(ref _selectedUsnHotSpotDrive, value); }

    private int _usnHotSpotMinutes = 15;
    public int UsnHotSpotMinutes { get => _usnHotSpotMinutes; set => SetProperty(ref _usnHotSpotMinutes, value); }

    private bool _isFindingUsnHotSpots;
    public bool IsFindingUsnHotSpots { get => _isFindingUsnHotSpots; private set => SetProperty(ref _isFindingUsnHotSpots, value); }

    private string _usnHotSpotStatusText = "Not checked";
    public string UsnHotSpotStatusText { get => _usnHotSpotStatusText; private set => SetProperty(ref _usnHotSpotStatusText, value); }

    public AsyncRelayCommand FindUsnHotSpotsCommand { get; }

    // ================================================================================
    // #349: NTFS metadata activity - per-second deltas of fsutil fsinfo statistics counters,
    // sampled every PerformanceViewModel tick (not throttled, unlike #324's WMI check above - a
    // single small fsutil shell-out is cheap enough for every tick, per this round's brief).
    // ================================================================================
    private string? _selectedNtfsActivityDrive;
    public string? SelectedNtfsActivityDrive
    {
        get => _selectedNtfsActivityDrive;
        set { if (SetProperty(ref _selectedNtfsActivityDrive, value)) { _lastNtfsStats = null; NtfsActivityStatusText = string.Empty; } }
    }

    private NtfsMetadataStatistics? _lastNtfsStats;
    private DateTime _lastNtfsStatsTime;
    private bool _ntfsActivitySampling;

    private double _mftReadsPerSec;
    public double MftReadsPerSec { get => _mftReadsPerSec; private set => SetProperty(ref _mftReadsPerSec, value); }

    private double _mftWritesPerSec;
    public double MftWritesPerSec { get => _mftWritesPerSec; private set => SetProperty(ref _mftWritesPerSec, value); }

    private double _metadataReadsPerSec;
    public double MetadataReadsPerSec { get => _metadataReadsPerSec; private set => SetProperty(ref _metadataReadsPerSec, value); }

    private double _metadataWritesPerSec;
    public double MetadataWritesPerSec { get => _metadataWritesPerSec; private set => SetProperty(ref _metadataWritesPerSec, value); }

    private double _logFileWritesPerSec;
    public double LogFileWritesPerSec { get => _logFileWritesPerSec; private set => SetProperty(ref _logFileWritesPerSec, value); }

    private string _ntfsActivityStatusText = string.Empty;
    public string NtfsActivityStatusText { get => _ntfsActivityStatusText; private set => SetProperty(ref _ntfsActivityStatusText, value); }

    // ================================================================================
    // #351: NTFS behaviour settings audit - system-wide, read once at Storage-tab load or refresh.
    // ================================================================================
    public ObservableCollection<NtfsBehaviorSettingRow> NtfsBehaviorSettings { get; } = new();

    private bool _isLoadingNtfsBehaviorSettings;
    public bool IsLoadingNtfsBehaviorSettings { get => _isLoadingNtfsBehaviorSettings; private set => SetProperty(ref _isLoadingNtfsBehaviorSettings, value); }

    private string _ntfsBehaviorStatusText = "Not read";
    public string NtfsBehaviorStatusText { get => _ntfsBehaviorStatusText; private set => SetProperty(ref _ntfsBehaviorStatusText, value); }

    public AsyncRelayCommand RefreshNtfsBehaviorSettingsCommand { get; }
    public AsyncRelayCommand EnableNtfsBehaviorSettingCommand { get; }
    public AsyncRelayCommand DisableNtfsBehaviorSettingCommand { get; }

    // ================================================================================
    // Round 17, #352/#355: free-space history + days-until-full projection, and per-volume
    // low-free-space alert thresholds - both sampled on the same Performance.Sampled tick (see
    // OnPerformanceSampledForFreeSpace), the same "piggyback rather than a new timer" shape #324/
    // #349 already use.
    // ================================================================================
    public Axis[] FreeSpaceHiddenXAxes { get; }
    public ObservableCollection<FreeSpaceVolumeRow> FreeSpaceVolumes { get; } = new();

    private readonly AlertThresholds _alertThresholds = AlertThresholdsService.Load();
    private readonly Dictionary<string, bool> _freeSpacePercentAlerted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _freeSpaceAbsoluteAlerted = new(StringComparer.OrdinalIgnoreCase);
    private bool _freeSpaceSampling;

    public bool FreeSpacePercentAlertEnabled
    {
        get => _alertThresholds.FreeSpacePercentEnabled;
        set { _alertThresholds.FreeSpacePercentEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); }
    }
    public double FreeSpacePercentAlertThreshold
    {
        get => _alertThresholds.FreeSpacePercentThreshold;
        set { _alertThresholds.FreeSpacePercentThreshold = value; OnPropertyChanged(); PersistAlertThresholds(); }
    }
    public bool FreeSpaceAbsoluteAlertEnabled
    {
        get => _alertThresholds.FreeSpaceAbsoluteEnabled;
        set { _alertThresholds.FreeSpaceAbsoluteEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); }
    }
    public double FreeSpaceAbsoluteAlertThresholdGb
    {
        get => _alertThresholds.FreeSpaceAbsoluteGbThreshold;
        set { _alertThresholds.FreeSpaceAbsoluteGbThreshold = value; OnPropertyChanged(); PersistAlertThresholds(); }
    }

    /// <summary>Re-reads alerts.json and writes only this VM's own fields back onto it, rather than
    /// blindly overwriting the whole file with this VM's (possibly stale) in-memory copy - avoids
    /// clobbering a concurrent edit to the Cpu/Memory/Temp fields SummaryViewModel's own threshold
    /// card owns (see SummaryViewModel.PersistAlertThresholds for its half of this same
    /// merge-on-save fix).</summary>
    private void PersistAlertThresholds()
    {
        var onDisk = AlertThresholdsService.Load();
        onDisk.FreeSpacePercentEnabled = _alertThresholds.FreeSpacePercentEnabled;
        onDisk.FreeSpacePercentThreshold = _alertThresholds.FreeSpacePercentThreshold;
        onDisk.FreeSpaceAbsoluteEnabled = _alertThresholds.FreeSpaceAbsoluteEnabled;
        onDisk.FreeSpaceAbsoluteGbThreshold = _alertThresholds.FreeSpaceAbsoluteGbThreshold;
        // #364
        onDisk.DiskStallDetectionEnabled = _alertThresholds.DiskStallDetectionEnabled;
        onDisk.DiskStallThresholdMs = _alertThresholds.DiskStallThresholdMs;
        AlertThresholdsService.Save(onDisk);
    }

    // ================================================================================
    // Round 17, #356/#357/#358/#360: "reclaimable space" card - component store analysis/cleanup,
    // a reclaimable-space inventory + Storage Sense policy, hibernation file sizing, and the search
    // indexer's footprint.
    // ================================================================================

    // #356
    private ComponentStoreAnalysis? _componentStoreAnalysis;
    public ComponentStoreAnalysis? ComponentStoreAnalysis { get => _componentStoreAnalysis; private set => SetProperty(ref _componentStoreAnalysis, value); }

    private bool _isAnalyzingComponentStore;
    public bool IsAnalyzingComponentStore { get => _isAnalyzingComponentStore; private set => SetProperty(ref _isAnalyzingComponentStore, value); }

    private string _componentStoreActionStatusText = string.Empty;
    public string ComponentStoreActionStatusText { get => _componentStoreActionStatusText; private set => SetProperty(ref _componentStoreActionStatusText, value); }

    private bool _isCleaningComponentStore;
    public bool IsCleaningComponentStore { get => _isCleaningComponentStore; private set => SetProperty(ref _isCleaningComponentStore, value); }

    public AsyncRelayCommand AnalyzeComponentStoreCommand { get; }
    public AsyncRelayCommand StartComponentCleanupCommand { get; }

    // #357
    public ObservableCollection<ReclaimableSpaceItem> ReclaimableItems { get; } = new();

    private string _reclaimableItemsStatusText = "Loading...";
    public string ReclaimableItemsStatusText { get => _reclaimableItemsStatusText; private set => SetProperty(ref _reclaimableItemsStatusText, value); }

    private StorageSensePolicyInfo? _storageSensePolicy;
    public StorageSensePolicyInfo? StorageSensePolicy { get => _storageSensePolicy; private set => SetProperty(ref _storageSensePolicy, value); }

    // #358
    private HibernationInfo? _hibernationInfo;
    public HibernationInfo? HibernationInfo { get => _hibernationInfo; private set => SetProperty(ref _hibernationInfo, value); }

    private string _hibernationActionStatusText = string.Empty;
    public string HibernationActionStatusText { get => _hibernationActionStatusText; private set => SetProperty(ref _hibernationActionStatusText, value); }

    private bool _isHibernationActionRunning;
    public bool IsHibernationActionRunning { get => _isHibernationActionRunning; private set => SetProperty(ref _isHibernationActionRunning, value); }

    private string _hibernateSizePercentInput = "75";
    public string HibernateSizePercentInput { get => _hibernateSizePercentInput; set => SetProperty(ref _hibernateSizePercentInput, value); }

    public AsyncRelayCommand DisableHibernationCommand { get; }
    public AsyncRelayCommand EnableHibernationCommand { get; }
    public AsyncRelayCommand SetHibernateSizeCommand { get; }

    // #360
    private IndexerFootprintInfo? _indexerFootprint;
    public IndexerFootprintInfo? IndexerFootprint { get => _indexerFootprint; private set => SetProperty(ref _indexerFootprint, value); }

    public AsyncRelayCommand RefreshReclaimableSpaceCommand { get; }

    // ================================================================================
    // Round 17, #359: page file placement, sizing, and peak usage.
    // ================================================================================
    public ObservableCollection<PageFileDetailInfo> PageFiles { get; } = new();

    private string _pageFileStatusText = "Loading...";
    public string PageFileStatusText { get => _pageFileStatusText; private set => SetProperty(ref _pageFileStatusText, value); }

    // ================================================================================
    // Round 18, #362/#363/#364/#365/#366: per-physical-disk bottleneck diagnostics - one row per
    // PhysicalDisk instance (not just the "_Total" aggregate the top of this tab already shows),
    // each with its own rolling-window latency-percentile histogram, plus the disk-stall timeline.
    // All fed off the same Performance.Sampled tick as #324/#349/#352 above - see
    // OnPerformanceSampledForDiskDiagnostics.
    // ================================================================================
    public ObservableCollection<PhysicalDiskDiagnosticsRow> PhysicalDiskRows { get; } = new();

    private readonly DiskLatencyHistoryService _diskLatencyHistory = new();
    private readonly Dictionary<string, bool> _diskStallAlerted = new(StringComparer.OrdinalIgnoreCase);

    // #364: persisted alongside the other alert-style thresholds (see PersistAlertThresholds) -
    // on by default (unlike the opt-in Cpu/Memory/Temp/FreeSpace toggles above), since a stall the
    // user isn't actively watching for is exactly the case this timeline exists to catch.
    public ObservableCollection<DiskStallEvent> DiskStalls { get; } = new();

    public bool DiskStallDetectionEnabled
    {
        get => _alertThresholds.DiskStallDetectionEnabled;
        set { _alertThresholds.DiskStallDetectionEnabled = value; OnPropertyChanged(); PersistAlertThresholds(); }
    }
    public double DiskStallThresholdMs
    {
        get => _alertThresholds.DiskStallThresholdMs;
        set { _alertThresholds.DiskStallThresholdMs = Math.Max(1, value); OnPropertyChanged(); PersistAlertThresholds(); }
    }

    // ================================================================================
    // Round 18, #367: StorPort driver-level latency tracing - a time-boxed, on-demand real-time
    // ETW capture, never automatic (see StorPortTraceService's remarks on why this chunk ships the
    // capture path without a live session wired up).
    // ================================================================================
    public ObservableCollection<StorPortLatencyEvent> StorPortEvents { get; } = new();

    private int _storPortCaptureDurationSeconds = 15;
    public int StorPortCaptureDurationSeconds { get => _storPortCaptureDurationSeconds; set => SetProperty(ref _storPortCaptureDurationSeconds, Math.Clamp(value, 5, 120)); }

    private double _storPortThresholdMs = 20;
    public double StorPortThresholdMs { get => _storPortThresholdMs; set => SetProperty(ref _storPortThresholdMs, Math.Max(0, value)); }

    private bool _isCapturingStorPort;
    public bool IsCapturingStorPort { get => _isCapturingStorPort; private set => SetProperty(ref _isCapturingStorPort, value); }

    private string _storPortStatusText = "Not captured";
    public string StorPortStatusText { get => _storPortStatusText; private set => SetProperty(ref _storPortStatusText, value); }

    public AsyncRelayCommand StartStorPortCaptureCommand { get; }

    // ================================================================================
    // Round 18, #368: per-file I/O attribution - same time-boxed, on-demand ETW shape as #367
    // above (see FileIoAttributionService's remarks).
    // ================================================================================
    public ObservableCollection<FileIoAttributionEntry> TopIoFiles { get; } = new();
    public ObservableCollection<FileIoAttributionEntry> TopIoProcesses { get; } = new();

    private int _ioCaptureDurationSeconds = 15;
    public int IoCaptureDurationSeconds { get => _ioCaptureDurationSeconds; set => SetProperty(ref _ioCaptureDurationSeconds, Math.Clamp(value, 10, 60)); }

    private bool _isCapturingIoAttribution;
    public bool IsCapturingIoAttribution { get => _isCapturingIoAttribution; private set => SetProperty(ref _isCapturingIoAttribution, value); }

    private string _ioAttributionStatusText = "Not captured";
    public string IoAttributionStatusText { get => _ioAttributionStatusText; private set => SetProperty(ref _ioAttributionStatusText, value); }

    public AsyncRelayCommand StartIoAttributionCaptureCommand { get; }

    // ================================================================================
    // Round 18, #369: minifilter (filesystem filter driver) stack audit - "quick flag, not a
    // verdict" (see MinifilterAuditService's remarks). On-demand only, via fltmc shell-outs.
    // ================================================================================
    public ObservableCollection<MinifilterDriverInfo> MinifilterDrivers { get; } = new();
    public ObservableCollection<MinifilterVolumeRow> MinifilterVolumes { get; } = new();

    private bool _isCheckingMinifilters;
    public bool IsCheckingMinifilters { get => _isCheckingMinifilters; private set => SetProperty(ref _isCheckingMinifilters, value); }

    private string _minifilterStatusText = "Not checked";
    public string MinifilterStatusText { get => _minifilterStatusText; private set => SetProperty(ref _minifilterStatusText, value); }

    private int _minifilterDeepStackThreshold = 8;
    public int MinifilterDeepStackThreshold
    {
        get => _minifilterDeepStackThreshold;
        set { if (SetProperty(ref _minifilterDeepStackThreshold, Math.Max(1, value))) ReclassifyMinifilterVolumes(); }
    }

    public AsyncRelayCommand CheckMinifiltersCommand { get; }

    private readonly ProcessesViewModel _processes;

    public StorageViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals, ProcessesViewModel processes)
    {
        _processes = processes;
        Performance = performance;
        EnergyThermals = energyThermals;

        var view = new ListCollectionView((IList)energyThermals.Temperatures)
        {
            Filter = o => o is SensorReading r && r.HardwareType == HardwareType.Storage,
        };
        DriveTemperatures = view;

        // #352: hidden X axis for the free-space history chart - a separate instance from
        // Performance.HiddenXAxes (used elsewhere in this file for the fixed-length CPU/RAM/Disk
        // history buffers) since this chart's series length is however many daily points are on
        // disk, not a fixed HistoryLength.
        FreeSpaceHiddenXAxes = new[] { new Axis { IsVisible = false, ShowSeparatorLines = false } };

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

        // Round 15, #337/#338/#339/#343/#345
        RefreshVolumeFilesystemFactsCommand = new AsyncRelayCommand(LoadVolumeFilesystemFactsAsync, () => !IsLoadingVolumeFilesystemFacts);
        ToggleSelfHealingCommand = new AsyncRelayCommand(param => ToggleSelfHealingAsync(param as VolumeFilesystemRow));
        ScheduleBootCheckCommand = new AsyncRelayCommand(param => ScheduleBootCheckForRowAsync(param as VolumeFilesystemRow));
        CancelBootCheckCommand = new AsyncRelayCommand(param => CancelBootCheckForRowAsync(param as VolumeFilesystemRow));

        // #340/#341
        RunChkdskScanCommand = new AsyncRelayCommand(RunChkdskScanAsync, () => !IsChkdskScanning && SelectedChkdskVolume is not null);
        CancelChkdskScanCommand = new RelayCommand(() => _chkdskScanCts?.Cancel(), () => IsChkdskScanning);
        RunVolumeScanCommand = new AsyncRelayCommand(() => RunVolumeRepairAsync(VolumeRepairMode.Scan), () => !IsRunningVolumeRepair && SelectedChkdskVolume is not null);
        RunVolumeSpotFixCommand = new AsyncRelayCommand(() => RunVolumeRepairAsync(VolumeRepairMode.SpotFix), () => !IsRunningVolumeRepair && SelectedChkdskVolume is not null);
        RunVolumeOfflineFixCommand = new AsyncRelayCommand(() => RunVolumeRepairAsync(VolumeRepairMode.OfflineScanAndFix), () => !IsRunningVolumeRepair && SelectedChkdskVolume is not null);

        // #342
        RefreshChkdskHistoryCommand = new AsyncRelayCommand(RefreshChkdskHistoryAsync, () => !IsLoadingChkdskHistory);

        // #344
        CheckNtfsCorruptionEventsCommand = new AsyncRelayCommand(CheckNtfsCorruptionEventsAsync, () => !IsCheckingNtfsCorruptionEvents);

        // Round 16, #346/#348/#350: per-row USN journal + geometry actions.
        CreateOrResizeUsnJournalCommand = new AsyncRelayCommand(param => CreateOrResizeUsnJournalAsync(param as VolumeFilesystemRow));
        DeleteUsnJournalCommand = new AsyncRelayCommand(param => DeleteUsnJournalAsync(param as VolumeFilesystemRow));

        // #347
        FindUsnHotSpotsCommand = new AsyncRelayCommand(FindUsnHotSpotsAsync, () => !IsFindingUsnHotSpots && SelectedUsnHotSpotDrive is not null);

        // #351
        RefreshNtfsBehaviorSettingsCommand = new AsyncRelayCommand(RefreshNtfsBehaviorSettingsAsync, () => !IsLoadingNtfsBehaviorSettings);
        EnableNtfsBehaviorSettingCommand = new AsyncRelayCommand(param => SetNtfsBehaviorSettingAsync(param as NtfsBehaviorSettingRow, 0));
        DisableNtfsBehaviorSettingCommand = new AsyncRelayCommand(param => SetNtfsBehaviorSettingAsync(param as NtfsBehaviorSettingRow, 1));

        // Round 17, #356
        AnalyzeComponentStoreCommand = new AsyncRelayCommand(AnalyzeComponentStoreAsync, () => !IsAnalyzingComponentStore);
        StartComponentCleanupCommand = new AsyncRelayCommand(StartComponentCleanupAsync, () => !IsCleaningComponentStore && ComponentStoreAnalysis is { Available: true });

        // #358
        DisableHibernationCommand = new AsyncRelayCommand(DisableHibernationAsync, () => !IsHibernationActionRunning);
        EnableHibernationCommand = new AsyncRelayCommand(EnableHibernationAsync, () => !IsHibernationActionRunning);
        SetHibernateSizeCommand = new AsyncRelayCommand(SetHibernateSizeAsync, () => !IsHibernationActionRunning);

        // #356/#357/#358/#360: one shared refresh for the whole "reclaimable space" card.
        RefreshReclaimableSpaceCommand = new AsyncRelayCommand(RefreshReclaimableSpaceAsync);

        // Round 18, #367/#368: time-boxed, on-demand ETW captures - never automatic.
        StartStorPortCaptureCommand = new AsyncRelayCommand(RunStorPortCaptureAsync, () => !IsCapturingStorPort);
        StartIoAttributionCaptureCommand = new AsyncRelayCommand(RunIoAttributionCaptureAsync, () => !IsCapturingIoAttribution);

        // Round 18, #369
        CheckMinifiltersCommand = new AsyncRelayCommand(CheckMinifiltersAsync, () => !IsCheckingMinifilters);

        // #324: subscribe to the shared sampler's tick rather than owning a new heavy timer -
        // see PerformanceViewModel.Sampled's remarks.
        Performance.Sampled += OnPerformanceSampled;

        // #349: same event, second independent handler - sampled every tick (no throttle), per
        // this round's brief.
        Performance.Sampled += OnPerformanceSampledForNtfsActivity;

        // #352/#355: third independent handler on the same shared tick - free-space history
        // sampling and low-free-space alert evaluation are cheap (DriveInfo reads, no shell-out),
        // so this runs unthrottled too, same as #349's handler above.
        Performance.Sampled += OnPerformanceSampledForFreeSpace;

        // Round 18, #362/#363/#364/#365/#366: fourth independent handler - pure in-memory work
        // (mirroring PerformanceViewModel.PhysicalDisks, updating the rolling latency-percentile
        // window, running the stall detector), so unlike the Task.Run-wrapped handlers above this
        // one runs synchronously, per this round's brief that #362-366 are cheap enough to
        // piggyback directly onto the tick.
        Performance.Sampled += OnPerformanceSampledForDiskDiagnostics;

        _ = Task.Run(() =>
        {
            var pools = StorageSpacesService.List();
            var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            var fixedDriveLetters = fixedDrives.Select(d => d.Name.TrimEnd('\\')).ToList(); // "C:" - colon kept, see FragmentationRow's remarks
            var disks = SystemSpecsService.ListDisksForSmart();

            // #328: cheap base facts (predicted failure, driver health) for every disk, computed
            // once at startup - refined per-disk once that disk's SMART/NVMe data is actually read
            // (see ApplySmartRawResult/ApplyNvmeHealth below).
            var failureFlags = SystemSpecsService.ReadDiskFailureFlags().ToDictionary(f => f.Index);
            var poolsForVerdict = pools;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var p in pools) StoragePools.Add(p);
                // #86, widened by #353/#354: a row per fixed volume, not just HDDs - see
                // FragmentationRow's remarks.
                foreach (var letter in fixedDriveLetters)
                {
                    bool isHdd = DiskFragmentationService.GetMediaType(letter.TrimEnd(':')) == "HDD";
                    FragmentationRows.Add(new FragmentationRow { DriveLetter = letter, IsHdd = isHdd });
                }
                foreach (var (index, model) in disks) SmartDiskOptions.Add(new SmartDiskOption { Index = index, Model = model });
                foreach (var d in fixedDrives) ThroughputDriveOptions.Add(d.Name.TrimEnd('\\'));
                SelectedThroughputDrive = ThroughputDriveOptions.FirstOrDefault();

                // #340/#341: same fixed-drive set as the throughput picker above, but bare (no
                // trailing colon) - unlike ThroughputDriveOptions above, this matches
                // VolumeFilesystemRow.DriveLetter's bare-letter convention (from MSFT_Volume's
                // single-char DriveLetter property), since ChkdskService's process args append
                // their own colon.
                foreach (var d in fixedDrives) ChkdskVolumeOptions.Add(d.Name.TrimEnd('\\', ':'));
                SelectedChkdskVolume = ChkdskVolumeOptions.FirstOrDefault();

                // #347/#349: same bare-letter fixed-drive set - reused rather than duplicated.
                SelectedUsnHotSpotDrive = ChkdskVolumeOptions.FirstOrDefault();
                SelectedNtfsActivityDrive = ChkdskVolumeOptions.FirstOrDefault();

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

        // Round 15, #345: the per-volume filesystem facts card - its own async pass (not folded
        // into the Task.Run above) since it does its own further per-row async fsutil/chkntfs
        // shell-outs after the initial WMI read, per LoadVolumeFilesystemFactsAsync's remarks.
        _ = LoadVolumeFilesystemFactsAsync();

        // #351: system-wide, independent of any per-volume row above.
        _ = RefreshNtfsBehaviorSettingsAsync();

        // #359: page file placement/sizing/peak-usage - a one-time-at-tab-load read, its own pass
        // since it cross-references the #328 DriveHealthVerdicts list populated by the Task.Run
        // above (best-effort - that list may still be filling in on a slow WMI query, in which
        // case the same-disk-as-failing-drive flag just comes back false for this load).
        _ = LoadPageFilesAsync();

        // #356/#357/#358/#360: "reclaimable space" card - everything here except the component-
        // store analysis (explicitly on-demand only, see AnalyzeComponentStoreAsync) is a one-time
        // read at tab load, same tier as the page-file load above.
        _ = RefreshReclaimableSpaceAsync();
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
        row.MftHealthText = string.Empty;
        row.FreeSpaceFragmentationText = string.Empty;
        try
        {
            var result = await DiskFragmentationService.Analyze(row.DriveLetter);
            row.StatusText = row.IsHdd
                ? result.Message
                : "Not applicable to SSD media (see MFT health / free-space fragmentation below).";
            row.IsWarning = row.IsHdd && result.Success && result.FragmentedPercent is { } p && p >= 10;

            // #353: MFT health - shown for HDD and SSD alike, since MFT fragmentation costs
            // metadata I/O on any medium.
            ApplyMftHealth(row, result);

            // #354: free-space fragmentation - largest contiguous extent from the same defrag
            // report; free-space percentage computed directly from DriveInfo rather than parsed.
            ApplyFreeSpaceFragmentation(row, result);
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

    /// <summary>#353: MFT size/record/fragment counts from the defrag report, plus whether the MFT
    /// has outgrown its reserved zone - cross-referenced from the #350 geometry facts already
    /// loaded onto the matching VolumeFilesystemRow (same drive letter, bare vs. colon-suffixed
    /// conventions reconciled here). "Quick flag, not a verdict" - both signals are pattern-matches
    /// on otherwise-ambiguous figures, not a confirmed problem.</summary>
    private void ApplyMftHealth(FragmentationRow row, FragmentationAnalysis result)
    {
        if (!result.Success)
        {
            row.MftHealthText = "Unknown (analysis failed).";
            row.MftHealthWarning = false;
            return;
        }

        var parts = new List<string>();
        if (result.MftSizeBytes is { } size) parts.Add($"size {Formatting.FormatBytes(size)}");
        if (result.MftRecordCount is { } records) parts.Add($"{records:N0} record(s)");
        if (result.MftFragmentCount is { } frags) parts.Add($"{frags:N0} fragment(s)");

        bool highFragmentCount = result.MftFragmentCount is { } f && f >= 100;

        bool? outgrownZone = null;
        string bareLetter = row.DriveLetter.TrimEnd(':');
        var geoRow = VolumeFilesystemRows.FirstOrDefault(r => string.Equals(r.DriveLetter, bareLetter, StringComparison.OrdinalIgnoreCase));
        if (geoRow?.GeometryFacts is { Available: true } geo &&
            geo.MftZoneStart.HasValue && geo.MftZoneEnd.HasValue && geo.BytesPerCluster.HasValue && geo.MftValidDataLengthBytes.HasValue &&
            geo.MftZoneEnd.Value > geo.MftZoneStart.Value)
        {
            ulong zoneBytes = (geo.MftZoneEnd.Value - geo.MftZoneStart.Value) * geo.BytesPerCluster.Value;
            outgrownZone = zoneBytes > 0 && geo.MftValidDataLengthBytes.Value > zoneBytes;
            if (outgrownZone == true) parts.Add("has outgrown its reserved MFT zone");
        }

        row.MftHealthWarning = highFragmentCount || outgrownZone == true;
        row.MftHealthText = parts.Count == 0
            ? "Not reported by this Windows build's defrag output."
            : string.Join(" · ", parts) + (row.MftHealthWarning ? " - quick flag, not a verdict." : ".");
    }

    /// <summary>#354: free-space percentage straight from DriveInfo (reliable, no parsing needed)
    /// paired with the largest contiguous free extent parsed from the same defrag report (no other
    /// source for that figure) - together these flag a volume that looks roomy overall but can't
    /// actually place a large file contiguously.</summary>
    private void ApplyFreeSpaceFragmentation(FragmentationRow row, FragmentationAnalysis result)
    {
        var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
            d.Name.TrimEnd('\\').Equals(row.DriveLetter, StringComparison.OrdinalIgnoreCase) && d.IsReady);

        string freePercentText = drive is not null && drive.TotalSize > 0
            ? $"{(drive.AvailableFreeSpace / (double)drive.TotalSize * 100):0.#}% free"
            : "Unknown free %";

        string extentText = result.Success && result.LargestFreeExtentBytes is { } extent
            ? $"largest contiguous extent {Formatting.FormatBytes(extent)}"
            : "largest contiguous extent not reported by this Windows build's defrag output";

        row.FreeSpaceFragmentationText = $"{freePercentText}, {extentText}.";
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
                // #361: size-on-disk vs. logical size, plus reparse-point/cloud-placeholder flags -
                // a folder/file that's mostly on-demand cloud content stops looking like the "real"
                // culprit once its on-disk figure is shown next to its logical size.
                var flags = new List<string>();
                if (item.IsCloudPlaceholder) flags.Add("cloud placeholder");
                if (item.IsReparsePoint) flags.Add("reparse point");

                LargestItems.Add(new LargestItemRow
                {
                    Path = item.Path,
                    SizeText = Formatting.FormatBytes(item.SizeBytes),
                    Kind = item.IsDirectory ? "Folder" : "File",
                    SizeOnDiskText = item.SizeOnDiskBytes is { } onDisk ? Formatting.FormatBytes(onDisk) : "Unknown",
                    FlagsText = string.Join(", ", flags),
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
    /// poll) doesn't belong on that class. Internal (not private) so FreeSpaceVolumeRow's own
    /// #352 chart can reuse it too, rather than a third copy of the same styling.</summary>
    internal static (LineSeries<double> Glow, LineSeries<double> Core) TrendLineOf(ObservableCollection<double> values, SKColor color, string name)
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

    // ================================================================================
    // Round 15, #337/#338/#339/#343/#345: per-volume filesystem facts card.
    // ================================================================================

    /// <summary>One MSFT_Volume query (#345) followed by, per row, the NTFS-specific fsutil/
    /// chkntfs facts (#337/#338/#339/#343) - all read once here (constructor + explicit Refresh
    /// click), never on a poll tick, per this round's brief. The per-row facts are their own pass
    /// (not folded into the constructor's other Task.Run) since each row does several of its own
    /// async shell-outs after the base list is already on screen, rather than blocking the whole
    /// card behind every volume's slowest fsutil call.</summary>
    private async Task LoadVolumeFilesystemFactsAsync()
    {
        if (IsLoadingVolumeFilesystemFacts) return;
        IsLoadingVolumeFilesystemFacts = true;
        try
        {
            var facts = await Task.Run(() => NtfsFilesystemService.ListVolumes());
            VolumeFilesystemRows.Clear();
            var rows = facts.Select(f => new VolumeFilesystemRow(f)).ToList();
            foreach (var row in rows) VolumeFilesystemRows.Add(row);

            var tasks = rows.Select(LoadRowNtfsDetailsAsync).ToArray();
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Card-level failure (e.g. the MSFT_Volume query itself threw) - individual row
            // failures are already caught and shown per-row in LoadRowNtfsDetailsAsync below.
        }
        finally
        {
            IsLoadingVolumeFilesystemFacts = false;
        }
    }

    /// <summary>Dirty bit (#337) and boot-check status (#343) apply to any volume; self-healing
    /// state (#338) and the corruption record log (#339) are NTFS-only concepts, so those two are
    /// skipped (left at their "Unknown (not an NTFS volume)"/empty defaults) for a non-NTFS row
    /// rather than shelling out to fsutil commands that don't apply to it.</summary>
    private async Task LoadRowNtfsDetailsAsync(VolumeFilesystemRow row)
    {
        row.NtfsFactsStatusText = "Loading...";
        try
        {
            var dirtyTask = NtfsFilesystemService.QueryDirtyAsync(row.DriveLetter);
            var bootCheckTask = NtfsFilesystemService.QueryBootCheckStatusAsync(row.DriveLetter);

            if (row.Facts.IsNtfs)
            {
                var repairTask = NtfsFilesystemService.QueryRepairStateAsync(row.DriveLetter);
                var corruptionTask = NtfsFilesystemService.EnumerateCorruptionRecordsAsync(row.DriveLetter);
                // Round 16, #346/#350: same per-row pass - USN journal status and NTFS geometry
                // facts are both NTFS-only concepts, same tier as repair state/corruption log above.
                var usnTask = UsnJournalService.QueryStatusAsync(row.DriveLetter);
                var geometryTask = NtfsFilesystemService.ReadGeometryFactsAsync(row.DriveLetter);
                await Task.WhenAll(dirtyTask, bootCheckTask, repairTask, corruptionTask, usnTask, geometryTask);

                var (enabled, warnOnly, _) = repairTask.Result;
                row.SelfHealingEnabled = enabled;
                row.SelfHealingWarnOnly = warnOnly;

                row.CorruptionRecords.Clear();
                foreach (var rec in corruptionTask.Result) row.CorruptionRecords.Add(rec);

                row.UsnJournalStatus = usnTask.Result;
                row.GeometryFacts = geometryTask.Result;
            }
            else
            {
                await Task.WhenAll(dirtyTask, bootCheckTask);
            }

            row.IsDirty = dirtyTask.Result;
            var (chkntfsText, bootExecuteText, isExcluded) = bootCheckTask.Result;
            row.ChkntfsText = chkntfsText;
            row.BootExecuteText = bootExecuteText;
            row.IsExcludedFromBootCheck = isExcluded;

            row.NtfsFactsStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            row.NtfsFactsStatusText = $"Failed: {ex.Message}";
        }
    }

    /// <summary>#338: toggles between Enabled and Disabled - a row currently Unknown, WarnOnly, or
    /// Disabled is treated as "not currently Enabled" and this attempts to enable it; only a
    /// currently-Enabled row disables. Re-queries afterwards so the displayed state reflects what
    /// fsutil actually set, not just an optimistic flip.</summary>
    private async Task ToggleSelfHealingAsync(VolumeFilesystemRow? row)
    {
        if (row is null || row.IsTogglingSelfHealing) return;
        bool enableNext = row.SelfHealingEnabled != true;

        row.IsTogglingSelfHealing = true;
        row.SelfHealingActionStatusText = enableNext ? "Enabling..." : "Disabling...";
        try
        {
            var (success, message) = await NtfsFilesystemService.SetRepairStateAsync(row.DriveLetter, enableNext);
            row.SelfHealingActionStatusText = message;
            if (success)
            {
                var (enabled, warnOnly, _) = await NtfsFilesystemService.QueryRepairStateAsync(row.DriveLetter);
                row.SelfHealingEnabled = enabled;
                row.SelfHealingWarnOnly = warnOnly;
            }
        }
        catch (Exception ex)
        {
            row.SelfHealingActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsTogglingSelfHealing = false;
        }
    }

    /// <summary>#343: schedules a boot-time `chkdsk /f /r` - confirmed first since a /r pass can
    /// run for hours and requires a reboot to even start (see
    /// NtfsFilesystemService.ScheduleBootCheckAsync's remarks for the safety timeout backing this).
    /// </summary>
    private async Task ScheduleBootCheckForRowAsync(VolumeFilesystemRow? row)
    {
        if (row is null || row.IsBootCheckActionRunning) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Schedule a full chkdsk /f /r check of {row.DriveLetter}: for the next restart?\n\n" +
            "This requires a reboot to take effect, and the /r (bad-sector) pass can take several " +
            "hours on a large volume - the computer won't be usable for normal work until it finishes.",
            "Schedule boot-time check",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        row.IsBootCheckActionRunning = true;
        row.BootCheckActionStatusText = "Scheduling...";
        try
        {
            var (success, message) = await NtfsFilesystemService.ScheduleBootCheckAsync(row.DriveLetter);
            row.BootCheckActionStatusText = message;
            if (success) await RefreshRowBootCheckStatusAsync(row);
        }
        catch (Exception ex)
        {
            row.BootCheckActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsBootCheckActionRunning = false;
        }
    }

    /// <summary>#343: `chkntfs /x` - excludes the volume from the default boot-time check.</summary>
    private async Task CancelBootCheckForRowAsync(VolumeFilesystemRow? row)
    {
        if (row is null || row.IsBootCheckActionRunning) return;

        row.IsBootCheckActionRunning = true;
        row.BootCheckActionStatusText = "Cancelling...";
        try
        {
            var (success, message) = await NtfsFilesystemService.CancelBootCheckAsync(row.DriveLetter);
            row.BootCheckActionStatusText = message;
            if (success) await RefreshRowBootCheckStatusAsync(row);
        }
        catch (Exception ex)
        {
            row.BootCheckActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsBootCheckActionRunning = false;
        }
    }

    private static async Task RefreshRowBootCheckStatusAsync(VolumeFilesystemRow row)
    {
        var (chkntfsText, bootExecuteText, isExcluded) = await NtfsFilesystemService.QueryBootCheckStatusAsync(row.DriveLetter);
        row.ChkntfsText = chkntfsText;
        row.BootExecuteText = bootExecuteText;
        row.IsExcludedFromBootCheck = isExcluded;
    }

    // ================================================================================
    // #340: online chkdsk /scan runner - streamed line-by-line into a scrollable log, cancellable.
    // ================================================================================

    private async Task RunChkdskScanAsync()
    {
        string? drive = SelectedChkdskVolume;
        if (drive is null || IsChkdskScanning) return;

        _chkdskScanCts = new CancellationTokenSource();
        var token = _chkdskScanCts.Token;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        IsChkdskScanning = true;
        ChkdskScanLogText = string.Empty;
        ChkdskScanStatusText = $"Scanning {drive}: (chkdsk /scan - online, no dismount, no reboot)...";
        var logBuilder = new System.Text.StringBuilder();
        try
        {
            var (problemsFound, verdict) = await ChkdskService.RunOnlineScanAsync(drive, line =>
            {
                dispatcher?.Invoke(() =>
                {
                    logBuilder.AppendLine(line);
                    ChkdskScanLogText = logBuilder.ToString();
                });
            }, token);

            ChkdskScanStatusText = verdict;

            ChkdskService.AppendScanRecord(new ChkdskScanRecord
            {
                DriveLetter = drive,
                Timestamp = DateTime.Now,
                Source = "Online scan (/scan)",
                ProblemsFound = problemsFound,
                Summary = verdict,
            });
            UpdateChkdskLastScanText(drive);
        }
        catch (OperationCanceledException)
        {
            ChkdskScanStatusText = $"Scan cancelled - {logBuilder.ToString().Split('\n').Length} line(s) logged before cancelling.";
        }
        catch (Exception ex)
        {
            ChkdskScanStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsChkdskScanning = false;
            _chkdskScanCts?.Dispose();
            _chkdskScanCts = null;
        }
    }

    private void UpdateChkdskLastScanText(string driveLetter)
    {
        var last = ChkdskService.LastScanFor(driveLetter);
        ChkdskLastScanText = last is null
            ? $"{driveLetter}: never scanned by this app."
            : $"Last scanned: {last.Timestamp:g} - {(last.ProblemsFound ? "problems found" : "no problems found")} ({last.Source}).";
    }

    // ================================================================================
    // #341: MSFT_Volume.Repair - Scan/SpotFix/OfflineScanAndFix. SpotFix and OfflineScanAndFix are
    // confirmed first (Offline dismounts the volume), matching the Yes/No MessageBox.Show pattern
    // ProcessesViewModel.EndSelected already uses for a different destructive action - no separate
    // confirmation mechanism invented for this.
    // ================================================================================

    private async Task RunVolumeRepairAsync(VolumeRepairMode mode)
    {
        string? drive = SelectedChkdskVolume;
        if (drive is null || IsRunningVolumeRepair) return;

        if (mode != VolumeRepairMode.Scan)
        {
            string question = mode == VolumeRepairMode.OfflineScanAndFix
                ? $"Run an OFFLINE scan and fix on {drive}:?\n\nThe volume will be dismounted for the duration of the repair - anything with open files or handles on it will be interrupted."
                : $"Run a spot-fix repair on {drive}:?\n\nThis repairs specific known-corrupt areas online, without a full offline scan.";
            var confirm = System.Windows.MessageBox.Show(question, "Repair volume", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        IsRunningVolumeRepair = true;
        VolumeRepairStatusText = $"Running ({mode})...";
        try
        {
            var (success, _, codeText, extraDetail) = await ChkdskService.RunVolumeRepairAsync(drive, mode);
            VolumeRepairStatusText = extraDetail.Length > 0 ? $"{codeText} {extraDetail}" : codeText;

            ChkdskService.AppendScanRecord(new ChkdskScanRecord
            {
                DriveLetter = drive,
                Timestamp = DateTime.Now,
                Source = $"WMI {mode}",
                ProblemsFound = !success,
                Summary = VolumeRepairStatusText,
            });
            UpdateChkdskLastScanText(drive);
        }
        catch (Exception ex)
        {
            VolumeRepairStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunningVolumeRepair = false;
        }
    }

    // ================================================================================
    // #342: filesystem check history.
    // ================================================================================

    private async Task RefreshChkdskHistoryAsync()
    {
        IsLoadingChkdskHistory = true;
        ChkdskHistoryStatusText = "Loading...";
        ChkdskHistory.Clear();
        try
        {
            var entries = await Task.Run(() => ChkdskService.ReadCombinedHistory());
            foreach (var e in entries) ChkdskHistory.Add(e);
            ChkdskHistoryStatusText = entries.Count == 0
                ? "No chkdsk runs found - neither this app's own history nor Windows' own event-logged reports."
                : $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} (most recent first - this app's own runs and Windows' own event-logged reports combined; see the Source column).";
        }
        catch (Exception ex)
        {
            ChkdskHistoryStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoadingChkdskHistory = false;
        }
    }

    // ================================================================================
    // #344: NTFS corruption event feed.
    // ================================================================================

    private async Task CheckNtfsCorruptionEventsAsync()
    {
        IsCheckingNtfsCorruptionEvents = true;
        NtfsCorruptionEventsStatusText = "Checking the System log for Ntfs corruption/resource-exhaustion events...";
        NtfsCorruptionEvents.Clear();
        try
        {
            var events = await Task.Run(() => NtfsCorruptionEventService.ReadEvents());
            foreach (var e in events) NtfsCorruptionEvents.Add(e);
            NtfsCorruptionEventsStatusText = events.Count == 0
                ? "No Ntfs corruption/resource-exhaustion events found in the last 60 days."
                : $"{events.Count} event(s) in the last 60 days, grouped by volume.";
        }
        catch (Exception ex)
        {
            NtfsCorruptionEventsStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCheckingNtfsCorruptionEvents = false;
        }
    }

    // ================================================================================
    // Round 16, #348: USN journal create/resize/delete - confirmed first (same Yes/No
    // MessageBox.Show pattern #341/#343 already use), since deleting the journal forces every
    // dependent tool to rescan.
    // ================================================================================

    private static long? ParseMegabytesInput(string input)
    {
        string trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        return long.TryParse(trimmed, out long mb) && mb > 0 ? mb * 1024 * 1024 : null;
    }

    private async Task CreateOrResizeUsnJournalAsync(VolumeFilesystemRow? row)
    {
        if (row is null || row.IsUsnJournalActionRunning) return;

        bool maxSizeValid = row.UsnMaxSizeMbInput.Trim().Length == 0 || ParseMegabytesInput(row.UsnMaxSizeMbInput) is not null;
        bool deltaValid = row.UsnAllocationDeltaMbInput.Trim().Length == 0 || ParseMegabytesInput(row.UsnAllocationDeltaMbInput) is not null;
        if (!maxSizeValid || !deltaValid)
        {
            row.UsnJournalActionStatusText = "Enter a whole number of MB (or leave a field blank to let fsutil pick its own default).";
            return;
        }
        long? maxSizeBytes = ParseMegabytesInput(row.UsnMaxSizeMbInput);
        long? allocationDeltaBytes = ParseMegabytesInput(row.UsnAllocationDeltaMbInput);

        string question = row.UsnJournalStatus?.Available == true
            ? $"Resize the USN journal on {row.DriveLetter}:?\n\nA journal already exists - this updates its maximum size/allocation delta without deleting its recorded history."
            : $"Create a USN journal on {row.DriveLetter}:?\n\nNo active journal was found on this volume.";
        var confirm = System.Windows.MessageBox.Show(question, "Create/resize USN journal", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        row.IsUsnJournalActionRunning = true;
        row.UsnJournalActionStatusText = "Working...";
        try
        {
            var (success, message) = await UsnJournalService.CreateOrResizeJournalAsync(row.DriveLetter, maxSizeBytes, allocationDeltaBytes);
            row.UsnJournalActionStatusText = message;
            if (success) row.UsnJournalStatus = await UsnJournalService.QueryStatusAsync(row.DriveLetter);
        }
        catch (Exception ex)
        {
            row.UsnJournalActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsUsnJournalActionRunning = false;
        }
    }

    private async Task DeleteUsnJournalAsync(VolumeFilesystemRow? row)
    {
        if (row is null || row.IsUsnJournalActionRunning) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Delete the USN journal on {row.DriveLetter}:?\n\n" +
            "Every tool that tracks its own last-seen USN against this journal (backup software, search indexing, file replication) will be forced into a full rescan the next time it runs, since its recorded position no longer means anything.",
            "Delete USN journal",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        row.IsUsnJournalActionRunning = true;
        row.UsnJournalActionStatusText = "Deleting...";
        try
        {
            var (success, message) = await UsnJournalService.DeleteJournalAsync(row.DriveLetter);
            row.UsnJournalActionStatusText = message;
            if (success) row.UsnJournalStatus = await UsnJournalService.QueryStatusAsync(row.DriveLetter);
        }
        catch (Exception ex)
        {
            row.UsnJournalActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsUsnJournalActionRunning = false;
        }
    }

    // ================================================================================
    // #347: USN churn hot spots.
    // ================================================================================

    private async Task FindUsnHotSpotsAsync()
    {
        string? drive = SelectedUsnHotSpotDrive;
        if (drive is null || IsFindingUsnHotSpots) return;

        IsFindingUsnHotSpots = true;
        UsnHotSpotStatusText = $"Reading the USN journal on {drive}: for the last {UsnHotSpotMinutes} minute(s) (this can take up to a minute on a busy volume)...";
        UsnHotSpots.Clear();
        try
        {
            var result = await UsnJournalService.FindHotSpotsAsync(drive, UsnHotSpotMinutes);
            foreach (var row in result.Rows) UsnHotSpots.Add(row);
            UsnHotSpotStatusText = result.StatusText;
        }
        catch (Exception ex)
        {
            UsnHotSpotStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsFindingUsnHotSpots = false;
        }
    }

    // ================================================================================
    // #349: NTFS metadata activity - per-second deltas, sampled every PerformanceViewModel tick.
    // ================================================================================

    /// <summary>Fired once per PerformanceViewModel sample tick, same event #324's handler
    /// subscribes to - unlike that handler, this one isn't throttled to every Nth tick: a single
    /// `fsutil fsinfo statistics` shell-out is cheap enough to run on every tick, per this round's
    /// brief. A re-entrancy guard still skips a tick if the previous read hasn't finished (e.g. the
    /// shell-out stalls), so ticks never stack up.</summary>
    private void OnPerformanceSampledForNtfsActivity()
    {
        if (_ntfsActivitySampling) return;
        string? drive = SelectedNtfsActivityDrive;
        if (drive is null) return;
        _ntfsActivitySampling = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var stats = await NtfsFilesystemService.ReadMetadataStatisticsAsync(drive);
                var now = DateTime.UtcNow;
                System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyNtfsActivitySample(stats, now));
            }
            finally
            {
                _ntfsActivitySampling = false;
            }
        });
    }

    private void ApplyNtfsActivitySample(NtfsMetadataStatistics stats, DateTime now)
    {
        if (!stats.Available)
        {
            NtfsActivityStatusText = $"Unavailable: {stats.UnavailableReason}";
            _lastNtfsStats = null;
            MftReadsPerSec = MftWritesPerSec = MetadataReadsPerSec = MetadataWritesPerSec = LogFileWritesPerSec = 0;
            return;
        }

        if (_lastNtfsStats is not null && _lastNtfsStatsTime != default)
        {
            double elapsed = (now - _lastNtfsStatsTime).TotalSeconds;
            if (elapsed > 0.05) // guards against a near-zero interval producing a huge spurious rate
            {
                MftReadsPerSec = RatePerSec(_lastNtfsStats.MftReads, stats.MftReads, elapsed);
                MftWritesPerSec = RatePerSec(_lastNtfsStats.MftWrites, stats.MftWrites, elapsed);
                MetadataReadsPerSec = RatePerSec(_lastNtfsStats.MetaDataReads, stats.MetaDataReads, elapsed);
                MetadataWritesPerSec = RatePerSec(_lastNtfsStats.MetaDataWrites, stats.MetaDataWrites, elapsed);
                LogFileWritesPerSec = RatePerSec(_lastNtfsStats.LogFileWrites, stats.LogFileWrites, elapsed);
                NtfsActivityStatusText = string.Empty;
            }
        }
        else
        {
            NtfsActivityStatusText = "First sample taken - rates will show from the next tick.";
        }

        _lastNtfsStats = stats;
        _lastNtfsStatsTime = now;
    }

    private static double RatePerSec(long previous, long current, double elapsedSeconds)
    {
        long delta = current - previous;
        // A negative delta means the underlying counter reset (these are session-scoped, and can
        // reset across a remount or a momentary read failure) - treated as "no data for this tick"
        // rather than shown as a negative rate.
        return delta < 0 ? 0 : delta / elapsedSeconds;
    }

    // ================================================================================
    // #351: NTFS behaviour settings audit.
    // ================================================================================

    private async Task RefreshNtfsBehaviorSettingsAsync()
    {
        if (IsLoadingNtfsBehaviorSettings) return;
        IsLoadingNtfsBehaviorSettings = true;
        NtfsBehaviorStatusText = "Reading...";
        try
        {
            var settings = await NtfsFilesystemService.QueryBehaviorSettingsAsync();
            NtfsBehaviorSettings.Clear();
            foreach (var s in settings) NtfsBehaviorSettings.Add(new NtfsBehaviorSettingRow(s));
            NtfsBehaviorStatusText = $"{settings.Count} setting(s) read.";
        }
        catch (Exception ex)
        {
            NtfsBehaviorStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoadingNtfsBehaviorSettings = false;
        }
    }

    /// <summary>Two explicit buttons (Enable=0/Disable=1) rather than one state-flipping toggle -
    /// disable8dot3 in particular has two other documented values (2 = per-volume default, 3 =
    /// disabled except system volume) a single "flip the current value" toggle couldn't map back to
    /// a sensible binary direction from, so this always sets one of the two simple, unambiguous
    /// values instead of guessing a direction from whatever the current value is.</summary>
    private async Task SetNtfsBehaviorSettingAsync(NtfsBehaviorSettingRow? row, int value)
    {
        if (row is null || row.IsActionRunning) return;
        string action = value == 0 ? "Enable" : "Disable";

        var confirm = System.Windows.MessageBox.Show(
            $"{action} '{row.Setting.Label}' (fsutil behavior set {row.Setting.Key} {value})?\n\n" +
            "This is a system-wide NTFS setting (every volume, not just this app) and typically requires a reboot to take effect.",
            "Change NTFS behaviour setting",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        row.IsActionRunning = true;
        row.ActionStatusText = value == 0 ? "Enabling..." : "Disabling...";
        try
        {
            var (success, message) = await NtfsFilesystemService.SetBehaviorAsync(row.Setting.Key, value);
            row.ActionStatusText = message;
            if (success) await RefreshNtfsBehaviorSettingsAsync();
        }
        catch (Exception ex)
        {
            row.ActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsActionRunning = false;
        }
    }

    // ================================================================================
    // Round 17, #352/#355: free-space history + days-until-full projection, and low-free-space
    // alert thresholds - both evaluated here, on the same tick.
    // ================================================================================

    private static readonly SKColor FreeSpaceChartColor = new(0x4F, 0x9C, 0xE0);

    private void OnPerformanceSampledForFreeSpace()
    {
        if (_freeSpaceSampling) return;
        _freeSpaceSampling = true;

        _ = Task.Run(() =>
        {
            try
            {
                var samples = new List<(string Letter, long Free, long Total)>();
                foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    try { samples.Add((d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize)); }
                    catch { /* transient read failure - skip this drive for this tick */ }
                }
                System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyFreeSpaceSample(samples));
            }
            finally
            {
                _freeSpaceSampling = false;
            }
        });
    }

    private void ApplyFreeSpaceSample(List<(string Letter, long Free, long Total)> samples)
    {
        foreach (var (letter, free, total) in samples)
        {
            var row = FreeSpaceVolumes.FirstOrDefault(r => r.DriveLetter == letter);
            if (row is null)
            {
                row = new FreeSpaceVolumeRow { DriveLetter = letter };
                FreeSpaceVolumes.Add(row);
            }

            var history = FreeSpaceHistoryService.RecordSample(letter, free, total);
            row.Apply(history, free, total, FreeSpaceChartColor);

            // #355: edge-triggered per volume, same "one toast per crossing, not one per tick"
            // shape SummaryViewModel.CheckThresholdAlerts already uses for Cpu/Memory/Temp.
            double percentFree = total > 0 ? free / (double)total * 100.0 : 100.0;
            double freeGb = free / 1_000_000_000.0;

            CheckFreeSpaceAlert(_freeSpacePercentAlerted, letter, FreeSpacePercentAlertEnabled, percentFree <= FreeSpacePercentAlertThreshold,
                $"{letter} free space is {percentFree:0.#}% (threshold {FreeSpacePercentAlertThreshold:0}%)");
            CheckFreeSpaceAlert(_freeSpaceAbsoluteAlerted, letter, FreeSpaceAbsoluteAlertEnabled, freeGb <= FreeSpaceAbsoluteAlertThresholdGb,
                $"{letter} free space is {Formatting.FormatBytes(free)} (threshold {FreeSpaceAbsoluteAlertThresholdGb:0} GB)");
        }
    }

    private static void CheckFreeSpaceAlert(Dictionary<string, bool> alerted, string letter, bool enabled, bool breached, string message)
    {
        if (!enabled) { alerted[letter] = false; return; }

        bool wasAlerted = alerted.TryGetValue(letter, out var a) && a;
        if (breached && !wasAlerted)
        {
            alerted[letter] = true;
            ToastService.Show("Low free space", message, isCritical: true);
        }
        else if (!breached)
        {
            alerted[letter] = false;
        }
    }

    // ================================================================================
    // Round 18, #362/#363/#364/#365/#366: per-physical-disk diagnostics - mirrors
    // PerformanceViewModel.PhysicalDisks into PhysicalDiskRows, feeds the rolling latency-
    // percentile window (#363), and runs the stall detector (#364). All pure in-memory work (no
    // WMI/shell-out), so unlike the Task.Run-wrapped handlers above this one runs synchronously.
    // ================================================================================

    private void OnPerformanceSampledForDiskDiagnostics()
    {
        foreach (var disk in Performance.PhysicalDisks)
        {
            var row = PhysicalDiskRows.FirstOrDefault(r => r.DiskName == disk.InstanceName);
            if (row is null)
            {
                row = new PhysicalDiskDiagnosticsRow { DiskName = disk.InstanceName };
                PhysicalDiskRows.Add(row);
            }

            row.ActivePercent = disk.ActivePercent;
            row.UtilizationPercent = disk.UtilizationPercent;
            row.IdleTimeAvailable = disk.IdleTimeAvailable;
            row.ReadBps = disk.ReadBytesPerSec;
            row.WriteBps = disk.WriteBytesPerSec;
            row.QueueLength = disk.QueueLength;
            row.ReadLatencyMs = disk.ReadLatencyMs;
            row.WriteLatencyMs = disk.WriteLatencyMs;

            // #363
            _diskLatencyHistory.Record(disk.InstanceName, disk.ReadLatencyMs, disk.WriteLatencyMs, disk.TransferLatencyMs);
            ApplyLatencyPercentiles(row, _diskLatencyHistory.GetPercentiles(disk.InstanceName));

            // #364
            CheckDiskStall(disk);
        }

        // Defensive only - the PhysicalDisk instance set is fixed at HardwareMonitorService
        // construction time and shouldn't change while the app runs (see
        // PerformanceViewModel.SyncPhysicalDisks' remarks).
        for (int i = PhysicalDiskRows.Count - 1; i >= 0; i--)
        {
            if (!Performance.PhysicalDisks.Any(d => d.InstanceName == PhysicalDiskRows[i].DiskName))
                PhysicalDiskRows.RemoveAt(i);
        }
    }

    private static void ApplyLatencyPercentiles(PhysicalDiskDiagnosticsRow row, DiskLatencyPercentiles? p)
    {
        if (p is null || p.SampleCount < 3)
        {
            row.PercentileSummaryText = "Collecting samples...";
            row.PercentileWindowText = string.Empty;
            return;
        }

        row.PercentileSummaryText =
            $"Read — p50 {p.ReadP50Ms:0.0} ms · p95 {p.ReadP95Ms:0.0} ms · p99 {p.ReadP99Ms:0.0} ms · max {p.ReadMaxMs:0.0} ms" +
            $"   |   Write — p50 {p.WriteP50Ms:0.0} ms · p95 {p.WriteP95Ms:0.0} ms · p99 {p.WriteP99Ms:0.0} ms · max {p.WriteMaxMs:0.0} ms";
        row.PercentileWindowText =
            $"{p.SampleCount} samples over the last {(DateTime.UtcNow - p.WindowStartUtc).TotalMinutes:0.0} min " +
            $"(rolling {DiskLatencyHistoryService.DefaultWindow.TotalMinutes:0}-min window)";

        for (int i = 0; i < row.HistogramBuckets.Count && i < p.HistogramBucketPercents.Length; i++)
            row.HistogramBuckets[i].Percent = Math.Round(p.HistogramBucketPercents[i], 1);
    }

    /// <summary>#364: edge-triggered per disk, same "one toast per crossing" shape
    /// CheckFreeSpaceAlert above uses - every breaching sample is still logged to DiskStalls
    /// (bounded to the most recent 500), just not re-toasted every tick while it stays breached.</summary>
    private void CheckDiskStall(PhysicalDiskUsage disk)
    {
        bool breached = DiskStallDetectionEnabled && disk.TransferLatencyMs >= DiskStallThresholdMs;
        bool wasAlerted = _diskStallAlerted.TryGetValue(disk.InstanceName, out var a) && a;

        if (!breached)
        {
            _diskStallAlerted[disk.InstanceName] = false;
            return;
        }

        DiskStalls.Add(new DiskStallEvent
        {
            TimestampLocal = DateTime.Now,
            DiskName = disk.InstanceName,
            TransferLatencyMs = disk.TransferLatencyMs,
            TopProcessText = FindTopDiskProcessText(),
        });
        while (DiskStalls.Count > 500) DiskStalls.RemoveAt(0);

        if (!wasAlerted)
        {
            _diskStallAlerted[disk.InstanceName] = true;
            ToastService.Show("Disk stall", $"{disk.InstanceName} averaged {disk.TransferLatencyMs:0} ms per transfer (threshold {DiskStallThresholdMs:0} ms).", isCritical: true);
        }
    }

    /// <summary>#364: best-effort "what was using the disk when it stalled" - reuses
    /// ProcessesViewModel's already-polled per-process DiskBytesPerSec rather than standing up a
    /// new sampler. ProcessesViewModel polls on its own independent timer (see CLAUDE.md's
    /// architecture notes), so this is whatever it last measured, not a value from this exact
    /// tick - a partial correlation, not a guarantee, and labeled as such.</summary>
    private string FindTopDiskProcessText()
    {
        var top = _processes.Processes.OrderByDescending(p => p.DiskBytesPerSec).FirstOrDefault();
        if (top is null || top.DiskBytesPerSec <= 0)
            return "No dominant disk consumer in the last process sample (partial correlation only - process I/O and this disk sample aren't from the exact same instant).";
        return $"{top.Name} (PID {top.Pid}) — {Formatting.FormatByteRate(top.DiskBytesPerSec)}";
    }

    // ================================================================================
    // Round 18, #367/#368: time-boxed, on-demand ETW captures - StorPort driver-level latency and
    // per-file I/O attribution. See StorPortTraceService/FileIoAttributionService's remarks for why
    // this chunk ships these as labeled partials rather than a live ETW session.
    // ================================================================================

    private async Task RunStorPortCaptureAsync()
    {
        IsCapturingStorPort = true;
        StorPortStatusText = $"Capturing for {StorPortCaptureDurationSeconds}s...";
        try
        {
            var result = await StorPortTraceService.RunAsync(StorPortCaptureDurationSeconds, StorPortThresholdMs, CancellationToken.None);
            StorPortEvents.Clear();
            foreach (var e in result.Events) StorPortEvents.Add(e);
            StorPortStatusText = result.StatusText;
        }
        catch (Exception ex)
        {
            StorPortStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCapturingStorPort = false;
        }
    }

    private async Task RunIoAttributionCaptureAsync()
    {
        IsCapturingIoAttribution = true;
        IoAttributionStatusText = $"Capturing for {IoCaptureDurationSeconds}s...";
        try
        {
            var result = await FileIoAttributionService.RunAsync(IoCaptureDurationSeconds, CancellationToken.None);
            TopIoFiles.Clear();
            foreach (var f in result.TopFiles) TopIoFiles.Add(f);
            TopIoProcesses.Clear();
            foreach (var p in result.TopProcesses) TopIoProcesses.Add(p);
            IoAttributionStatusText = result.StatusText;
        }
        catch (Exception ex)
        {
            IoAttributionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCapturingIoAttribution = false;
        }
    }

    // ================================================================================
    // Round 18, #369: minifilter (filesystem filter driver) stack audit - "quick flag, not a
    // verdict" (see MinifilterAuditService's remarks).
    // ================================================================================

    private async Task CheckMinifiltersAsync()
    {
        IsCheckingMinifilters = true;
        MinifilterStatusText = "Running fltmc filters / fltmc instances...";
        try
        {
            var result = await MinifilterAuditService.RunAsync();
            MinifilterDrivers.Clear();
            MinifilterVolumes.Clear();

            if (!result.Available)
            {
                MinifilterStatusText = $"Failed: {result.UnavailableReason}";
                return;
            }

            foreach (var f in result.Filters) MinifilterDrivers.Add(f);
            foreach (var v in result.Volumes) MinifilterVolumes.Add(new MinifilterVolumeRow(v));
            ReclassifyMinifilterVolumes();

            MinifilterStatusText = $"{result.Filters.Count} filter driver(s) reported, attached across {result.Volumes.Count} volume(s). Quick flag only - a deep stack is a plausible latency contributor, not proof of one.";
        }
        catch (Exception ex)
        {
            MinifilterStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCheckingMinifilters = false;
        }
    }

    private void ReclassifyMinifilterVolumes()
    {
        foreach (var v in MinifilterVolumes) v.IsDeepStack = v.InstanceCount >= MinifilterDeepStackThreshold;
    }

    // ================================================================================
    // Round 17, #356: component store (WinSxS) analysis + cleanup - on-demand only, the analyze
    // pass itself walks the whole store and can take a while.
    // ================================================================================

    private async Task AnalyzeComponentStoreAsync()
    {
        IsAnalyzingComponentStore = true;
        ComponentStoreActionStatusText = "Analyzing the component store (this walks the whole WinSxS folder and can take a minute or two)...";
        try
        {
            var result = await ReclaimableSpaceService.AnalyzeComponentStoreAsync();
            ComponentStoreAnalysis = result;
            ComponentStoreActionStatusText = result.Available ? string.Empty : $"Failed: {result.UnavailableReason}";
        }
        catch (Exception ex)
        {
            ComponentStoreActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzingComponentStore = false;
        }
    }

    private async Task StartComponentCleanupAsync()
    {
        if (ComponentStoreAnalysis is not { Available: true }) return;

        var confirm = System.Windows.MessageBox.Show(
            "Run /StartComponentCleanup on the Windows component store?\n\n" +
            "This permanently removes superseded component versions dism has already determined are safe to drop (older servicing generations, disabled-feature payloads past their uninstall window). It can take several minutes.",
            "Clean up component store",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsCleaningComponentStore = true;
        ComponentStoreActionStatusText = "Cleaning up (this can take several minutes)...";
        try
        {
            var (_, message) = await ReclaimableSpaceService.StartComponentCleanupAsync();
            ComponentStoreActionStatusText = message;
        }
        catch (Exception ex)
        {
            ComponentStoreActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsCleaningComponentStore = false;
        }
    }

    // ================================================================================
    // Round 17, #357/#358/#360: reclaimable-space inventory + Storage Sense policy, hibernation
    // sizing, and the search indexer's footprint - all refreshed together, since they're all
    // one-time-at-tab-load reads shown in the same card.
    // ================================================================================

    private async Task RefreshReclaimableSpaceAsync()
    {
        ReclaimableItemsStatusText = "Scanning reclaimable-space locations (this walks each folder's full contents)...";
        try
        {
            var items = await Task.Run(() => ReclaimableSpaceService.InventoryReclaimableSpace());
            ReclaimableItems.Clear();
            foreach (var i in items) ReclaimableItems.Add(i);
            long knownTotal = items.Where(i => i.SizeBytes.HasValue).Sum(i => i.SizeBytes!.Value);
            ReclaimableItemsStatusText = $"{Formatting.FormatBytes(knownTotal)} across {items.Count(i => i.SizeBytes is > 0)} location(s) with something to reclaim.";
        }
        catch (Exception ex)
        {
            ReclaimableItemsStatusText = $"Failed: {ex.Message}";
        }

        try { StorageSensePolicy = await Task.Run(() => ReclaimableSpaceService.ReadStorageSensePolicy()); }
        catch { StorageSensePolicy = new StorageSensePolicyInfo { Available = false }; }

        try { HibernationInfo = await ReclaimableSpaceService.ReadHibernationInfoAsync(); }
        catch (Exception ex) { HibernationInfo = new HibernationInfo { Available = false, UnavailableReason = ex.Message }; }

        try { IndexerFootprint = await Task.Run(() => ReclaimableSpaceService.ReadIndexerFootprint()); }
        catch { IndexerFootprint = new IndexerFootprintInfo(); }
    }

    // ---- #358: hibernation actions -----------------------------------------------------------

    private async Task DisableHibernationAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            "Disable hibernation?\n\n" +
            "This also disables Fast Startup, since Fast Startup is implemented on top of hibernation - shutdown/boot will take the normal (non-hybrid-resume) path afterwards.",
            "Disable hibernation",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsHibernationActionRunning = true;
        HibernationActionStatusText = "Working...";
        try
        {
            var (_, message) = await ReclaimableSpaceService.DisableHibernationAsync();
            HibernationActionStatusText = message;
            HibernationInfo = await ReclaimableSpaceService.ReadHibernationInfoAsync();
        }
        catch (Exception ex)
        {
            HibernationActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsHibernationActionRunning = false;
        }
    }

    private async Task EnableHibernationAsync()
    {
        IsHibernationActionRunning = true;
        HibernationActionStatusText = "Working...";
        try
        {
            var (_, message) = await ReclaimableSpaceService.EnableHibernationAsync();
            HibernationActionStatusText = message;
            HibernationInfo = await ReclaimableSpaceService.ReadHibernationInfoAsync();
        }
        catch (Exception ex)
        {
            HibernationActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsHibernationActionRunning = false;
        }
    }

    private async Task SetHibernateSizeAsync()
    {
        if (!int.TryParse(HibernateSizePercentInput.Trim(), out int percent) || percent <= 0)
        {
            HibernationActionStatusText = "Enter a whole-number percentage of installed RAM (e.g. 75).";
            return;
        }

        IsHibernationActionRunning = true;
        HibernationActionStatusText = "Working (this also turns hibernation on if it was off)...";
        try
        {
            var (_, message) = await ReclaimableSpaceService.SetHibernateFileSizeAsync(percent);
            HibernationActionStatusText = message;
            HibernationInfo = await ReclaimableSpaceService.ReadHibernationInfoAsync();
        }
        catch (Exception ex)
        {
            HibernationActionStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsHibernationActionRunning = false;
        }
    }

    // ================================================================================
    // Round 17, #359: page file placement, sizing, and peak usage.
    // ================================================================================

    private async Task LoadPageFilesAsync()
    {
        PageFileStatusText = "Loading...";
        try
        {
            var details = await Task.Run(() => SystemSpecsService.ReadPageFileDetails());

            // Cross-reference #1: an SSD exists elsewhere on this system while this page file
            // sits on an HDD.
            bool fasterSsdExists = false;
            try
            {
                fasterSsdExists = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => d.Name.TrimEnd('\\', ':'))
                    .Any(letter => DiskFragmentationService.GetMediaType(letter) == "SSD");
            }
            catch { /* leave false - flag just won't fire */ }

            foreach (var detail in details)
            {
                detail.FasterDriveElsewhereFlag = detail.MediaType == "HDD" && fasterSsdExists;

                // Cross-reference #2: this page file's physical disk also hosts a drive this app's
                // own #328 verdict flagged Replace. Best-effort - if the disk-index resolution
                // fails, the flag just stays false rather than guessed.
                try
                {
                    int? diskIndex = ClusterMappingService.ResolveDiskIndexForVolume(detail.DriveLetter);
                    if (diskIndex is { } idx)
                        detail.SameDiskAsFailingDriveFlag = DriveHealthVerdicts.Any(v => v.Index == idx && v.Level == DriveHealthLevel.Replace);
                }
                catch { /* leave false */ }
            }

            PageFiles.Clear();
            foreach (var d in details) PageFiles.Add(d);
            PageFileStatusText = details.Count == 0
                ? "No page files configured, or Win32_PageFileUsage is unavailable on this system."
                : $"{details.Count} page file(s).";
        }
        catch (Exception ex)
        {
            PageFileStatusText = $"Failed: {ex.Message}";
        }
    }
}
