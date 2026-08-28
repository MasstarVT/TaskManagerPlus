namespace TaskManagerPlus.Models;

/// <summary>Persisted remote-monitoring preference (#101) - off by default, same as every other
/// opt-in feature toggle in this app. The port is fixed rather than configurable in the UI to
/// keep the "what did I expose" story simple (one well-known number to check/firewall against).</summary>
public sealed class RemoteMonitorSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5157;

    /// <summary>Round 12, #97: optional shared-token query-string check - a minimal opt-in step
    /// up from the endpoint's original fully-open-on-the-LAN design. Empty/null (the default)
    /// means "unchanged from before this feature existed" - every request is served with no
    /// check at all. When set, a request must carry a matching `?token=...` query-string
    /// parameter or gets a 401 with no body - see RemoteMonitorService.HandleAsync. This is
    /// still not real authentication (a token in a plain HTTP query string is visible to
    /// anything on the LAN path, and HttpListener itself has no TLS here) - it's documented in
    /// the UI as exactly that: a minimal step up, not a security boundary.</summary>
    public string? Token { get; set; }

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
