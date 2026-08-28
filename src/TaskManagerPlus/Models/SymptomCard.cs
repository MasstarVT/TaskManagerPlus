namespace TaskManagerPlus.Models;

/// <summary>
/// #901: one plain-English symptom on the Troubleshoot tab's landing grid - deliberately worded
/// the way a non-technical user would describe the problem ("My PC is slow"), not the underlying
/// subsystem, since the whole point of this tab is not making the user guess which of the other
/// twelve tabs to open first.
/// </summary>
public sealed class SymptomCard
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>False for a card shown on the landing grid whose branch isn't wired up yet (a
    /// later round adds it - see TroubleshootViewModel's remarks) - rendered dimmed and
    /// non-clickable rather than hidden, so the full intended symptom list is still visible.</summary>
    public bool IsAvailable { get; init; } = true;
}
