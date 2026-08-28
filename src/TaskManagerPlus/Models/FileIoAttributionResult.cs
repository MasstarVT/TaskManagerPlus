namespace TaskManagerPlus.Models;

/// <summary>Round 18, #368: one aggregated "top 20" row (either a file path or a "Name (PID N)"
/// process label, depending on which list it's in) from a time-boxed ETW capture window - shape is
/// in place for when a full ETW session is wired up; see FileIoAttributionService's remarks for why
/// this chunk ships the capture path as a labeled stub rather than a live session.</summary>
public sealed class FileIoAttributionEntry
{
    public string Key { get; init; } = string.Empty;
    public long BytesRead { get; init; }
    public long BytesWritten { get; init; }
    public long TotalBytes => BytesRead + BytesWritten;
}

public sealed class FileIoAttributionResult
{
    public bool Available { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public List<FileIoAttributionEntry> TopFiles { get; init; } = new();
    public List<FileIoAttributionEntry> TopProcesses { get; init; } = new();
}
