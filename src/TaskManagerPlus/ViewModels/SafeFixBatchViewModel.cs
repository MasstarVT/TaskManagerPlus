using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #976: the Health Check card's "Run safe fixes" batch runner - multi-selects every currently
/// fired finding whose resolved action(s) are ALL RemediationRiskLevel.Low AND IsUndoable, then
/// runs them sequentially. Explicitly excludes anything Medium/High risk or non-undoable - filtered
/// right here, at selection time, not just described in the UI copy (see Refresh below), per that
/// suggestion's own "filter this at the point where you decide what's selectable" requirement.
///
/// Rebuilt every SummaryViewModel.RefreshHealthIssues pass (Refresh is called from there) so the
/// list always reflects what's currently firing - but never rebuilt mid-run (IsRunning guards it),
/// so a batch already in progress can't have its own item list swapped out from under it.
/// </summary>
public sealed class SafeFixBatchViewModel : ObservableObject
{
    private readonly ServicesViewModel _services;

    public ObservableCollection<SafeFixBatchItem> Items { get; } = new();

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseCanExecuteChanged(); } }

    public AsyncRelayCommand RunSafeFixesCommand { get; }

    public SafeFixBatchViewModel(ServicesViewModel services)
    {
        _services = services;
        RunSafeFixesCommand = new AsyncRelayCommand(RunAllAsync, () => !IsRunning && !ReadOnlyModeService.IsReadOnly && Items.Count > 0);
    }

    /// <summary>Re-resolves every currently-fired finding's action(s) and keeps only the findings
    /// whose ENTIRE resolved action set is low-risk and undoable - a finding that resolves to even
    /// one Medium/High-risk or non-undoable action is left out of the batch entirely (it's still
    /// reachable one at a time from its own "Fix this" button).</summary>
    public void Refresh(IEnumerable<HealthIssue> issues)
    {
        if (IsRunning) return; // never swap the list out from under an in-progress run

        Items.Clear();
        foreach (var issue in issues)
        {
            if (!issue.HasFixAction) continue;

            var resolved = RemediationActionCatalog.Resolve(issue, _services);
            if (resolved.Count == 0) continue;
            if (!resolved.All(a => a.RiskLevel == RemediationRiskLevel.Low && a.IsUndoable)) continue;

            // One action per finding - the first resolved action is what "Fix this" would have
            // pre-selected too (RemediationReviewViewModel.SelectedAction defaults to
            // actions.FirstOrDefault()).
            Items.Add(new SafeFixBatchItem(issue, resolved[0]));
        }

        RaiseCanExecuteChanged();
    }

    private async Task RunAllAsync()
    {
        IsRunning = true;
        try
        {
            foreach (var item in Items.ToList())
            {
                if (item.Action.Execute is null)
                {
                    item.Status = SafeFixItemStatus.Failed;
                    item.ResultText = "No runnable action.";
                    continue;
                }

                item.Status = SafeFixItemStatus.Running;
                try
                {
                    var result = await item.Action.Execute(CancellationToken.None);
                    item.Status = result.Success ? SafeFixItemStatus.Succeeded : SafeFixItemStatus.Failed;
                    item.ResultText = result.Output;

                    ChangeJournalService.Append(new ChangeJournalEntry
                    {
                        Kind = item.Action.JournalKind,
                        Target = item.Action.Title,
                        ActionDescription = item.Action.Title,
                        BeforeValue = result.BeforeValue,
                        AfterValue = result.AfterValue,
                        TriggeredBy = $"Health Check finding: {item.Issue.Title ?? item.Issue.Message} -> Run safe fixes",
                        Success = result.Success,
                        IsUndoable = item.Action.IsUndoable && result.Success,
                        NotUndoableReason = item.Action.NotUndoableReason,
                        ServiceName = item.Action.ServiceName,
                        Pid = item.Action.Pid,
                        ProcessName = item.Action.ProcessName,
                        StartupItemName = item.Action.StartupItemName,
                        StartupItemCommand = item.Action.StartupItemCommand,
                        StartupItemSource = item.Action.StartupItemSource,
                    });

                    // #978: a batched fix can in principle declare RequiresReboot too - keep the
                    // "restart pending — including from N fix(es) you ran" banner honest for this
                    // path as well, not just the single-action review dialog.
                    if (result.Success && item.Action.RequiresReboot)
                        PendingRebootActionsService.Add(item.Action.Title);
                }
                catch (Exception ex)
                {
                    item.Status = SafeFixItemStatus.Failed;
                    item.ResultText = $"Failed: {ex.Message}";
                }
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void RaiseCanExecuteChanged() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}
