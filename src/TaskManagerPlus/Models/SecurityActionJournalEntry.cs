namespace TaskManagerPlus.Models;

/// <summary>What kind of reversible action a #899 journal entry records - decides which existing
/// toggle method Undo calls back into.</summary>
public enum SecurityActionKind
{
    FirewallRuleDisable,
    ServiceDisable,
    ScheduledTaskDisable,
    StartupItemDisable,
    FileQuarantine,
    RestorePoint,
    Other,
}

/// <summary>
/// One #899 action-journal entry - persisted to security-actions.json (AppPaths.SettingsDirectory)
/// via SecurityActionJournalService. Every Disable/Quarantine/Restore-point action wired into the
/// journal records one of these; where the action IS reversible, UndoPayload carries enough to
/// call the SAME existing toggle method again with the opposite state (see
/// SecurityViewModel.UndoJournalEntry for exactly what each Kind's payload contains and how it's
/// used).
/// </summary>
public sealed class SecurityActionJournalEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>For display - the grid's "g" format would otherwise show UTC as if it were local.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    public SecurityActionKind Kind { get; init; }
    public string ActionDescription { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;

    /// <summary>Kind-specific reversal data (a small JSON object, serialized to string) - null for
    /// an action that isn't reversible at all (e.g. a restore-point creation). See
    /// SecurityActionJournalService's Build*UndoPayload helpers for the exact shape per Kind.</summary>
    public string? UndoPayload { get; init; }

    public bool IsUndone { get; set; }

    /// <summary>HMAC over the fields Undo reads (see JournalIntegrityService) - null on entries
    /// written before it existed or without a trusted key, which makes them not undoable.</summary>
    public string? Mac { get; set; }

    /// <summary>Set by SecurityActionJournalService from <see cref="Mac"/>, never read from the
    /// file. Undo re-verifies (TryGetVerifiedUndoPayload) rather than trusting this flag.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVerified { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanUndo => !IsUndone && UndoPayload is not null && IsVerified;

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => IsUndone ? "Undone"
        : UndoPayload is not null && !IsVerified ? "Unverified - undo disabled"
        : "Active";
}
