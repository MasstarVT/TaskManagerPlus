namespace TaskManagerPlus.Models;

/// <summary>#947: one user-authored dated annotation on the Timeline panel - persisted separately
/// from every other lane's source data, since Notes is the one lane the user writes to directly
/// rather than one this app derives from a system log/WMI class. Plain JSON list, same
/// missing/corrupt-file-degrades-to-empty shape every other settings file in this app uses.</summary>
public sealed class TimelineNoteEntry
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;
}
