namespace TaskManagerPlus.Models;

/// <summary>#392: one recorded "this PC asked for the BitLocker recovery key" event, from the
/// BitLocker Management / BitLocker-API operational logs - plus a best-effort correlation against
/// nearby TPM/firmware/Secure-Boot-related events. LikelyCauseText is deliberately worded as a
/// hypothesis, never a diagnosis - see BitLockerService.ReadRecoveryPromptHistory's remarks for why
/// this correlation can't be more than that from event timestamps alone.</summary>
public sealed class BitLockerRecoveryPromptEvent
{
    public DateTime TimeCreated { get; init; }
    public string Channel { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Empty when no TPM/firmware/Secure-Boot-related event was found within the
    /// correlation window - never fabricated when nothing nearby was found.</summary>
    public string LikelyCauseText { get; init; } = string.Empty;
    public bool HasLikelyCause => LikelyCauseText.Length > 0;
}
