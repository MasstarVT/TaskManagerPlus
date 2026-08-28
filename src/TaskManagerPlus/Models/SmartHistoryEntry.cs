namespace TaskManagerPlus.Models;

/// <summary>#325: one persisted snapshot of one drive's key SMART attributes, written to
/// smart-history.json under AppPaths.SettingsDirectory at most once per app start per disk (the
/// first time that disk's SMART details are read this session - see SmartHistoryService.RecordIfNew).
/// Every entry is kept (not just the latest two) so #326's trend chart has a real series to plot.</summary>
public sealed class SmartHistoryEntry
{
    /// <summary>Best-effort stable identity for one physical disk across app runs - disk index
    /// alone isn't guaranteed stable across reboots/hot-plugs, so this pairs it with the model
    /// string, the same tradeoff SystemSpecsService's own index-based WMI pairing already accepts
    /// elsewhere (ReadDiskWearByIndex, SmartRawAttributeService's BusType/MediaType lookups).</summary>
    public string DiskKey { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    // SMART 05/C5/C6/BB/C7 - the same five attributes the Storage tab's triage-tile strip (#306) tracks.
    public int Reallocated { get; set; }
    public int PendingSector { get; set; }
    public int OfflineUncorrectable { get; set; }
    public int ReportedUncorrectable { get; set; }
    public int UdmaCrcErrors { get; set; }

    // NVMe (#315/#316) - null for SATA/ATA drives.
    public int? NvmePercentageUsed { get; set; }
    public int? NvmeAvailableSparePercent { get; set; }
    public double? NvmeDataUnitsWrittenTb { get; set; }

    // #327: SATA SSD host-written bytes (attribute 0xF1/0xF9 x bytes-per-sector) and reported
    // life-left percent (0xE9/0xE7), when present - null on HDD/NVMe media.
    public double? HostWrittenBytes { get; set; }
    public int? SataLifeLeftPercent { get; set; }
}
