namespace TaskManagerPlus.Models;

/// <summary>Round: #406 pinned leak-watch list - persisted set of image names the user asked to
/// watch for leaks, kept by name (not PID, which is meaningless across a restart) so the watch
/// picks the process back up automatically the next time it's running.</summary>
public sealed class LeakWatchSettings
{
    public List<string> WatchedImageNames { get; set; } = new();

    public static LeakWatchSettings Defaults => new();
}
