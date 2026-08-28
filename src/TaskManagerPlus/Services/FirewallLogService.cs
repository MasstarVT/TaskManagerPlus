using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One DROP entry from pfirewall.log (#568).</summary>
public sealed record FirewallLogEntry(
    DateTime Time, string Direction, string Protocol, string SourceIp, string DestIp, string SourcePort, string DestPort);

/// <summary><see cref="Entries"/> is empty on any failure - <see cref="FileFound"/>/<see cref="Message"/>
/// tell the caller why (no log file yet vs. a read error vs. genuinely nothing dropped), so the view
/// never has to guess at an empty list's meaning.</summary>
public sealed record FirewallLogReadResult(List<FirewallLogEntry> Entries, bool FileFound, string? Message);

/// <summary>
/// Item #568 (suggestions.md "Firewall rules and blocked connections"): reads
/// %SystemRoot%\System32\LogFiles\Firewall\pfirewall.log - the only first-party record of what
/// Windows Firewall actually dropped (the rule browser and profile status above only describe the
/// *configuration*, not what it did). Off by default, so the file frequently doesn't exist at all;
/// that's treated as "logging not enabled" rather than an error. The W3C-extended-log-format
/// #Fields header (a stable, documented format that hasn't changed across Windows versions) is
/// parsed to build a column-name -&gt; index map dynamically rather than hardcoding field positions,
/// so field reordering on some future Windows build degrades to missing columns instead of silently
/// misreading them.
/// </summary>
public static class FirewallLogService
{
    private static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"LogFiles\Firewall\pfirewall.log");

    private const int TimeoutMs = 15000;

    public static async Task<FirewallLogReadResult> ReadDropEntriesAsync(int maxEntries = 2000)
    {
        string path = DefaultLogPath;
        try
        {
            if (!File.Exists(path))
                return new FirewallLogReadResult(new(), false,
                    "The firewall log file doesn't exist yet - dropped-connection logging is probably disabled. Use \"Enable dropped-packet logging\" below, then check back after some blocked traffic occurs.");

            var lines = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null) lines.Add(line);
            }

            string? fieldsLine = lines.FirstOrDefault(l => l.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase));
            if (fieldsLine is null)
                return new FirewallLogReadResult(new(), true, "The log file exists but its #Fields header is missing or unrecognized - can't tell which column is which.");

            var fields = fieldsLine["#Fields:".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fields.Length; i++) index[fields[i]] = i;

            var entries = new List<FirewallLogEntry>();
            foreach (var line in lines)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                string Get(string field) => index.TryGetValue(field, out int i) && i < tokens.Length ? tokens[i] : "-";

                if (!Get("action").Equals("DROP", StringComparison.OrdinalIgnoreCase)) continue;

                DateTime time = DateTime.TryParse($"{Get("date")} {Get("time")}", out var t) ? t : DateTime.MinValue;
                entries.Add(new FirewallLogEntry(time, Get("path"), Get("protocol"), Get("src-ip"), Get("dst-ip"), Get("src-port"), Get("dst-port")));
            }

            entries.Reverse(); // the log file is append-only chronological - most recent first is more useful here
            if (entries.Count > maxEntries) entries = entries.Take(maxEntries).ToList();

            return new FirewallLogReadResult(entries, true, entries.Count == 0 ? "No DROP entries found in the current log." : null);
        }
        catch (UnauthorizedAccessException)
        {
            return new FirewallLogReadResult(new(), true, "Access denied reading the firewall log file.");
        }
        catch (Exception ex)
        {
            return new FirewallLogReadResult(new(), false, $"Couldn't read the firewall log: {ex.Message}");
        }
    }

    /// <summary>A quick "is it even on" read - reused by the ViewModel to decide whether to show
    /// the Enable button as already-satisfied, without needing to touch the log file itself.
    /// Crude (checks whether *any* profile reports LogDroppedConnections as Enable) since the log
    /// file itself doesn't distinguish per-profile, and this app enables/disables all profiles
    /// together anyway (see EnableDroppedConnectionLoggingAsync below).</summary>
    public static async Task<bool?> IsLoggingEnabledAsync()
    {
        string output = await RunNetshAsync("advfirewall show allprofiles");
        if (string.IsNullOrWhiteSpace(output)) return null;
        return Regex.IsMatch(output, @"LogDroppedConnections\s+Enable", RegexOptions.IgnoreCase);
    }

    /// <summary>#568's own action: `netsh advfirewall set allprofiles logging droppedconnections
    /// enable` - always called from behind an explicit confirm dialog in the ViewModel (this
    /// service has no confirmation of its own, by design, matching AdapterRestartService's own
    /// "the caller owns the prompt" convention) since it's a persistent firewall configuration
    /// change, not a one-off diagnostic read.</summary>
    public static async Task<string> EnableDroppedConnectionLoggingAsync()
    {
        string output = await RunNetshAsync("advfirewall set allprofiles logging droppedconnections enable");
        return output.Length == 0 ? "Dropped-connection logging enabled." : output;
    }

    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start netsh.exe.";

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "netsh.exe timed out.";
            }

            return ((await outputTask) + (await errorTask)).Trim();
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }
}
