namespace TaskManagerPlus.Models;

/// <summary>
/// #831: the subset of a Scheduled Task's per-task XML definition the Security tab's audit pass
/// needs - fields `schtasks /query /fo csv /v` (what ScheduledTaskRow/ScheduledTaskService.ListAsync
/// already surface on the Startup tab) never exposes at all: the Hidden flag, the task's registered
/// folder, its run-as identity, and its action command. See
/// ScheduledTaskService.QuerySecurityInfoAsync for where this comes from (one aggregated
/// `schtasks /query /xml ONE` call, not a duplicate of ScheduledTaskRow's own CSV-backed fields).
/// Deliberately a separate, minimal record rather than added fields on ScheduledTaskRow - this data
/// only feeds AutorunsService's security-lens findings pass, never binds to the Startup tab's own
/// Scheduled Tasks grid.
/// </summary>
public sealed class ScheduledTaskXmlInfo
{
    /// <summary>Full task path as Task Scheduler registers it, e.g.
    /// "\Microsoft\Windows\Defrag\ScheduledDefrag" - matches ScheduledTaskRow.Name's format, so a
    /// finding's Reason text can point a user at the same name to look up in the Startup tab's grid.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Name's folder portion only (everything before the last backslash), e.g.
    /// "\Microsoft\Windows\Defrag" - "\" for a task registered at the root.</summary>
    public string FolderPath { get; init; } = string.Empty;

    public bool IsHidden { get; init; }

    /// <summary>Raw &lt;UserId&gt;/&lt;GroupId&gt; text from the task's Principal - a SID
    /// (e.g. "S-1-5-18" for SYSTEM), a DOMAIN\user account name, or a well-known name like "SYSTEM".</summary>
    public string RunAsUser { get; init; } = string.Empty;

    /// <summary>Every &lt;Exec&gt; action's Command + Arguments, joined with " ; " for a task with
    /// multiple actions - empty for a task with no Exec action at all (e.g. a COM handler action).</summary>
    public string ActionCommand { get; init; } = string.Empty;
}
