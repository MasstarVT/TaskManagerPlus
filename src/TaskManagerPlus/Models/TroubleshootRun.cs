using System.Collections.ObjectModel;
using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #901: one execution of a symptom's ordered <see cref="DiagnosticStep"/> list - what the
/// Troubleshoot tab's run view binds to. <see cref="VerdictText"/> is filled in once every step
/// has finished (or been skipped/timed out), summarizing the "most likely cause" a later step's
/// evidence pointed to - see each branch's remarks in TroubleshootViewModel for what that
/// means concretely. Like every heuristic finding elsewhere in this app, a verdict is a lead, not
/// a confirmed diagnosis, and is worded that way in the UI.
/// </summary>
public sealed class TroubleshootRun : ObservableObject
{
    public required string SymptomId { get; init; }
    public required string DisplayName { get; init; }

    public ObservableCollection<DiagnosticStep> Steps { get; } = new();

    public DateTime StartedAt { get; init; } = DateTime.Now;

    private DateTime? _finishedAt;
    public DateTime? FinishedAt { get => _finishedAt; set => SetProperty(ref _finishedAt, value); }

    private bool _isRunning = true;
    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }

    private string _verdictText = string.Empty;
    public string VerdictText
    {
        get => _verdictText;
        set { if (SetProperty(ref _verdictText, value)) OnPropertyChanged(nameof(HasVerdict)); }
    }

    /// <summary>True once VerdictText has something worth showing - lets the run view collapse
    /// the verdict card entirely while a run is still in progress or produced nothing actionable.</summary>
    public bool HasVerdict => !string.IsNullOrWhiteSpace(VerdictText);
}
