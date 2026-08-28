namespace TaskManagerPlus.Models;

/// <summary>
/// Result of one on-demand raw-SMART read (round: #301/#302/#304/#312) for a single disk -
/// the decoded attribute table plus enough context (which vendor profile matched, where the data
/// came from, and this disk's bus/media type) for StorageViewModel to derive the triage strip
/// (#306), temperature/power-on/endurance summaries (#307-#311), and the "why is this empty"
/// message (#312) without re-querying WMI itself.
/// </summary>
public sealed class SmartRawResult
{
    public List<SmartRawAttribute> Attributes { get; init; } = new();

    /// <summary>Name of the matched SmartVendorProfiles entry ("Seagate", "Samsung", ...), or null
    /// when no vendor prefix matched this disk's Model string - shown under the grid (#304) so the
    /// user knows which interpretation produced the numbers.</summary>
    public string? VendorProfileName { get; init; }

    /// <summary>Where the 512-byte SMART data blob actually came from - the WMI class, or the
    /// SCSI pass-through fallback (#312) - shown as a small caption under the grid.</summary>
    public string SourceDescription { get; init; } = string.Empty;

    /// <summary>True when neither the WMI class nor the SCSI pass-through fallback produced any
    /// data - UnavailableReason explains why (#312) rather than the grid just being silently
    /// empty.</summary>
    public bool Unavailable { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    /// <summary>MSFT_PhysicalDisk.BusType, resolved to a friendly name ("USB", "SATA", "NVMe",
    /// ...), or "Unknown" when the namespace/class/this disk's entry isn't available.</summary>
    public string BusType { get; init; } = "Unknown";

    /// <summary>MSFT_PhysicalDisk.MediaType resolved to "HDD"/"SSD"/"Unknown" - drives whether
    /// #309's HDD-only wear-cycle card is shown at all.</summary>
    public string MediaType { get; init; } = "Unknown";

    /// <summary>MSFT_StorageReliabilityCounter.Wear for this disk, when available - the existing
    /// driver-summarised wear percentage #311 cross-checks the raw SSD attributes against.</summary>
    public int? DriverWearPercent { get; init; }

    /// <summary>Win32_DiskDrive.BytesPerSector, for #310's LBA-count-to-bytes conversion. Defaults
    /// to the near-universal 512 when the field can't be read, rather than failing the whole
    /// endurance calculation over one missing property.</summary>
    public int BytesPerSector { get; init; } = 512;
}
