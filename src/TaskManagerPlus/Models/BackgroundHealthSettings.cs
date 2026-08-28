namespace TaskManagerPlus.Models;

/// <summary>
/// #959/#960: persisted to background-health.json (AppPaths.SettingsDirectory) - the always-on
/// background health collector's own settings, independent of LoggingSettingsService (the
/// user-started CSV logging feature's settings). Fails silently to these defaults on a missing/
/// corrupt file, same as every other settings file in this app.
/// </summary>
public sealed class BackgroundHealthSettings
{
    /// <summary>#959: default ON ("always-on is the point"), but the user must be able to turn it
    /// off entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Low-frequency by design - default 60s, configurable. BackgroundHealthCollectorService
    /// clamps this to a sane minimum at load time so a bad value can't accidentally turn this back
    /// into a high-frequency poller.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>#960: disk budget in MB for health-history.jsonl plus its rolled/gzipped segments
    /// combined - the oldest segments are pruned once the total exceeds this.</summary>
    public int BudgetMb { get; set; } = 50;
}
