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
    public SecurityActionKind Kind { get; init; }
    public string ActionDescription { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;

    /// <summary>Kind-specific reversal data (a small JSON object, serialized to string) - null for
    /// an action that isn't reversible at all (e.g. a restore-point creation). See
    /// SecurityActionJournalService's Build*UndoPayload helpers for the exact shape per Kind.</summary>
    public string? UndoPayload { get; init; }

    public bool IsUndone { get; set; }

    public bool CanUndo => !IsUndone && UndoPayload is not null;

    public string StatusText => IsUndone ? "Undone" : "Active";
}
