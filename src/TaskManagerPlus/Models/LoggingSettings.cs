namespace TaskManagerPlus.Models;

/// <summary>Persisted logging preferences (#95) - auto-start a rolling in-memory buffer on
/// launch, so a crash can be diagnosed retroactively even if the user never clicked "Start
/// Logging" beforehand. Defaults off, same as every other opt-in feature toggle in this app.</summary>
public sealed class LoggingSettings
{
    public bool AutoStartRollingBuffer { get; set; }
    public int RollingBufferMinutes { get; set; } = 15;

    public static LoggingSettings Defaults => new();
}
