namespace TaskManagerPlus.Models;

/// <summary>
/// Round 13, #320: one entry from the NVMe Error Information Log (log page 0x01) - 64-byte
/// entries, most-recent first. An empty log is the normal, expected result on a healthy drive, not
/// a read failure - StorageViewModel states that explicitly rather than leaving a blank grid.
/// </summary>
public sealed class NvmeErrorLogEntry
{
    public ulong ErrorCount { get; init; }
    public ushort SubmissionQueueId { get; init; }
    public ushort CommandId { get; init; }

    /// <summary>Raw 16-bit completion Status Field (phase tag bit still included) - kept for
    /// completeness alongside the decoded StatusText below.</summary>
    public ushort StatusField { get; init; }
    public string StatusText { get; init; } = string.Empty;

    public ulong Lba { get; init; }
    public uint NamespaceId { get; init; }
}
