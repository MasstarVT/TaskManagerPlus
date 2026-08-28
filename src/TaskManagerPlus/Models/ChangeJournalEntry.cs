namespace TaskManagerPlus.Models;

/// <summary>
/// #972-973: what kind of mutation a <see cref="ChangeJournalEntry"/> records - drives both the
/// display text and, in the Troubleshoot tab's "Changes made by this app" panel
/// (ChangeJournalViewModel), which inverse Services/*.cs call an Undo click actually runs. Shared
/// between an entry logged at an existing tab's own action (Services/Startup/Processes/Energy &amp;
/// Thermals) and one logged from #967's remediation-review flow, since both ultimately call the
/// same underlying service methods either way - see ChangeJournalViewModel.Undo for the one inverse
/// switch this feeds.
/// </summary>
public enum ChangeKind
{
    ServiceStateChange,
    StartupToggle,
    ProcessPriorityChange,
    ProcessAffinityChange,
    ProcessSuspend,
    ProcessResume,
    ProcessTrimWorkingSet,

    /// <summary>Switched the active power scheme (EnergyThermalsViewModel.SetPowerPlanAsync) -
    /// BeforeValue/AfterValue are scheme GUIDs.</summary>
    PowerPlanChange,

    /// <summary>A single power-plan setting value changed within the active scheme (currently
    /// only #967's "lower minimum processor state" remediation action) - BeforeValue/AfterValue
    /// are the setting's plain numeric percent, not a GUID, so this is kept distinct from
    /// PowerPlanChange above rather than reusing it for two differently-shaped value pairs.</summary>
    PowerSettingChange,

    /// <summary>A one-shot tool run (sfc, DISM, netsh int ip reset, chkdsk /scan) - never
    /// undoable, there's nothing to reverse.</summary>
    OneShotToolRun,
}

/// <summary>
/// #972: one row in change-journal.jsonl (AppPaths.SettingsDirectory) - every system mutation this
/// app has ever performed, whether triggered directly from a tab's own Start/Stop/Restart/Toggle/
/// SetPriority/SetAffinity/Suspend/Resume/TrimWorkingSet/SetActivePlan button, or from #967's
/// remediation-review "Run" button. Appended (never edited) by ChangeJournalService.Append, except
/// for the Undone/UndoneAtUtc pair which ChangeJournalService.MarkUndone flips in place once #973's
/// Undo actually runs successfully.
/// </summary>
public sealed class ChangeJournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public ChangeKind Kind { get; set; }

    /// <summary>Display name of what was changed - a service's display name, a startup item's
    /// name, "chrome.exe (PID 1234)", a power plan's name, ...</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Plain-English past-tense description - "Restarted service", "Disabled at startup",
    /// "Lowered priority to Idle", "Ran sfc /scannow", ...</summary>
    public string ActionDescription { get; set; } = string.Empty;

    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }

    /// <summary>#972: "the finding/user-click that triggered it" - e.g. "Services tab" for a
    /// direct action, or "Health Check finding: CPU running hot -> Fix this" for a #967 remediation
    /// run.</summary>
    public string TriggeredBy { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public bool IsUndoable { get; set; }

    /// <summary>Why Undo isn't offered, when IsUndoable is false at journal time (e.g. "One-shot
    /// tool run - nothing to reverse."). A process-kind entry that WAS undoable at journal time can
    /// still turn out to be un-undoable *now* (the process has since exited) - that's a live check
    /// ChangeJournalViewModel makes at render time, not this field.</summary>
    public string? NotUndoableReason { get; set; }

    // ----- kind-specific identifiers needed to actually run the inverse operation -----

    public string? ServiceName { get; set; }

    public int? Pid { get; set; }
    public string? ProcessName { get; set; }

    public string? StartupItemName { get; set; }
    public string? StartupItemCommand { get; set; }
    public string? StartupItemSource { get; set; } // StartupSource enum, stored as text

    public string? PowerPlanGuidBefore { get; set; }

    /// <summary>#971: path to the .reg export taken before this entry's action ran, if any -
    /// offered as a secondary "restore from registry backup" option alongside the primary
    /// same-service-method Undo.</summary>
    public string? RegistryBackupPath { get; set; }

    public bool Undone { get; set; }
    public DateTime? UndoneAtUtc { get; set; }
}
