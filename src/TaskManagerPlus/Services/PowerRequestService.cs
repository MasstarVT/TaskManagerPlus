using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #652: `powercfg /requests` - the live list of outstanding DISPLAY/SYSTEM/AWAYMODE/EXECUTION/
/// PERFBOOST power requests, naming whichever process/service/driver is currently holding one open.
/// This is the direct, real-time answer to "why won't my PC sleep right now" - refreshed on demand
/// only (a real subprocess call, gated behind its own button in EnergyThermalsViewModel), never on
/// the tick timer, per CLAUDE.md's on-demand-vs-polled convention (item 652 calls this out
/// explicitly). The category-header / "[TYPE] path" block layout parsed below is not a documented,
/// versioned contract (same caveat as every other powercfg text parse in this app), but has been
/// stable across Windows 10/11 releases.
/// </summary>
public static class PowerRequestService
{
    private static readonly string[] KnownCategories = { "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST", "ACTIVELOCKSCREEN" };
    private static readonly Regex SourceLineRegex = new(@"^\[(PROCESS|SERVICE|DRIVER)\]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<(List<PowerRequestEntry> Requests, string StatusText)> ReadAsync()
    {
        string output;
        int? exitCode;
        try { (output, exitCode) = await RunProcessAsync("powercfg.exe", "/requests", 15000); }
        catch (Exception ex) { return (new List<PowerRequestEntry>(), $"Couldn't run powercfg /requests: {ex.Message}"); }

        if (output.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            return (new List<PowerRequestEntry>(), "powercfg /requests needs administrator privileges (this app should already be elevated - try relaunching it).");
        if (exitCode is not 0 and not null)
            return (new List<PowerRequestEntry>(), "Couldn't read outstanding power requests.");

        var requests = Parse(output);
        string status = requests.Count == 0
            ? "No outstanding power requests - nothing is currently holding this PC awake or its display on."
            : $"{requests.Count} outstanding power request(s).";
        return (requests, status);
    }

    private static List<PowerRequestEntry> Parse(string output)
    {
        var result = new List<PowerRequestEntry>();
        var lines = output.Replace("\r\n", "\n").Split('\n');

        string currentCategory = string.Empty;
        string? pendingType = null;
        string? pendingName = null;
        var pendingReason = new List<string>();

        void Flush()
        {
            if (pendingType is null || pendingName is null) return;
            result.Add(new PowerRequestEntry
            {
                Category = currentCategory,
                SourceType = pendingType,
                SourceName = pendingName.Trim(),
                Reason = string.Join(" ", pendingReason).Trim(),
            });
            pendingType = null;
            pendingName = null;
            pendingReason.Clear();
        }

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            var categoryMatch = KnownCategories.FirstOrDefault(c => line.Equals($"{c}:", StringComparison.OrdinalIgnoreCase));
            if (categoryMatch is not null)
            {
                Flush();
                currentCategory = categoryMatch;
                continue;
            }
            if (currentCategory.Length == 0) continue; // banner/preamble text before the first category header

            if (line.Equals("None.", StringComparison.OrdinalIgnoreCase)) { Flush(); continue; }

            var sourceMatch = SourceLineRegex.Match(line);
            if (sourceMatch.Success)
            {
                Flush();
                pendingType = sourceMatch.Groups[1].Value.ToUpperInvariant();
                pendingName = sourceMatch.Groups[2].Value;
                continue;
            }

            if (pendingType is not null) pendingReason.Add(line);
        }
        Flush();
        return result;
    }

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern PowerPlanService.RunCapturedAsync
    /// established. Duplicated here rather than shared, matching this app's existing convention of
    /// each shelled-out-tool service owning its own small copy.</summary>
    private static async Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
