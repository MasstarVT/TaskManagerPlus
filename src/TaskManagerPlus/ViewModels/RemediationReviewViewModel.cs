using System.Collections.ObjectModel;
using System.Windows.Input;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #968-980: backs RemediationReviewWindow - the review dialog a "Fix this" click on a Health
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
///   5. #974: Run/Preview both stay disabled whenever a declared precondition fails, with a
///      tooltip stating precisely which one and why.
///   6. #975: once Run finishes, the finding's own rule (if it has one) is re-evaluated fresh so
///      the dialog can say "this finding cleared" / "still present" rather than leaving that to a
///      separate manual re-check.
///   7. #977: sfc/DISM/chkdsk stream their output live (with a real progress bar where the tool's
///      own output is percentage-parseable) instead of only showing text once the whole run ends,
///      with a Cancel button that kills the process mid-run.
///   8. #979: an action that needs its target volume offline can be queued as a scheduled task
///      instead of run now - "next boot" or a specific time.
///   9. #980: Run and Queue (not Preview - dry-run always works) are disabled app-wide while
///      read-only mode is on.
/// </summary>
public sealed class RemediationReviewViewModel : ObservableObject
{
    private readonly ServicesViewModel _services;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly Func<string?, bool?>? _reEvaluateFinding;
    private readonly string? _originatingRuleId;

    private int _preconditionCheckVersion;
    private CancellationTokenSource? _runCts;

    public string FindingTitle { get; }
    public List<RemediationAction> Actions { get; }

    /// <summary>#977: streamed stdout/stderr lines for the currently selected action's most recent
    /// preview/run - cleared at the start of each new preview/run. Only ever populated for an
    /// action whose ExecuteStreaming/ExecutePreviewStreaming is set (sfc/DISM/chkdsk); empty (and
    /// hidden - see IsStreamingSupported) for every other action, which keeps using the plain
    /// PreviewOutputText/RunOutputText boxes below exactly as before.</summary>
    public ObservableCollection<string> LiveOutputLines { get; } = new();

    public bool IsStreamingSupported => SelectedAction is { ExecuteStreaming: not null } or { ExecutePreviewStreaming: not null };

    private double? _progressPercent;
    /// <summary>#977: 0-100 when the running tool's output is percentage-parseable (DISM, chkdsk);
    /// null for an indeterminate/spinner state (sfc, or before any progress line has arrived yet).</summary>
    public double? ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (SetProperty(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(HasProgressPercent));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
            }
        }
    }

    public bool HasProgressPercent => ProgressPercent is not null;

    /// <summary>XAML-friendly negation of HasProgressPercent - avoids needing a boolean-inverse
    /// value converter just for this one binding.</summary>
    public bool IsProgressIndeterminate => ProgressPercent is null;

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
            PostActionVerificationText = string.Empty;
            DeferredQueueStatusText = string.Empty;
            LiveOutputLines.Clear();
            ProgressPercent = null;
            _restorePointResolved = !NeedsRestorePointPrompt;

            OnPropertyChanged(nameof(CommandText));
            OnPropertyChanged(nameof(PreviewCommandText));
            OnPropertyChanged(nameof(RiskLevelText));
            OnPropertyChanged(nameof(RequiresRebootText));
            OnPropertyChanged(nameof(UndoableText));
            OnPropertyChanged(nameof(NeedsRestorePointPrompt));
            OnPropertyChanged(nameof(IsStreamingSupported));
            OnPropertyChanged(nameof(HasProgressPercent));
            OnPropertyChanged(nameof(SupportsDeferredQueue));

            // #974: re-evaluate this action's own preconditions fresh every time the selection
            // changes - see LoadPreconditionsAsync's remarks on why this doesn't block the UI.
            _preconditionsPassed = true; // optimistic default - see remarks above LoadPreconditionsAsync
            PreconditionBlockReason = string.Empty;
            PreconditionAdvisoryText = string.Empty;
            if (value is not null) _ = LoadPreconditionsAsync(value);

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

    // ----- #974 preconditions ---------------------------------------------------------------

    private bool _preconditionsPassed = true;

    private bool _isCheckingPreconditions;
    public bool IsCheckingPreconditions { get => _isCheckingPreconditions; private set => SetProperty(ref _isCheckingPreconditions, value); }

    private string _preconditionBlockReason = string.Empty;
    /// <summary>The exact reason Run/Preview are disabled right now - bound as both inline text and
    /// each button's ToolTip, e.g. "chkdsk C: requires an NTFS volume - C: is exFAT." Empty when
    /// nothing is blocked.</summary>
    public string PreconditionBlockReason { get => _preconditionBlockReason; private set => SetProperty(ref _preconditionBlockReason, value); }

    private string _preconditionAdvisoryText = string.Empty;
    /// <summary>Non-blocking precondition notes (currently just System Protection looking off) -
    /// shown as an informational line, never disables Run/Preview.</summary>
    public string PreconditionAdvisoryText { get => _preconditionAdvisoryText; private set => SetProperty(ref _preconditionAdvisoryText, value); }

    /// <summary>#974: evaluated fresh on every SelectedAction change. Deliberately optimistic while
    /// in flight (_preconditionsPassed starts true and only flips to false once a check actually
    /// reports a blocking failure) rather than disabling Run/Preview for the ~1-2s a precondition
    /// check can take - every blocking check this catalog declares (NTFS volume, service still
    /// present, no reboot pending) reads already-loaded ViewModel state with no real I/O, so in
    /// practice this resolves near-instantly; the one precondition kind that does make a network/
    /// process call (System Protection) is always declared non-blocking in this catalog.</summary>
    private async Task LoadPreconditionsAsync(RemediationAction action)
    {
        int myVersion = ++_preconditionCheckVersion;
        if (action.Preconditions.Count == 0) return; // nothing to check - stay optimistic, no UI flicker

        IsCheckingPreconditions = true;
        List<PreconditionCheckResult> results;
        try
        {
            results = await RemediationPreconditionService.CheckAsync(action, _services, _systemSpecs);
        }
        catch
        {
            results = new List<PreconditionCheckResult>();
        }

        if (myVersion != _preconditionCheckVersion) return; // SelectedAction changed again meanwhile - stale, drop it
        IsCheckingPreconditions = false;

        var blocked = results.Where(r => r.IsBlocked).ToList();
        _preconditionsPassed = blocked.Count == 0;
        PreconditionBlockReason = string.Join(" ", blocked.Select(b => b.Reason).Where(r => !string.IsNullOrEmpty(r)));

        var advisory = results.Where(r => !r.Precondition.Blocking && r.Reason is { Length: > 0 }).ToList();
        PreconditionAdvisoryText = string.Join(" ", advisory.Select(a => a.Reason));

        RaiseCanExecuteChanged();
    }

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

    // ----- #975 post-action verification -----------------------------------------------------

    private string _postActionVerificationText = string.Empty;
    /// <summary>"This finding cleared" / "This finding is still present" - empty when the
    /// originating finding has no RuleId to re-check (a hand-rolled, non-rule-engine finding), or
    /// before a run has finished.</summary>
    public string PostActionVerificationText { get => _postActionVerificationText; private set => SetProperty(ref _postActionVerificationText, value); }

    // ----- #979 deferred/scheduled queue -----------------------------------------------------

    public bool SupportsDeferredQueue => SelectedAction?.SupportsDeferredQueue == true;

    private DateTime _deferredDate = DateTime.Today.AddDays(1);
    /// <summary>Date half of the "queue for a specific time" option - a WPF DatePicker only
    /// selects a date, so the time-of-day half is <see cref="DeferredTimeText"/> below.</summary>
    public DateTime DeferredDate { get => _deferredDate; set => SetProperty(ref _deferredDate, value); }

    private string _deferredTimeText = DateTime.Now.AddHours(1).ToString("HH:mm");
    /// <summary>24-hour "HH:mm" text - defaults to an hour from now. Combined with
    /// <see cref="DeferredDate"/> in QueueAsync; an unparseable value falls back to that same
    /// default rather than failing the whole queue attempt.</summary>
    public string DeferredTimeText { get => _deferredTimeText; set => SetProperty(ref _deferredTimeText, value); }

    private string _deferredQueueStatusText = string.Empty;
    public string DeferredQueueStatusText { get => _deferredQueueStatusText; private set => SetProperty(ref _deferredQueueStatusText, value); }

    public AsyncRelayCommand RunPreviewCommand { get; }
    public AsyncRelayCommand CreateRestorePointCommand { get; }
    public RelayCommand SkipRestorePointCommand { get; }
    public RelayCommand OpenSystemProtectionCommand { get; }
    public AsyncRelayCommand RunActionCommand { get; }
    public RelayCommand CancelRunCommand { get; }
    public AsyncRelayCommand QueueForNextBootCommand { get; }
    public AsyncRelayCommand QueueForSpecificTimeCommand { get; }

    public RemediationReviewViewModel(HealthIssue issue, List<RemediationAction> actions,
        ServicesViewModel services, SystemSpecsViewModel systemSpecs, Func<string?, bool?>? reEvaluateFinding = null)
    {
        FindingTitle = issue.Title is { Length: > 0 } t ? t : issue.Message;
        Actions = actions;
        _services = services;
        _systemSpecs = systemSpecs;
        _reEvaluateFinding = reEvaluateFinding;
        _originatingRuleId = issue.RuleId;

        RunPreviewCommand = new AsyncRelayCommand(RunPreviewAsync, () => !IsBusy && SelectedAction is not null && _preconditionsPassed);
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, () => !IsBusy && NeedsRestorePointPrompt);
        SkipRestorePointCommand = new RelayCommand(_ =>
        {
            _restorePointResolved = true;
            RestorePointStatus = "Skipped - continuing without a restore point.";
            RaiseCanExecuteChanged();
        }, _ => !IsBusy && NeedsRestorePointPrompt);
        OpenSystemProtectionCommand = new RelayCommand(_ => RestorePointService.OpenSystemProtectionSettings());
        RunActionCommand = new AsyncRelayCommand(RunActionAsync, CanRun);
        CancelRunCommand = new RelayCommand(_ => _runCts?.Cancel(), _ => IsBusy && _runCts is not null && !_runCts.IsCancellationRequested);
        QueueForNextBootCommand = new AsyncRelayCommand(() => QueueAsync(atSpecificTime: false), CanQueue);
        QueueForSpecificTimeCommand = new AsyncRelayCommand(() => QueueAsync(atSpecificTime: true), CanQueue);

        // Selecting the first action last, so its property-changed side effects (including
        // _restorePointResolved) run against fully-constructed commands above.
        SelectedAction = actions.FirstOrDefault();
    }

    private bool CanRun() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedAction?.Execute is not null &&
        _preconditionsPassed && (!NeedsRestorePointPrompt || _restorePointResolved);

    // #980: Queue is a mutating action (it schedules a real task) - gated the same as Run, not
    // Preview.
    private bool CanQueue() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SupportsDeferredQueue && _preconditionsPassed;

    private static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    private async Task RunPreviewAsync()
    {
        var action = SelectedAction;
        if (action is null) return;

        if (action.ExecutePreview is null && action.ExecutePreviewStreaming is null)
        {
            PreviewOutputText = "No dry run available for this action.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Running preview...";
        LiveOutputLines.Clear();
        ProgressPercent = null;
        _runCts = new CancellationTokenSource();
        try
        {
            RemediationRunResult result = action.ExecutePreviewStreaming is not null
                ? await action.ExecutePreviewStreaming(_runCts.Token, line => AppendLiveLine(action, line))
                : await action.ExecutePreview!(_runCts.Token);

            PreviewOutputText = result.Output;
            StatusMessage = result.Cancelled ? "Preview cancelled." : result.Success ? "Preview finished." : "Preview reported an error - see output above.";
        }
        catch (Exception ex)
        {
            PreviewOutputText = $"Preview failed: {ex.Message}";
            StatusMessage = "Preview failed.";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
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
        PostActionVerificationText = string.Empty;
        LiveOutputLines.Clear();
        ProgressPercent = null;
        string? registryBackupPath = null;
        _runCts = new CancellationTokenSource();
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
            RemediationRunResult result = action.ExecuteStreaming is not null
                ? await action.ExecuteStreaming(_runCts.Token, line => AppendLiveLine(action, line))
                : await action.Execute(_runCts.Token);

            RunOutputText += result.Output;
            StatusMessage = result.Cancelled ? "Cancelled." : result.Success ? "Done." : "Finished with an error - see output above.";
            HasRun = true;

            ChangeJournalService.Append(BuildJournalEntry(action, result, registryBackupPath));

            // #978: track this action for the "restart pending — including from N fix(es) you
            // ran" banner, but only once it actually succeeded (a failed/cancelled run didn't do
            // anything a reboot would need to finish applying).
            if (result.Success && action.RequiresReboot)
                PendingRebootActionsService.Add(action.Title);

            // #975: re-run the originating finding's own rule (if any) fresh, so the dialog can
            // say whether this specific finding actually cleared - a real re-check, not an
            // assumption that "Success" means "fixed". Skipped for a cancelled run (nothing
            // meaningful changed to re-check).
            if (!result.Cancelled && _reEvaluateFinding is not null)
            {
                bool? stillFiring = _reEvaluateFinding(_originatingRuleId);
                PostActionVerificationText = stillFiring switch
                {
                    null => string.Empty,
                    false => "This finding cleared - the underlying condition is no longer being detected.",
                    true => "This finding is still present - the underlying condition is still being detected.",
                };
            }
        }
        catch (Exception ex)
        {
            RunOutputText += $"\nFailed: {ex.Message}";
            StatusMessage = "Failed.";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            IsBusy = false;
        }
    }

    /// <summary>#977: marshals one streamed line onto the UI thread (ObservableCollection can only
    /// be mutated from the thread that owns it) - same Application.Current.Dispatcher.Invoke
    /// pattern StorageViewModel/other ViewModels in this app already use for background-thread ->
    /// UI-thread handoff.</summary>
    private void AppendLiveLine(RemediationAction action, string line)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LiveOutputLines.Add(line);
            if (action.ParseProgressPercent?.Invoke(line) is { } pct)
                ProgressPercent = Math.Clamp(pct, 0, 100);
        });
    }

    /// <summary>#979: creates a scheduled task that runs this action's exact Command the next boot
    /// (or at a chosen time), then records it in DeferredActionService so the Changes panel's
    /// "Queued fixes" section can list/cancel it. Never invokes RemediationAction.Execute itself -
    /// the scheduled task runs the raw Command line directly, independent of this app even being
    /// open at the time.</summary>
    private async Task QueueAsync(bool atSpecificTime)
    {
        var action = SelectedAction;
        if (action is null || !action.SupportsDeferredQueue) return;

        IsBusy = true;
        DeferredQueueStatusText = "Queuing...";
        try
        {
            string taskName = $"TaskManagerPlus-Deferred-{action.Id}-{Guid.NewGuid():N}";
            DateTime whenLocal = CombineDeferredDateTime();
            var (success, error) = atSpecificTime
                ? await ScheduledTaskService.CreateOnceAsync(taskName, action.Command, whenLocal)
                : await ScheduledTaskService.CreateOnStartAsync(taskName, action.Command);

            if (!success)
            {
                DeferredQueueStatusText = $"Couldn't queue this fix: {error}";
                return;
            }

            DeferredActionService.Add(new DeferredAction
            {
                TaskName = taskName,
                ActionTitle = action.Title,
                Command = action.Command,
                ScheduleText = atSpecificTime ? $"At {whenLocal:g}" : "Next boot",
                TriggeredBy = $"Health Check finding: {FindingTitle} -> Fix this ({action.Title})",
            });

            DeferredQueueStatusText = atSpecificTime
                ? $"Queued for {whenLocal:g} - cancellable from the Troubleshoot tab's Changes panel."
                : "Queued for next boot - cancellable from the Troubleshoot tab's Changes panel.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DateTime CombineDeferredDateTime()
    {
        TimeSpan time = TimeSpan.TryParse(DeferredTimeText, out var parsed) ? parsed : DateTime.Now.AddHours(1).TimeOfDay;
        return DeferredDate.Date + time;
    }

    private ChangeJournalEntry BuildJournalEntry(RemediationAction action, RemediationRunResult result, string? registryBackupPath) => new()
    {
        Kind = action.JournalKind,
        Target = action.Title,
        ActionDescription = result.Cancelled ? $"{action.Title} (cancelled)" : action.Title,
        BeforeValue = result.BeforeValue,
        AfterValue = result.AfterValue,
        TriggeredBy = $"Health Check finding: {FindingTitle} -> Fix this ({action.Title})",
        Success = result.Success,
        IsUndoable = action.IsUndoable && result.Success,
        NotUndoableReason = result.Cancelled ? "Cancelled mid-run - nothing to reverse." : action.NotUndoableReason,
        ServiceName = action.ServiceName,
        Pid = action.Pid,
        ProcessName = action.ProcessName,
        StartupItemName = action.StartupItemName,
        StartupItemCommand = action.StartupItemCommand,
        StartupItemSource = action.StartupItemSource,
        RegistryBackupPath = registryBackupPath,
    };
}
