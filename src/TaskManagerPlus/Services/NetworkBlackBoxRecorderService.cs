using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One ring-buffer sample (#600) - whatever the Network tab's own existing 15s
/// connectivity tick already knows on that cycle, not a second probe of its own.</summary>
public sealed record BlackBoxSample(
    DateTime TimeUtc, double? GatewayLatencyMs, double LossPercent, bool LinkUp, int? RssiDbm,
    long RxErrorsDelta, long TxErrorsDelta, long RxDiscardsDelta, long TxDiscardsDelta);

/// <summary>One incident the recorder actually wrote to disk (#600) - shown in the Network tab's
/// incident list. <see cref="FilePath"/> may point at a file that's since been moved/deleted by the
/// user outside the app; the view degrades that to a disabled "Open" action rather than throwing.</summary>
public sealed record BlackBoxIncident(DateTime TriggeredAtUtc, string TriggerReason, string FilePath, int SampleCount);

/// <summary>
/// Item #600: a bounded in-memory ring buffer of latency/loss/link-state/RSSI/adapter-error-counter
/// samples that, while toggled on, costs nothing but keeping the last few hundred small structs
/// around - the same "cheap bookkeeping, safe to leave running" tradeoff CLAUDE.md calls out for this
/// item specifically (unlike the app's actual on-demand scans, which are gated behind an explicit
/// button because they're genuinely expensive). The buffer only ever touches disk at the one moment
/// it's actually useful: when <see cref="AddSample"/> observes a disconnect/link-down transition,
/// the whole ring is dumped to a timestamped file under <c>AppPaths.SettingsDirectory\NetworkIncidents</c>
/// - "what were the last few minutes of network health right before it dropped", instead of finding
/// out too late that nothing was being recorded.
///
/// Fed from NetworkViewModel's own existing 15s CheckConnectivityAsync tick (already gathering
/// gateway reachability, RSSI and adapter error deltas for other cards on the same cycle) rather than
/// a dedicated timer of its own - the same "reuse an existing tick" precedent NetworkHistoryService's
/// RecordSample already established for this tab.
/// </summary>
public sealed class NetworkBlackBoxRecorderService
{
    private const int RingCapacity = 240; // ~1 hour of samples at the tab's own 15s tick
    private const int MaxIncidentHistory = 50;

    private readonly object _lock = new();
    private readonly Queue<BlackBoxSample> _ring = new();
    private bool? _lastLinkUp;

    public bool IsRecording { get; private set; }

    public void Start() => IsRecording = true;

    public void Stop()
    {
        IsRecording = false;
        lock (_lock) { _ring.Clear(); _lastLinkUp = null; }
    }

    /// <summary>Adds one sample; returns the incident just written, if this sample's link-state
    /// transition (up -&gt; down) triggered a dump - null on every other call, including every call
    /// while <see cref="IsRecording"/> is false.</summary>
    public BlackBoxIncident? AddSample(BlackBoxSample sample)
    {
        if (!IsRecording) return null;

        List<BlackBoxSample>? snapshotToWrite = null;
        lock (_lock)
        {
            _ring.Enqueue(sample);
            while (_ring.Count > RingCapacity) _ring.Dequeue();

            bool wasUp = _lastLinkUp ?? true;
            if (_lastLinkUp is not null && wasUp && !sample.LinkUp)
                snapshotToWrite = _ring.ToList();

            _lastLinkUp = sample.LinkUp;
        }

        if (snapshotToWrite is null) return null;

        try
        {
            return WriteIncident(snapshotToWrite, "Link/gateway went down");
        }
        catch
        {
            // Best-effort - a failed write shouldn't disrupt the connectivity tick it rides on;
            // the ring buffer itself is unaffected, so the next transition gets another chance.
            return null;
        }
    }

    private static BlackBoxIncident WriteIncident(List<BlackBoxSample> samples, string reason)
    {
        var dir = AppPaths.GetPath("NetworkIncidents");
        Directory.CreateDirectory(dir);

        string fileName = $"incident-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
        string path = Path.Combine(dir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("TimeUtc,GatewayLatencyMs,LossPercent,LinkUp,RssiDbm,RxErrorsDelta,TxErrorsDelta,RxDiscardsDelta,TxDiscardsDelta");
        foreach (var s in samples)
        {
            sb.AppendLine(string.Join(",",
                s.TimeUtc.ToString("o", CultureInfo.InvariantCulture),
                s.GatewayLatencyMs?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty,
                s.LossPercent.ToString("0.#", CultureInfo.InvariantCulture),
                s.LinkUp,
                s.RssiDbm?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s.RxErrorsDelta, s.TxErrorsDelta, s.RxDiscardsDelta, s.TxDiscardsDelta));
        }
        File.WriteAllText(path, sb.ToString());

        var incident = new BlackBoxIncident(DateTime.UtcNow, reason, path, samples.Count);
        AppendToIndex(incident);
        return incident;
    }

    private static string IndexPath => AppPaths.GetPath("network-blackbox-incidents.json");

    private static void AppendToIndex(BlackBoxIncident incident)
    {
        try
        {
            var list = LoadIndexRaw();
            list.Add(incident);
            if (list.Count > MaxIncidentHistory) list = list.Skip(list.Count - MaxIncidentHistory).ToList();

            var dir = Path.GetDirectoryName(IndexPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - the .csv file itself was already written successfully either way; a
            // failed index update just means it might not show up in the persisted list after a
            // restart.
        }
    }

    /// <summary>Loads the persisted incident index (most recent last) - called once when the Network
    /// tab starts up, so incidents from a previous session's recording still show in the list.</summary>
    public static List<BlackBoxIncident> LoadIncidentHistory() => LoadIndexRaw();

    private static List<BlackBoxIncident> LoadIndexRaw()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var json = File.ReadAllText(IndexPath);
                var list = JsonSerializer.Deserialize<List<BlackBoxIncident>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt/unreadable index - start fresh rather than blocking on it.
        }
        return new List<BlackBoxIncident>();
    }
}
