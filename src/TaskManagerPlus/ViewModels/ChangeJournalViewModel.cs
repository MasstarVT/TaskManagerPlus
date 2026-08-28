using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>#973: one row in the "Changes made by this app" panel - wraps a persisted
/// ChangeJournalEntry with the live, render-time undoability check ChangeJournalViewModel.Evaluate
/// computes (a process-kind entry that WAS undoable when journaled can still turn out not to be
/// *now*, e.g. the process has since exited - see CanUndoNow's remarks there).</summary>
public sealed class ChangeJournalRow : ObservableObject
{
    public ChangeJournalEntry Entry { get; }
    public bool CanUndoNow { get; }
    public string BlockedReason { get; }

    /// <summary>#971: a secondary restore option alongside the primary same-service-method Undo,
    /// offered only when this entry's action backed up a registry key first.</summary>
    public bool HasRegistryBackup => Entry.RegistryBackupPath is { Length: > 0 };

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }

    public ChangeJournalRow(ChangeJournalEntry entry, bool canUndoNow, string blockedReason)
    {
        Entry = entry;
        CanUndoNow = canUndoNow;
        BlockedReason = blockedReason;
    }
}

/// <summary>
/// #973: backs the Troubleshoot tab's "Changes made by this app" sub-page (same landing-page-swap
/// pattern as Timeline/Baselines/BackgroundHealth - see TroubleshootViewModel's remarks). Lists
/// every ChangeJournalService entry newest-first and, for the ones CanUndoNow, runs the recorded
/// inverse operation through the exact same Services/*.cs methods the original action used - never
/// a different code path than what a user would click on the Services/Startup/Processes/Energy &amp;
/// Thermals tab itself.
/// </summary>
public sealed class ChangeJournalViewModel : ObservableObject
{
    public ObservableCollection<ChangeJournalRow> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public AsyncRelayCommand RestoreFromBackupCommand { get; }

    public ChangeJournalViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        UndoCommand = new AsyncRelayCommand(param => UndoAsync(param as ChangeJournalRow), param => param is ChangeJournalRow { CanUndoNow: true, IsBusy: false });
        RestoreFromBackupCommand = new AsyncRelayCommand(param => RestoreFromBackupAsync(param as ChangeJournalRow), param => param is ChangeJournalRow { HasRegistryBackup: true, IsBusy: false });
        Refresh();
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var entry in ChangeJournalService.LoadAll())
        {
            var (canUndo, reason) = Evaluate(entry);
            Entries.Add(new ChangeJournalRow(entry, canUndo, reason));
        }
    }

    /// <summary>Whether this entry's inverse can be run right now, and why not when it can't -
    /// "process no longer running" / "not reversible" per #973's own examples.</summary>
    private static (bool CanUndo, string Reason) Evaluate(ChangeJournalEntry entry)
    {
        if (entry.Undone) return (false, "Already undone.");
        if (!entry.Success) return (false, "The original action didn't succeed - nothing to undo.");
        if (!entry.IsUndoable) return (false, entry.NotUndoableReason ?? "Not reversible.");

        switch (entry.Kind)
        {
            case ChangeKind.ProcessPriorityChange:
            case ChangeKind.ProcessAffinityChange:
            case ChangeKind.ProcessSuspend:
            case ChangeKind.ProcessResume:
                if (entry.Pid is not { } pid) return (false, "No process id recorded.");
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (entry.ProcessName is { Length: > 0 } name &&
                        !string.Equals(proc.ProcessName, Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                        return (false, "Process no longer running (this PID now belongs to a different process).");
                }
                catch
                {
                    return (false, "Process no longer running.");
                }
                return (true, string.Empty);

            case ChangeKind.ServiceStateChange:
                return string.IsNullOrEmpty(entry.ServiceName) ? (false, "No service name recorded.") : (true, string.Empty);

            case ChangeKind.StartupToggle:
                return string.IsNullOrEmpty(entry.StartupItemName) ? (false, "No startup item recorded.") : (true, string.Empty);

            case ChangeKind.PowerPlanChange:
            case ChangeKind.PowerSettingChange:
                return string.IsNullOrEmpty(entry.BeforeValue) ? (false, "No prior value recorded.") : (true, string.Empty);

            case ChangeKind.ProcessTrimWorkingSet:
                return (false, "Trimming a working set can't be undone - Windows lets it regrow on demand.");

            case ChangeKind.OneShotToolRun:
                return (false, "One-shot tool run - nothing to reverse.");

            default:
                return (false, "Not reversible.");
        }
    }

    private async Task UndoAsync(ChangeJournalRow? row)
    {
        if (row is null || !row.CanUndoNow) return;

        row.IsBusy = true;
        try
        {
            var (success, message) = await RunInverseAsync(row.Entry);
            row.ResultText = message;
            if (success)
            {
                ChangeJournalService.MarkUndone(row.Entry.Id);
                Refresh(); // reloads from disk so the row's Undone/CanUndoNow state stays honest
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task RestoreFromBackupAsync(ChangeJournalRow? row)
    {
        if (row?.Entry.RegistryBackupPath is not { Length: > 0 } path) return;

        row.IsBusy = true;
        try
        {
            var (success, error) = await RegistryBackupService.RestoreKeyAsync(path);
            row.ResultText = success ? "Registry key restored from backup." : $"Couldn't restore from backup: {error}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>The one inverse-operation switch every Undo click runs through, regardless of
    /// whether the original entry came from a tab's own action or #967's remediation flow - both
    /// funnel through the same ChangeKind values (see ChangeJournalEntry's remarks).</summary>
    private static async Task<(bool Success, string Message)> RunInverseAsync(ChangeJournalEntry entry)
    {
        switch (entry.Kind)
        {
            case ChangeKind.ServiceStateChange:
            {
                if (string.IsNullOrEmpty(entry.ServiceName)) return (false, "No service name recorded.");
                // The inverse of "this service was stopped/started/restarted" is simply "make sure
                // it's running again" - mirrors #973's own example. A restart's inverse is the same
                // call (there's no earlier state to return to beyond "running").
                var (success, error) = await Task.Run(() => ServiceControlService.Start(entry.ServiceName));
                return (success, success ? $"{entry.Target} started." : $"Couldn't start {entry.Target}: {error}");
            }

            case ChangeKind.StartupToggle:
            {
                bool wasEnabled = string.Equals(entry.BeforeValue, "Enabled", StringComparison.OrdinalIgnoreCase);
                var item = new StartupItem
                {
                    Name = entry.StartupItemName ?? string.Empty,
                    Command = entry.StartupItemCommand ?? string.Empty,
                    Source = Enum.TryParse<StartupSource>(entry.StartupItemSource, out var src) ? src : StartupSource.RegistryRunHkcu,
                };
                var (success, error) = StartupManagerService.SetEnabled(item, wasEnabled);
                return (success, success ? $"{entry.Target} set back to {(wasEnabled ? "enabled" : "disabled")}." : $"Couldn't change {entry.Target}: {error}");
            }

            case ChangeKind.ProcessPriorityChange:
            {
                if (entry.Pid is not { } pid || !Enum.TryParse<ProcessPriorityClass>(entry.BeforeValue, out var before))
                    return (false, "No prior priority recorded.");
                var (success, error) = ProcessControlService.SetPriority(pid, before);
                return (success, success ? $"Priority restored to {before}." : $"Couldn't restore priority: {error}");
            }

            case ChangeKind.ProcessAffinityChange:
            {
                if (entry.Pid is not { } pid || !long.TryParse(entry.BeforeValue, out var mask))
                    return (false, "No prior affinity mask recorded.");
                var (success, error) = ProcessControlService.SetAffinity(pid, mask);
                return (success, success ? "CPU affinity restored." : $"Couldn't restore affinity: {error}");
            }

            case ChangeKind.ProcessSuspend:
            {
                if (entry.Pid is not { } pid) return (false, "No process id recorded.");
                var (success, error) = ProcessControlService.Resume(pid);
                return (success, success ? "Process resumed." : $"Couldn't resume: {error}");
            }

            case ChangeKind.ProcessResume:
            {
                if (entry.Pid is not { } pid) return (false, "No process id recorded.");
                var (success, error) = ProcessControlService.Suspend(pid);
                return (success, success ? "Process suspended again." : $"Couldn't suspend: {error}");
            }

            case ChangeKind.PowerPlanChange:
            {
                if (string.IsNullOrEmpty(entry.BeforeValue)) return (false, "No prior power plan recorded.");
                var (success, error) = await PowerPlanService.SetActivePlanAsync(entry.BeforeValue);
                return (success, success ? "Power plan restored." : $"Couldn't restore power plan: {error}");
            }

            case ChangeKind.PowerSettingChange:
            {
                if (string.IsNullOrEmpty(entry.BeforeValue)) return (false, "No prior value recorded.");
                var (setOutput, setExit) = await TroubleshootService.RunCapturedAsync(
                    "powercfg.exe", $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {entry.BeforeValue}", timeoutMs: 10_000);
                if (setExit != 0) return (false, $"Couldn't restore setting: {setOutput}");
                var (actOutput, actExit) = await TroubleshootService.RunCapturedAsync("powercfg.exe", "/setactive SCHEME_CURRENT", timeoutMs: 10_000);
                return (actExit == 0, actExit == 0 ? "Minimum processor state restored." : $"Couldn't apply: {actOutput}");
            }

            default:
                return (false, "Not reversible.");
        }
    }
}
