namespace TaskManagerPlus.Models;

/// <summary>Result of AutorunsBaselineService.Diff() - #803. HasBaseline is false when no
/// baseline snapshot exists yet (or it was unreadable), in which case the three lists are empty
/// and callers should prompt the user to "Save baseline" first rather than showing a misleading
/// empty diff.</summary>
public sealed class AutorunsDiffResult
{
    public bool HasBaseline { get; set; }
    public List<AutorunEntry> Added { get; } = new();
    public List<AutorunEntry> Removed { get; } = new();
    public List<AutorunEntry> Changed { get; } = new();
}
