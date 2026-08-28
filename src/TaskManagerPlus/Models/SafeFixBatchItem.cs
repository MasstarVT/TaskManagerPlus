using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#976: per-item status in the "Run safe fixes" batch runner.</summary>
public enum SafeFixItemStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>One finding queued in the safe-fixes batch - the HealthIssue it came from plus the one
/// RemediationAction that will actually run for it (SafeFixBatchViewModel.Refresh already filtered
/// this down to only Low-risk, undoable, fully-resolved actions before this row exists at all).</summary>
public sealed class SafeFixBatchItem : ObservableObject
{
    public HealthIssue Issue { get; }
    public RemediationAction Action { get; }

    public string Title => Action.Title;

    private SafeFixItemStatus _status = SafeFixItemStatus.Pending;
    public SafeFixItemStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsSucceeded));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    /// <summary>Plain string mirror of Status - simpler for XAML DataTriggers to bind against than
    /// the enum directly, matching how every other status-word property in this app already
    /// exposes itself as text (RemediationReviewViewModel.RiskLevelText, and friends).</summary>
    public string StatusText => Status switch
    {
        SafeFixItemStatus.Pending => "Pending",
        SafeFixItemStatus.Running => "Running…",
        SafeFixItemStatus.Succeeded => "Succeeded",
        SafeFixItemStatus.Failed => "Failed",
        _ => Status.ToString(),
    };

    public bool IsRunning => Status == SafeFixItemStatus.Running;
    public bool IsSucceeded => Status == SafeFixItemStatus.Succeeded;
    public bool IsFailed => Status == SafeFixItemStatus.Failed;

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }

    public SafeFixBatchItem(HealthIssue issue, RemediationAction action)
    {
        Issue = issue;
        Action = action;
    }
}
