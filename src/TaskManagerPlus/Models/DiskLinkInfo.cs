namespace TaskManagerPlus.Models;

/// <summary>
/// #379/#381: negotiated-vs-rated link speed (SATA) or negotiated-vs-max link speed/width (NVMe)
/// for one disk - SATA and NVMe are mutually exclusive per disk, so both shapes live on one result
/// with whichever half doesn't apply left at its default/Unavailable. See StorageLinkService.
/// </summary>
public sealed class DiskLinkInfo
{
    public bool IsSata { get; init; }
    public bool IsNvme { get; init; }

    // #379: SATA generation, 1/2/3 (1.5/3.0/6.0 Gb/s) decoded from ATA IDENTIFY DEVICE words 76/77.
    public int? SataNegotiatedGen { get; init; }
    public int? SataMaxSupportedGen { get; init; }
    public bool SataAvailable { get; init; }
    public string SataUnavailableReason { get; init; } = string.Empty;

    /// <summary>True only when both figures were actually read and the negotiated generation is
    /// below what the drive itself reports supporting - never inferred from a missing reading.</summary>
    public bool SataDowngraded => SataAvailable && SataNegotiatedGen is > 0 && SataMaxSupportedGen is > 0
        && SataNegotiatedGen < SataMaxSupportedGen;

    // #381: PCIe generation (1=2.5, 2=5.0, 3=8.0, 4=16.0, 5=32.0 GT/s) and lane count, from the
    // DEVPKEY_PciDevice_Current/MaxLinkSpeed/Width device properties on the NVMe controller's PCI
    // function devnode.
    public int? NvmeCurrentLinkSpeedGen { get; init; }
    public int? NvmeCurrentLinkWidth { get; init; }
    public int? NvmeMaxLinkSpeedGen { get; init; }
    public int? NvmeMaxLinkWidth { get; init; }
    public bool NvmeAvailable { get; init; }
    public string NvmeUnavailableReason { get; init; } = string.Empty;

    public bool NvmeDowngraded => NvmeAvailable
        && ((NvmeCurrentLinkSpeedGen is > 0 && NvmeMaxLinkSpeedGen is > 0 && NvmeCurrentLinkSpeedGen < NvmeMaxLinkSpeedGen)
            || (NvmeCurrentLinkWidth is > 0 && NvmeMaxLinkWidth is > 0 && NvmeCurrentLinkWidth < NvmeMaxLinkWidth));
}
