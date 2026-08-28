using System.Windows.Input;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #968-971: backs RemediationReviewWindow - the review dialog a "Fix this" click on a Health
/// Check finding opens (see SummaryViewModel.FixFindingCommand). Never runs anything on its own;
/// every action here is explicit and user-triggered:
///   1. The selected action's exact <see cref="RemediationAction.Command"/> is always on screen,
///      in a read-only copyable TextBox, before Run is ever reachable (#968).
///   2. "Preview (dry run)" runs PreviewCommand and shows its output, or reports plainly that no
///      dry run exists for this action (#969).
///   3. For a Medium/High-risk action, Run stays disabled until the restore-point prompt is
///      resolved one way or the other - created, or explicitly skipped (#970).
///   4. Run itself backs up the registry first when the action needs it (#971), then executes,
///      then journals the result (#972) with enough detail for #973's Undo panel to reverse it.
/// </summary>
public sealed class RemediationReviewViewModel : ObservableObject
{
    public string FindingTitle { get; }
    public List<RemediationAction> Actions { get; }

    private RemediationAction? _selectedAction;
    public RemediationAction? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (!SetProperty(ref _selectedAction, value)) return;

            PreviewOutputText = string.Empty;
            RunOutputText = string.Empty;
            StatusMessage = string.Empty;
            RestorePointStatus = string.Empty;
            SystemProtectionLikelyDisabled = false;
            HasRun = false;
            _restorePointResolved = !NeedsRestorePointPrompt;

            OnPropertyChanged(nameof(CommandText));
            OnPropertyChanged(nameof(PreviewCommandText));
            OnPropertyChanged(nameof(RiskLevelText));
            OnPropertyChanged(nameof(RequiresRebootText));
            OnPropertyChanged(nameof(UndoableText));
            OnPropertyChanged(nameof(NeedsRestorePointPrompt));
            RaiseCanExecuteChanged();
        }
    }

    public string CommandText => SelectedAction?.Command ?? string.Empty;
    public string PreviewCommandText => SelectedAction?.PreviewCommand ?? string.Empty;
    public string RiskLevelText => SelectedAction?.RiskLevel.ToString() ?? string.Empty;
    public string RequiresRebootText => SelectedAction?.RequiresReboot == true
        ? "Yes - a restart is recommended/required for this to fully take effect."
        : "No.";
    public string UndoableText => SelectedAction switch
    {
        null => string.Empty,
        { IsUndoable: true } => "Yes - reversible afterward from the Troubleshoot tab's \"Changes made by this app\" panel.",
        { NotUndoableReason: { Length: > 0 } reason } => reason,
        _ => "Not reversible.",
    };

    /// <summary>#970: offered for Medium and High risk (documented on RemediationRiskLevel's own
    /// usages above) - every Low-risk action in this catalog is either read-only (a scan) or
    /// trivially reversible on its own (a toggle/priority change), so a restore-point interruption
    /// isn't warranted for those.</summary>
    public bool NeedsRestorePointPrompt => SelectedAction is { RiskLevel: RemediationRiskLevel.Medium or RemediationRiskLevel.High };

    private bool _restorePointResolved;

    private string _restorePointStatus = string.Empty;
    public string RestorePointStatus { get => _restorePointStatus; private set => SetProperty(ref _restorePointStatus, value); }

    private bool _systemProtectionLikelyDisabled;
    public bool SystemProtectionLikelyDisabled { get => _systemProtectionLikelyDisabled; private set => SetProperty(ref _systemProtectionLikelyDisabled, value); }

    private string _previewOutputText = string.Empty;
    public string PreviewOutputText { get => _previewOutputText; private set => SetProperty(ref _previewOutputText, value); }

    private string _runOutputText = string.Empty;
    public string RunOutputText { get => _runOutputText; private set => SetProperty(ref _runOutputText, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCanExecuteChanged(); } }

    private bool _hasRun;
    public bool HasRun { get => _hasRun; private set => SetProperty(ref _hasRun, value); }

    public AsyncRelayCommand RunPreviewCommand { get; }
    public AsyncRelayCommand CreateRestorePointCommand { get; }
    public RelayCommand SkipRestorePointCommand { get; }
    public RelayCommand OpenSystemProtectionCommand { get; }
    public AsyncRelayCommand RunActionCommand { get; }

    public RemediationReviewViewModel(HealthIssue issue, List<RemediationAction> actions)
    {
        FindingTitle = issue.Title is { Length: > 0 } t ? t : issue.Message;
        Actions = actions;

        RunPreviewCommand = new AsyncRelayCommand(RunPreviewAsync, () => !IsBusy && SelectedAction is not null);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsBusy && NeedsRestorePointPrompt);
        SkipRestorePointCommand = new RelayCommand(_ =>
        {
            _restorePointResolved = true;
            RestorePointStatus = "Skipped - continuing without a restore point.";
            RaiseCanExecuteChanged();
        }, _ => !IsBusy && NeedsRestorePointPrompt);
        OpenSystemProtectionCommand = new RelayCommand(_ => RestorePointService.OpenSystemProtectionSettings());
        RunActionCommand = new AsyncRelayCommand(RunActionAsync, CanRun);

        // Selecting the first action last, so its property-changed side effects (including
        // _restorePointResolved) run against fully-constructed commands above.
        SelectedAction = actions.FirstOrDefault();
    }

    private bool CanRun() => !IsBusy && SelectedAction?.Execute is not null && (!NeedsRestorePointPrompt || _restorePointResolved);

    private static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    private async Task RunPreviewAsync()
    {
        var action = SelectedAction;
        if (action is null) return;

        if (action.ExecutePreview is null)
        {
            PreviewOutputText = "No dry run available for this action.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Running preview...";
        try
        {
            var result = await action.ExecutePreview(CancellationToken.None);
            PreviewOutputText = result.Output;
            StatusMessage = result.Success ? "Preview finished." : "Preview reported an error - see output above.";
        }
        catch (Exception ex)
        {
            PreviewOutputText = $"Preview failed: {ex.Message}";
            StatusMessage = "Preview failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateRestorePointAsync()
    {
        IsBusy = true;
        RestorePointStatus = "Creating restore point...";
        try
        {
            string description = $"TaskManagerPlus before {SelectedAction?.Title ?? "remediation action"}";
            var (success, message, disabled) = await RestorePointService.CreateRestorePointAsync(description);
            RestorePointStatus = message;
            SystemProtectionLikelyDisabled = disabled;
            if (success) _restorePointResolved = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunActionAsync()
    {
        var action = SelectedAction;
        if (action?.Execute is null) return;

        IsBusy = true;
        RunOutputText = string.Empty;
        string? registryBackupPath = null;
        try
        {
            if (action.RegistryKeyToBackup is { Length: > 0 } key)
            {
                StatusMessage = "Backing up registry key...";
                var (backupSuccess, path, error) = await RegistryBackupService.BackupKeyAsync(key);
                if (backupSuccess)
                {
                    registryBackupPath = path;
                    RunOutputText += $"Registry key backed up to {path}\n\n";
                }
                else
                {
                    RunOutputText += $"Registry backup failed ({error}) - continuing anyway.\n\n";
                }
            }

            StatusMessage = "Running...";
            var result = await action.Execute(CancellationToken.None);
            RunOutputText += result.Output;
            StatusMessage = result.Success ? "Done." : "Finished with an error - see output above.";
            HasRun = true;

            ChangeJournalService.Append(BuildJournalEntry(action, result, registryBackupPath));
        }
        catch (Exception ex)
        {
            RunOutputText += $"\nFailed: {ex.Message}";
            StatusMessage = "Failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ChangeJournalEntry BuildJournalEntry(RemediationAction action, RemediationRunResult result, string? registryBackupPath) => new()
    {
        Kind = action.JournalKind,
        Target = action.Title,
        ActionDescription = action.Title,
        BeforeValue = result.BeforeValue,
        AfterValue = result.AfterValue,
        TriggeredBy = $"Health Check finding: {FindingTitle} -> Fix this ({action.Title})",
        Success = result.Success,
        IsUndoable = action.IsUndoable && result.Success,
        NotUndoableReason = action.NotUndoableReason,
        ServiceName = action.ServiceName,
        Pid = action.Pid,
        ProcessName = action.ProcessName,
        StartupItemName = action.StartupItemName,
        StartupItemCommand = action.StartupItemCommand,
        StartupItemSource = action.StartupItemSource,
        RegistryBackupPath = registryBackupPath,
    };
}
