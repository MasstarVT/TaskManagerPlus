namespace TaskManagerPlus.Models;

/// <summary>
/// Round 13, #321: one entry from the NVMe Device Self-test Log (log page 0x06) - up to 20 past
/// results, most-recent first. Also carries the "test currently running" state from the same log
/// page (bytes 0-1) so the UI can show progress without a second read.
/// </summary>
public sealed class NvmeSelfTestResult
{
    public string OperationText { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public byte SegmentNumber { get; init; }
    public ulong? PowerOnHours { get; init; }
    public ulong? FailingLba { get; init; }
    public uint? NamespaceId { get; init; }
}
