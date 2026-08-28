namespace TaskManagerPlus.Services;

/// <summary>
/// #952/#957: tracks whether CPU% has read at or below the idle threshold for the last N
/// consecutive samples fed to it via <see cref="Record"/> - the "sustained idle for several
/// minutes" rolling check both the automatic weekly baseline capture's trigger condition (#952)
/// and every baseline capture's idle-gating label (#957) share.
///
/// Not a DispatcherTimer itself - the owning ViewModel (BaselineViewModel) calls Record from
/// whatever cadence it already polls on, mirroring SummaryViewModel.CaptureIdleCpuTempOrNull's
/// existing single-sample idle gate (same threshold), just extended to require several minutes of
/// sustained readings rather than one instantaneous one.
/// </summary>
public sealed class IdleRollingTracker
{
    /// <summary>Same threshold SummaryViewModel.CaptureIdleCpuTempOrNull already uses for its own
    /// (single-sample) idle gate.</summary>
    public const double IdleCpuPercentThreshold = 15.0;

    private readonly int _samplesRequired;
    private int _consecutiveIdleSamples;

    /// <param name="samplesRequired">How many consecutive Record calls at/under the threshold count
    /// as "sustained idle" - paired with the caller's own tick interval to express a time window
    /// (e.g. 30 samples at a 10s tick = 5 minutes).</param>
    public IdleRollingTracker(int samplesRequired) => _samplesRequired = Math.Max(1, samplesRequired);

    public bool IsSustainedIdle => _consecutiveIdleSamples >= _samplesRequired;

    public void Record(double cpuPercent)
    {
        if (cpuPercent <= IdleCpuPercentThreshold) _consecutiveIdleSamples++;
        else _consecutiveIdleSamples = 0;
    }
}
