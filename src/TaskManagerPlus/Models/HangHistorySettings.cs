namespace TaskManagerPlus.Models;

/// <summary>Item 66: one executable's all-time "Not responding" hang history - the app already
/// tracks ProcessRow.NotRespondingSeconds live but discards it every tick once a process recovers;
/// this is what survives across app restarts (HangHistoryService persists it to
/// hang-history.json under AppPaths.SettingsDirectory) so "Explorer stops responding for 20s three
/// times a day" is visible as a trend, not just something you'd have to be watching live to catch.</summary>
public sealed class HangHistoryEntry
{
    /// <summary>Process.ProcessName shape (no ".exe" suffix) - matches ProcessRow.Name so a hang
    /// history entry can be correlated with a currently-running row without a lookup table.</summary>
    public string ExecutableName { get; set; } = string.Empty;

    public int HangCount { get; set; }

    /// <summary>The longest single "Not responding" episode recorded for this executable - a max,
    /// never overwritten by a shorter later episode.</summary>
    public int PeakDurationSeconds { get; set; }

    public DateTime LastHangTime { get; set; }
}

/// <summary>Item 66: persisted to hang-history.json via AppPaths (same shape as every other
/// settings file in this app, e.g. PollIntervalSettings) - see HangHistoryService.</summary>
public sealed class HangHistorySettings
{
    public List<HangHistoryEntry> Entries { get; set; } = new();

    public static HangHistorySettings Defaults => new();
}
