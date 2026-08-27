namespace TaskManagerPlus.Models;

/// <summary>Persisted logging preferences (#95) - auto-start a rolling in-memory buffer on
/// launch, so a crash can be diagnosed retroactively even if the user never clicked "Start
/// Logging" beforehand. Defaults off, same as every other opt-in feature toggle in this app.</summary>
public sealed class LoggingSettings
{
    public bool AutoStartRollingBuffer { get; set; }
    public int RollingBufferMinutes { get; set; } = 15;

    /// <summary>Round 11, #76: how often a manual (or rolling-buffer) logging row is written, in
    /// seconds. Defaults to the original fixed 1s cadence; 5s/10s trade sample resolution for a
    /// smaller file and less disk I/O on a long unattended session.</summary>
    public int SampleIntervalSeconds { get; set; } = 1;

    /// <summary>Round 11, #77: automatically delete rotated "-partN" log files (and their gzipped
    /// copies, #74) older than AutoCleanupDays - never the currently-active file - so an
    /// unattended logging habit doesn't slowly fill the Logs folder. On by default, same as most
    /// of this app's other "quietly do the sensible thing" toggles.</summary>
    public bool AutoCleanupEnabled { get; set; } = true;
    public int AutoCleanupDays { get; set; } = 30;

    public static LoggingSettings Defaults => new();
}
