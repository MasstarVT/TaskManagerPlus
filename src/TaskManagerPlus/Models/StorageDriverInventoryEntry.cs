namespace TaskManagerPlus.Models;

/// <summary>#383: one loaded storage-stack driver's version/date/signer, from either
/// Win32_PnPSignedDriver (when the driver has its own PnP device node - the SATA/NVMe adapter,
/// each physical disk) or read straight off the .sys file on disk via its Services registry
/// ImagePath (storport/partmgr/volsnap and other filter drivers with no device node of their own).
/// SourceNote always says which path produced this row, and every field independently degrades to
/// "Unknown" rather than the whole row disappearing.</summary>
public sealed class StorageDriverInventoryEntry
{
    public string ServiceName { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string Version { get; init; } = "Unknown";
    public string DateText { get; init; } = "Unknown";
    public string Signer { get; init; } = "Unknown";
    public string SourceNote { get; init; } = string.Empty;
}
