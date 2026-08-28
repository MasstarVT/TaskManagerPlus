namespace TaskManagerPlus.Models;

/// <summary>
/// Data shapes for suggestions.md #278-283 (Page-fault storms and memory-driven hitching) - the
/// hard-fault rate separated from harmless soft faults (#278), per-process/per-file hard-fault
/// attribution in both an always-available approximate mode and an optional ETW deep mode (#279),
/// standby-list depletion (#280), page-file placement/latency (#281), and memory-compression
/// pressure (#282). See PageFaultService, HardFaultEtwService, StandbyListService,
/// PageFileLatencyService and MemoryCompressionService respectively for how these are populated.
///
/// #283 (working-set trim detector) needs no new model here - it lives directly on
/// ProcessRow.LastTrimmedText, since the app already polls per-process working set every tick and
/// this is pure detection logic layered over data already sampled (see
/// ProcessesViewModel.MergeInto).
/// </summary>

/// <summary>#278: system-wide hard-fault rate - Memory\Pages Input/sec (pages actually read from
/// disk to resolve a fault) and Memory\Page Reads/sec (the underlying disk-read rate behind those
/// page-ins - can read lower than Pages Input/sec, since one disk read can satisfy more than one
/// page). Deliberately distinct from Memory\Page Faults/sec, which sums soft+hard faults and is
/// dominated by harmless soft faults resolved straight from RAM - see PageFaultService's remarks.</summary>
public sealed class HardFaultRateInfo
{
    public bool IsAvailable { get; init; }
    public double PagesInputPerSec { get; init; }
    public double PageReadsPerSec { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#279 fallback mode: one process's Process(*)\Page Faults/sec reading. Explicitly an
/// approximation - this counter is total (soft+hard) faults, not hard-only; Windows exposes no
/// per-process hard-fault-only performance counter. See PageFaultService.SampleTopProcesses.</summary>
public sealed class ProcessPageFaultRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double PageFaultsPerSec { get; init; }
}

/// <summary>#279 ETW deep mode: one (process, faulting file) pair's hard-fault count for the
/// current measurement session, from Microsoft-Windows-Kernel-Memory's hard-fault events - see
/// HardFaultEtwService's remarks for the capture/parse shape. A blank FileName is a legitimate
/// outcome (a private/anonymous page has no backing file), not a parse failure.</summary>
public sealed class HardFaultEtwRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>#280: standby (reclaimable file-cache) list depletion - Free &amp; Zero Page List
/// Bytes plus the three Standby Cache priority tiers (Core/Normal Priority/Reserve - together the
/// full standby list) and Modified Page List Bytes. A pure data-degrade class - the "is this
/// actually thrashing" heuristic (standby list small relative to total RAM while the hard-fault
/// rate is elevated) is computed in ResponsivenessViewModel.SampleLight, which has both this and
/// #278's hard-fault rate on hand; see StandbyListService's remarks.</summary>
public sealed class StandbyListInfo
{
    public bool IsAvailable { get; init; }
    public long FreeZeroBytes { get; init; }
    public long StandbyCoreBytes { get; init; }
    public long StandbyNormalBytes { get; init; }
    public long StandbyReserveBytes { get; init; }
    public long StandbyTotalBytes => StandbyCoreBytes + StandbyNormalBytes + StandbyReserveBytes;
    public long ModifiedPageListBytes { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#281: one configured page file (from the PagingFiles registry value) - its volume,
/// media type (HDD/SSD/Unknown, via DiskFragmentationService.GetMediaType - not re-derived) and
/// current/peak usage (Paging File(*)\% Usage / % Usage Peak, per-instance). Mutable/
/// INotifyPropertyChanged for the same reason the Storage tab's own FragmentationRow is - an
/// on-demand per-row fragmentation check (reusing DiskFragmentationService.Analyze directly)
/// updates IsCheckingFragmentation/FragmentationStatusText in place from a button click.</summary>
public sealed class PageFileVolumeRow : TaskManagerPlus.Common.ObservableObject
{
    public string ConfiguredPath { get; init; } = string.Empty;

    /// <summary>Bare drive letter with no colon (e.g. "C") - the exact format
    /// DiskFragmentationService.GetMediaType/Analyze both expect. Empty when the configured path
    /// couldn't be resolved to a drive letter (never guessed).</summary>
    public string VolumeLetter { get; init; } = string.Empty;

    public string DriveLetterText => VolumeLetter.Length > 0 ? $"{VolumeLetter}:" : "Unknown";

    public long MinSizeMb { get; init; }
    public long MaxSizeMb { get; init; }
    public string SizeText => MaxSizeMb > 0 ? $"{MinSizeMb:N0}–{MaxSizeMb:N0} MB" : "System-managed";

    public string MediaType { get; init; } = "Unknown";
    public bool IsMechanical => MediaType == "HDD";

    public double? PercentUsage { get; init; }
    public double? PercentUsagePeak { get; init; }

    private string _fragmentationStatusText = "Not checked";
    public string FragmentationStatusText { get => _fragmentationStatusText; set => SetProperty(ref _fragmentationStatusText, value); }

    private bool _isCheckingFragmentation;
    public bool IsCheckingFragmentation { get => _isCheckingFragmentation; set => SetProperty(ref _isCheckingFragmentation, value); }

    private bool _isFragmentationWarning;
    public bool IsFragmentationWarning { get => _isFragmentationWarning; set => SetProperty(ref _isFragmentationWarning, value); }
}

/// <summary>#282: the "Memory Compression" system process's working set (the compressed-memory
/// pool size) against total physical RAM, plus Modified Page List Bytes (reused from #280's
/// StandbyListInfo rather than a second Memory\Modified Page List Bytes counter instance) -
/// decompression cost shows up as CPU spikes/micro-stutter, not as a memory number alone.
/// IsAvailable false means the process couldn't be found - a legitimate outcome (Windows hasn't
/// compressed anything yet, or this build/configuration doesn't run it as a separate process), not
/// a lookup failure - see MemoryCompressionService.Sample's remarks.</summary>
public sealed class MemoryCompressionInfo
{
    public bool IsAvailable { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ModifiedPageListBytes { get; init; }
    public long TotalRamBytes { get; init; }
    public double PercentOfTotalRam => TotalRamBytes > 0 ? Math.Clamp((double)WorkingSetBytes / TotalRamBytes * 100.0, 0, 100) : 0;
    public string StatusText { get; init; } = string.Empty;
}
