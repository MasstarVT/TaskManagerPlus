namespace TaskManagerPlus.Models;

/// <summary>
/// User-configurable threshold-alert settings (#72) - a toast fires once when a metric crosses
/// above its threshold, leveraging the same already-polled data the Summary tab's Health Check
/// card and the logging infrastructure both already read, no new sampling. Persisted the same way
/// ThemeColors is (plain JSON, defaults on a missing/corrupt file).
/// </summary>
public sealed class AlertThresholds
{
    public bool CpuEnabled { get; set; }
    public double CpuPercent { get; set; } = 90;

    public bool MemoryEnabled { get; set; }
    public double MemoryPercent { get; set; } = 90;

    public bool TempEnabled { get; set; }
    public double TempC { get; set; } = 90;

    public static AlertThresholds Defaults => new();
}
