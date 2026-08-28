namespace TaskManagerPlus.Models;

/// <summary>
/// #370: one entry in the Storage tab's unified event timeline - covering the storport/disk/NTFS/
/// PnP provider family (`disk`, `Ntfs`, `volmgr`, `partmgr`, `storahci`, `stornvme`, `iaStorAC`,
/// `volsnap`, `Microsoft-Windows-Kernel-PnP`) that #371-#374 all filter/group this same list by.
/// DeviceText is a best-effort label - either a resolved drive letter (volume-level events), a
/// "Disk N (Model)" label (physical-disk-level events, via Win32_DiskDrive.Index), a raw
/// "\Device\..." path fragment when neither resolves, or "Unknown device" when the message has no
/// recognizable device reference at all - never a guess. See StorageEventTimelineService.
/// </summary>
public sealed class StorageTimelineEvent
{
    public DateTime TimeCreated { get; init; }

    /// <summary>Display label for the event source, e.g. "Disk", "Ntfs", "storahci", "Kernel-PnP".</summary>
    public string Provider { get; init; } = string.Empty;

    public int EventId { get; init; }

    public string DeviceText { get; init; } = "Unknown device";

    /// <summary>Short human label for what this event means, e.g. "I/O retried", "Controller reset",
    /// "Surprise removal" - drives the #371-#374 card filters below.</summary>
    public string Category { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
