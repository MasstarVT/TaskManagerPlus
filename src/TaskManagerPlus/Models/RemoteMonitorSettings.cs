namespace TaskManagerPlus.Models;

/// <summary>Persisted remote-monitoring preference (#101) - off by default, same as every other
/// opt-in feature toggle in this app. The port is fixed rather than configurable in the UI to
/// keep the "what did I expose" story simple (one well-known number to check/firewall against).</summary>
public sealed class RemoteMonitorSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5157;

    public static RemoteMonitorSettings Defaults => new();
}

/// <summary>One point-in-time metrics snapshot served as JSON by RemoteMonitorService (#101) -
/// deliberately read-only and a small, fixed subset of what the app tracks (no process list, no
/// file paths, nothing an attacker on the LAN could use for more than "is this PC busy").</summary>
public sealed class RemoteMetricsSnapshot
{
    public string MachineName { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; }
    public double CpuPercent { get; init; }
    public double CpuTempC { get; init; }
    public bool HasCpuTemp { get; init; }
    public double RamPercent { get; init; }
    public double DiskPercent { get; init; }
    public double NetworkReceiveBps { get; init; }
    public double NetworkSendBps { get; init; }
    public string Uptime { get; init; } = string.Empty;
}
