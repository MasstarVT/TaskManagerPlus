using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #291: lists active BITS (Background Intelligent Transfer Service) jobs by shelling out to
/// `bitsadmin.exe /list /allusers /verbose` and parsing its text output - the same "known Windows
/// tool over raw interop" tradeoff every other shelled-out reader in this app takes (there is a
/// BITS COM API, but no service in this app takes a COM interop dependency anywhere else). Exact
/// field indentation/spacing in bitsadmin's output isn't a documented, versioned contract, so this
/// parses loosely (a "LABEL: value" line scan per job block, the same tolerant style
/// ScheduledTaskService/WifiScanStormService already use for other CLI tools) rather than assuming
/// fixed column positions. On-demand-refreshed-plus-startup-load per CLAUDE.md's tiering for a
/// cheap-ish shell-out.
/// </summary>
public static class BitsTransferService
{
    private static readonly Regex BytesRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    public static async Task<(bool Success, string Message, List<BitsTransferRow> Rows)> ListAsync()
    {
        string output;
        int? exitCode;
        try
        {
            (output, exitCode) = await RunCapturedAsync("bitsadmin.exe", "/list /allusers /verbose");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't run bitsadmin.exe: {ex.Message}", new List<BitsTransferRow>());
        }

        if (exitCode is null)
            return (false, "bitsadmin.exe timed out.", new List<BitsTransferRow>());

        var rows = ParseVerboseOutput(output);

        // bitsadmin returns 0 whether or not there are jobs - an empty parse just means "no active
        // BITS transfers right now", the common case, not a failure.
        string message = rows.Count == 0
            ? "No active BITS transfers found."
            : $"{rows.Count} active BITS transfer(s).";
        return (true, message, rows);
    }

    private static List<BitsTransferRow> ParseVerboseOutput(string output)
    {
        var rows = new List<BitsTransferRow>();
        string? displayName = null, jobId = null, typeText = null, stateText = null, owner = null, priorityText = null;
        long bytesTransferred = 0, bytesTotal = 0;
        bool haveJob = false;

        void Flush()
        {
            if (!haveJob) return;
            rows.Add(new BitsTransferRow
            {
                DisplayName = displayName ?? "(unnamed)",
                JobId = jobId ?? string.Empty,
                TypeText = typeText ?? "Unknown",
                StateText = stateText ?? "Unknown",
                Owner = owner ?? string.Empty,
                PriorityText = priorityText ?? "Unknown",
                BytesTransferred = bytesTransferred,
                BytesTotal = bytesTotal,
            });
            displayName = jobId = typeText = stateText = owner = priorityText = null;
            bytesTransferred = bytesTotal = 0;
            haveJob = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("DISPLAY:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                haveJob = true;
                string rest = line["DISPLAY:".Length..].Trim();
                displayName = rest.Trim('\'', '"');
                continue;
            }
            if (!haveJob) continue;

            if (StartsWithLabel(line, "JOB ID", out string v1)) jobId = v1;
            else if (StartsWithLabel(line, "TYPE", out string v2)) typeText = v2;
            else if (StartsWithLabel(line, "STATE", out string v3)) stateText = v3;
            else if (StartsWithLabel(line, "OWNER", out string v4)) owner = v4;
            else if (StartsWithLabel(line, "PRIORITY", out string v5)) priorityText = v5;
            else if (line.Contains("BYTES", StringComparison.OrdinalIgnoreCase))
            {
                var m = BytesRegex.Match(line);
                if (m.Success)
                {
                    long.TryParse(m.Groups[1].Value, out bytesTransferred);
                    long.TryParse(m.Groups[2].Value, out bytesTotal);
                }
            }
        }
        Flush();
        return rows;
    }

    private static bool StartsWithLabel(string line, string label, out string value)
    {
        int idx = line.IndexOf(':');
        if (idx < 0 || !line[..idx].Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }
        value = line[(idx + 1)..].Trim();
        return true;
    }

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - the
    /// same concurrent-read/bounded-wait/kill-on-timeout pattern ScheduledTaskService.RunCapturedAsync
    /// already establishes.</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
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
