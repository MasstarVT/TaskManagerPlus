namespace TaskManagerPlus.Models;

/// <summary>
/// #979: one queued-for-later remediation action - a scheduled task
/// (Services/ScheduledTaskService.CreateOnStartAsync/CreateOnceAsync) plus enough metadata for the
/// Changes panel's "Queued fixes" section to show what it is and cancel it. Persisted (append/
/// remove, never edited in place) to deferred-actions.json under AppPaths.SettingsDirectory - the
/// scheduled task itself is the actual source of truth for whether the fix will run, this file is
/// just what the UI reads to list/cancel them without shelling out to `schtasks /query` and
/// re-parsing task names back into a title on every render.
/// </summary>
public sealed class DeferredAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskName { get; set; } = string.Empty;
    public string ActionTitle { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;

    /// <summary>"Next boot" or a specific date/time, as plain text for display - the schedule was
    /// already resolved into the scheduled task itself at creation time, so this is descriptive
    /// only, never re-parsed.</summary>
    public string ScheduleText { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>#972-style provenance line, same shape as ChangeJournalEntry.TriggeredBy.</summary>
    public string TriggeredBy { get; set; } = string.Empty;
}
