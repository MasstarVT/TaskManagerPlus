using System.Diagnostics;
using System.Net.Http;

namespace TaskManagerPlus.Services;

/// <summary>One #579 speed-test run's result - either direction. <see cref="ErrorMessage"/> null
/// means the test completed; non-null means it failed outright (no internet, endpoint unreachable,
/// timed out) and <see cref="Mbps"/>/<see cref="BytesTransferred"/> are meaningless zeros.</summary>
public sealed record SpeedTestResult(DateTime TimestampUtc, string Direction, string EndpointUrl, double Mbps, long BytesTransferred, double DurationSeconds, string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}

/// <summary>
/// Item #579 (suggestions.md "Throughput, bufferbloat and per-process bandwidth"): a built-in
/// single-stream HTTP download/upload speed test - the same shape every consumer "speed test"
/// site uses under the hood, deliberately NOT presented as a certified/ISP-comparable figure. A
/// single TCP stream is throttled by TCP's own congestion window ramp-up and by one connection's
/// share of any per-flow QoS shaping far more than a real multi-stream ISP test would be, so this
/// reads as a rough *floor* on actual link capacity, never an exact number - the UI carries that
/// caveat verbatim next to every result.
///
/// Endpoint is user-selectable (a plain URL text field on the Network tab) rather than hardcoded,
/// since there's no single "the" canonical speed-test endpoint this app can bundle a license/API
/// key for - defaults to Cloudflare's own public, no-auth speed-test endpoints
/// (speed.cloudflare.com), which are documented and widely used by other open-source speed-test
/// tools for exactly this purpose.
/// </summary>
public static class SpeedTestService
{
    public const string DefaultDownloadUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
    public const string DefaultUploadUrl = "https://speed.cloudflare.com/__up";

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>Streams a GET response to completion, timing sustained throughput. Reads into a
    /// reused buffer rather than buffering the whole body, so a large payload doesn't balloon this
    /// app's own working set while the test runs.</summary>
    public static async Task<SpeedTestResult> RunDownloadAsync(string url, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var sw = Stopwatch.StartNew();
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                total += read;
            sw.Stop();

            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            double mbps = total * 8.0 / seconds / 1_000_000.0;
            return new SpeedTestResult(startedAt, "Download", url, mbps, total, seconds, null);
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(startedAt, "Download", url, 0, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(startedAt, "Download", url, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>POSTs a fixed-size random payload (random, not zeroed, so a proxy/endpoint that
    /// transparently compresses an all-zero body can't understate the real upload time), timing
    /// how long the server takes to accept the whole body.</summary>
    public static async Task<SpeedTestResult> RunUploadAsync(string url, int payloadMb = 10, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var bytes = new byte[Math.Max(1, payloadMb) * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            using var content = new ByteArrayContent(bytes);

            var sw = Stopwatch.StartNew();
            using var response = await Http.PostAsync(url, content, ct);
            sw.Stop();

            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            double mbps = bytes.Length * 8.0 / seconds / 1_000_000.0;
            string? error = response.IsSuccessStatusCode ? null : $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}";
            return new SpeedTestResult(startedAt, "Upload", url, mbps, bytes.Length, seconds, error);
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(startedAt, "Upload", url, 0, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(startedAt, "Upload", url, 0, 0, 0, ex.Message);
        }
    }
}
