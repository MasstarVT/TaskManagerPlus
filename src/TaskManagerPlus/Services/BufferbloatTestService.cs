using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TaskManagerPlus.Services;

/// <summary>#580's final graded result. <see cref="ErrorMessage"/> null means both phases produced
/// at least one successful ping; non-null means the test couldn't complete (Grade is then "N/A").</summary>
public sealed record BufferbloatResult(double IdleLatencyMs, double LoadedLatencyMs, double DeltaMs, string Grade, string? ErrorMessage);

/// <summary>
/// Item #580 (suggestions.md "Throughput, bufferbloat and per-process bandwidth"): runs the same
/// ICMP ping the #501 latency monitor already uses, continuously, first idle then while the #579
/// download saturates the link, and grades the added latency A-F the way the well-known public
/// bufferbloat testers (Waveform's, DSLReports') do. This is the single most useful test in this
/// batch for "video calls stutter whenever someone else in the house starts a download" - a plain
/// throughput number never shows that, only latency measured *during* load does.
///
/// Split into two public phases (idle, then loaded) rather than one do-everything method so the
/// caller (NetworkViewModel) can update its own status text between them, the same "set status,
/// await, set status again" shape every other multi-step on-demand test in this ViewModel already
/// uses - no cross-thread progress-callback plumbing needed.
/// </summary>
public static class BufferbloatTestService
{
    private const int PingTimeoutMs = 1500;
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Phase 1: average round-trip over <paramref name="duration"/> with no other traffic
    /// deliberately generated - the baseline #580 compares the loaded phase against.</summary>
    public static async Task<double> MeasureIdleLatencyAsync(string host, TimeSpan duration, CancellationToken ct = default)
    {
        var samples = await CollectPingsAsync(host, duration, ct);
        return samples.Count > 0 ? samples.Average() : 0;
    }

    /// <summary>Phase 2: starts the #579 download and pings the same host continuously until it
    /// finishes, returning the average loaded round-trip alongside the download's own result (so
    /// the caller can also surface "how fast was the saturating download itself").</summary>
    public static async Task<(double LoadedLatencyMs, SpeedTestResult Download)> MeasureLoadedLatencyAsync(
        string pingHost, string downloadUrl, CancellationToken ct = default)
    {
        var downloadTask = SpeedTestService.RunDownloadAsync(downloadUrl, ct);
        var pingSamples = new List<double>();

        using var ping = new Ping();
        while (!downloadTask.IsCompleted)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var reply = await ping.SendPingAsync(pingHost, PingTimeoutMs);
                if (reply.Status == IPStatus.Success) pingSamples.Add(reply.RoundtripTime);
            }
            catch
            {
                // one failed ping mid-download shouldn't abort the whole test
            }
            try { await Task.Delay(PingInterval, ct); } catch (OperationCanceledException) { break; }
        }

        var download = await downloadTask;
        double loaded = pingSamples.Count > 0 ? pingSamples.Average() : 0;
        return (loaded, download);
    }

    private static async Task<List<double>> CollectPingsAsync(string host, TimeSpan duration, CancellationToken ct)
    {
        var samples = new List<double>();
        using var ping = new Ping();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var reply = await ping.SendPingAsync(host, PingTimeoutMs);
                if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
            }
            catch
            {
                // one failed ping shouldn't abort the whole measurement window
            }
            try { await Task.Delay(PingInterval, ct); } catch (OperationCanceledException) { break; }
        }
        return samples;
    }

    /// <summary>Grade bands loosely mirror the well-known public bufferbloat testers' A-F scale:
    /// near-zero added latency under load is an A, a few hundred added ms is an F. Informational
    /// thresholds this app picked to match that same spirit, not a certified/standardized scale -
    /// "quick flag, not a verdict" applies here as much as anywhere else in this app's
    /// heuristics.</summary>
    public static string GradeFor(double deltaMs) => deltaMs switch
    {
        <= 5 => "A+",
        <= 30 => "A",
        <= 60 => "B",
        <= 200 => "C",
        <= 400 => "D",
        _ => "F",
    };
}
