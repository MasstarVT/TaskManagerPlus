namespace TaskManagerPlus.Models;

/// <summary>Round 16, #853: one row in the token-privilege-audit result for a process - a privilege
/// name (SeDebugPrivilege, SeBackupPrivilege, ...), its current attribute state text, and whether
/// it's on the small dangerous-if-unexpected watch list AND actually enabled AND held by a
/// non-Microsoft-signed process (the specific combination #853 asks to call out - a legitimate
/// system process holding SeDebugPrivilege is completely normal and not flagged). See
/// TokenPrivilegeAuditService. "Quick flag, not a verdict" - same framing as every other heuristic
/// in this app.</summary>
public sealed class TokenPrivilegeInfo
{
    public string Name { get; init; } = string.Empty;
    public string StateText { get; init; } = string.Empty;
    public bool Enabled { get; init; }

    /// <summary>True for SeDebugPrivilege/SeTcbPrivilege/SeLoadDriverPrivilege/SeBackupPrivilege/
    /// SeImpersonatePrivilege regardless of state - lets the UI show "on the watch list" even for a
    /// disabled one, distinct from IsFlagged (the actual "worth a look" combination).</summary>
    public bool IsWatchListed { get; init; }

    /// <summary>Watch-listed AND enabled AND the process isn't Microsoft-signed - the actual
    /// "worth a second look" signal.</summary>
    public bool IsFlagged { get; init; }
}
