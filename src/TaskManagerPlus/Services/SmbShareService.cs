using System.Diagnostics;
using System.IO;
using System.Management;
using TaskManagerPlus.Common;

namespace TaskManagerPlus.Services;

/// <summary>One connected SMB client share's latency/queue/throughput snapshot (#586).</summary>
public sealed record SmbShareLatency(
    string ShareName, double AvgReadLatencyMs, double AvgWriteLatencyMs,
    double AvgDataQueueLength, double DataBytesPerSec, bool LooksSlow);

/// <summary>One `HKCU\Network` mapped-drive entry, or one live `net use` connection, merged into a
/// single row by drive letter (#587). <see cref="IsReachable"/> is null until #587's on-demand
/// reachability test has actually run for this row. Extends ObservableObject (rather than a plain
/// mutable class) the same way AdapterHealthRow does above - #587's reachability test mutates
/// ConnectionState/IsReachable/ReachabilityText on an existing row well after construction, and the
/// bound DataGrid cell needs to actually repaint when that happens.</summary>
public sealed class MappedDriveInfo : ObservableObject
{
    public string DriveLetter { get; init; } = string.Empty;
    public string RemotePath { get; init; } = string.Empty;
    public bool ReconnectAtLogon { get; init; }

    private string _connectionState = "Unknown"; // from `net use`: OK / Disconnected / Unavailable / ...
    public string ConnectionState { get => _connectionState; set => SetProperty(ref _connectionState, value); }

    private bool? _isReachable;
    public bool? IsReachable { get => _isReachable; set => SetProperty(ref _isReachable, value); }

    private string _reachabilityText = "Not tested";
    public string ReachabilityText { get => _reachabilityText; set => SetProperty(ref _reachabilityText, value); }
}

/// <summary>One `MSFT_SmbConnection` row - the negotiated dialect/signing/encryption Windows
/// actually settled on with one server, per connection (#588).</summary>
public sealed record SmbConnectionInfo(
    string ServerName, string ShareName, string Dialect, bool Signed, bool Encrypted,
    bool IsSmb1, string? SigningCaveat);

/// <summary>
/// Items #586/#587/#588 (suggestions.md "SMB and network drives"): reads for the new "Network
/// drives" card - "the PC is slow" very often means one unresponsive network share silently
/// stalling Explorer, a file dialog, or a save, and none of that shows up anywhere else in this
/// app.
/// </summary>
public static class SmbShareService
{
    // #586: Win32_PerfRawData_SMBClientShares' own raw counters need a second sample plus the
    // Timestamp_PerfTime/Frequency_PerfTime fields to convert correctly - the *formatted*
    // counterpart (Win32_PerfFormattedData_SMBClientShares_SMBClientShares) is the same
    // "SMB Client Shares" perfmon object with Windows' own perflib already doing that conversion,
    // the same "known Windows tool/API over hand-rolled raw-counter math" tradeoff CLAUDE.md's
    // Cross-cutting conventions favor - avoids this app guessing at a raw-to-formatted conversion
    // it has no live multi-share machine to verify against.
    private const string SmbShareCountersQuery = "SELECT * FROM Win32_PerfFormattedData_SMBClientShares_SMBClientShares";

    // Latency this app flags as "slow enough to look into" - a couple hundred ms average read/write
    // latency on a LAN share is already well outside "feels instant" territory. Informational only.
    private const double SlowLatencyMsThreshold = 200.0;

    /// <summary>#586: per-connected-share latency/queue-depth/throughput, for charting the slow
    /// ones. Empty (not an exception) when nothing's currently connected via SMB, or the perf
    /// counters aren't available on this machine.</summary>
    public static List<SmbShareLatency> ReadShareLatencies()
    {
        var results = new List<SmbShareLatency>();
        try
        {
            using var searcher = new ManagementObjectSearcher(SmbShareCountersQuery);
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? string.Empty).Trim();
                if (name.Length == 0 || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

                // AvgsecPerRead/Write are reported in seconds by perflib's own "average timer"
                // counter type - converted to ms here for a readable UI figure.
                double readMs = ReadDoubleProperty(mo, "AvgsecPerRead") * 1000.0;
                double writeMs = ReadDoubleProperty(mo, "AvgsecPerWrite") * 1000.0;
                double queueLength = ReadDoubleProperty(mo, "AvgDataQueueLength");
                double dataBytesPerSec = ReadDoubleProperty(mo, "DataBytesPersec");
                bool looksSlow = readMs > SlowLatencyMsThreshold || writeMs > SlowLatencyMsThreshold;

                results.Add(new SmbShareLatency(name, readMs, writeMs, queueLength, dataBytesPerSec, looksSlow));
            }
        }
        catch
        {
            // No SMB client shares perf object on this machine, or WMI failed outright - the view
            // just shows an empty list rather than a guessed figure.
        }
        return results.OrderByDescending(r => Math.Max(r.AvgReadLatencyMs, r.AvgWriteLatencyMs)).ToList();
    }

    private static double ReadDoubleProperty(ManagementObject mo, string name)
    {
        try
        {
            var value = mo[name];
            return value is null ? 0 : Convert.ToDouble(value);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>#587: merges `HKCU\Network`'s persisted mappings with live `net use` output by
    /// drive letter - the registry has the "reconnect at logon" intent even for a drive that
    /// isn't currently connected, `net use` has the live connection state; together they're the
    /// full picture either alone is missing.</summary>
    public static async Task<List<MappedDriveInfo>> ReadMappedDrivesAsync()
    {
        var byLetter = new Dictionary<string, MappedDriveInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var networkKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Network");
            if (networkKey is not null)
            {
                foreach (var letter in networkKey.GetSubKeyNames())
                {
                    using var driveKey = networkKey.OpenSubKey(letter);
                    if (driveKey is null) continue;
                    string remotePath = (driveKey.GetValue("RemotePath") as string ?? string.Empty).Trim();
                    if (remotePath.Length == 0) continue;

                    // ConnectFlags bit 0x1 = "reconnect at logon" (the same flag Explorer's own
                    // "Reconnect at sign-in" checkbox on Map Network Drive writes).
                    bool reconnect = driveKey.GetValue("ConnectFlags") is int flags && (flags & 0x1) != 0;
                    byLetter[$"{letter}:"] = new MappedDriveInfo
                    {
                        DriveLetter = $"{letter}:",
                        RemotePath = remotePath,
                        ConnectionState = "Not connected (registered mapping only)",
                        ReconnectAtLogon = reconnect,
                    };
                }
            }
        }
        catch
        {
            // Best-effort - fall through with whatever `net use` alone can still provide.
        }

        try
        {
            string output = await RunCommandAsync("net.exe", "use");
            foreach (var (letter, remotePath, state) in ParseNetUseOutput(output))
            {
                if (byLetter.TryGetValue(letter, out var existing))
                {
                    // MappedDriveInfo is a mutable class specifically so a live `net use` state can
                    // overwrite the registry-derived placeholder in place, without a record `with`.
                    existing.ConnectionState = state;
                }
                else
                {
                    byLetter[letter] = new MappedDriveInfo { DriveLetter = letter, RemotePath = remotePath, ConnectionState = state };
                }
            }
        }
        catch
        {
            // `net.exe` failed/absent - the registry-derived rows above still stand.
        }

        return byLetter.Values.OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Parses `net use`'s fixed-width text table. Column layout: Status, Local, Remote,
    /// Network - Status/Local can be blank for a continuation line, so this only accepts rows that
    /// actually start with a drive letter.</summary>
    private static IEnumerable<(string Letter, string RemotePath, string State)> ParseNetUseOutput(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length < 4) continue;

            // Find a token like "X:" - net use pads Status left, so the drive letter's column
            // position varies slightly with locale/status text length; searching for the pattern
            // is more robust than a fixed substring offset.
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(?<letter>[A-Za-z]):\s+(?<remote>\\\\[^\s]+(?:\s[^\s]+)?)");
            if (!match.Success) continue;

            string letter = match.Groups["letter"].Value.ToUpperInvariant() + ":";
            string remote = match.Groups["remote"].Value.Trim();
            string state = line.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? "OK"
                : line.Contains("Disconnected", StringComparison.OrdinalIgnoreCase) ? "Disconnected"
                : line.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ? "Unavailable"
                : "Unknown";

            yield return (letter, remote, state);
        }
    }

    /// <summary>#587: on-demand reachability test for one mapped drive - a plain
    /// Directory.Exists can hang for tens of seconds against a dead SMB target (exactly the
    /// symptom this item exists to catch), so this races it against a timeout instead of calling
    /// it directly.</summary>
    public static async Task<(bool Reachable, string Text)> TestReachabilityAsync(string remotePath, TimeSpan timeout)
    {
        try
        {
            var checkTask = Task.Run(() => Directory.Exists(remotePath));
            var completed = await Task.WhenAny(checkTask, Task.Delay(timeout));
            if (completed != checkTask)
                return (false, $"Unreachable (no response within {timeout.TotalSeconds:0}s) - this is exactly the kind of dead mapping that hangs Explorer/file dialogs.");

            bool exists = await checkTask;
            return exists ? (true, "Reachable") : (false, "Unreachable (path not found)");
        }
        catch (Exception ex)
        {
            return (false, $"Unreachable ({ex.Message})");
        }
    }

    /// <summary>#587's "Disconnect" action - `net use X: /delete /y`, always behind the caller's
    /// own confirm (same "caller owns the prompt" convention NetworkStackResetService's own
    /// remarks describe).</summary>
    public static async Task<string> DisconnectAsync(string driveLetter)
    {
        try
        {
            return await RunCommandAsync("net.exe", $"use {driveLetter} /delete /y");
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }

    /// <summary>#588: negotiated dialect/signing/encryption per server, via `MSFT_SmbConnection`
    /// in the SMB client's own CIM namespace (the same class `Get-SmbConnection` reads) - flags
    /// SMB1 still in use (a real security/performance concern) and notes that signing (while a
    /// legitimate security control) does cost throughput on a slow link, without claiming this
    /// app knows whether it was administratively forced here.</summary>
    public static List<SmbConnectionInfo> ReadConnections()
    {
        var results = new List<SmbConnectionInfo>();
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\SMB");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_SmbConnection"));
            foreach (ManagementObject mo in searcher.Get())
            {
                string server = (mo["ServerName"] as string ?? string.Empty).Trim();
                string share = (mo["ShareName"] as string ?? string.Empty).Trim();
                if (server.Length == 0) continue;

                string dialect = (mo["Dialect"] as string ?? "Unknown").Trim();
                bool signed = mo["Signed"] is bool s && s;
                bool encrypted = mo["Encrypted"] is bool e && e;
                bool isSmb1 = dialect.StartsWith("1.", StringComparison.Ordinal) || dialect.Equals("1.0", StringComparison.Ordinal);

                // Null (not empty) when there's nothing worth flagging, so the view's own
                // NullToVisibilityConverter-gated caveat line collapses cleanly instead of showing
                // an empty row.
                string? caveat = isSmb1
                    ? "SMB1 is deprecated - both a known security risk (no signing-by-default, weaker crypto) and materially slower than SMB2/3."
                    : signed
                        ? "Signing is active on this connection - a legitimate security control, but it does add CPU/latency overhead that shows up most on an already-slow link."
                        : null;

                results.Add(new SmbConnectionInfo(server, share, dialect, signed, encrypted, isSmb1, caveat));
            }
        }
        catch
        {
            // The root\Microsoft\Windows\SMB namespace/provider isn't present (very old Windows),
            // or nothing is currently connected - an empty list either way, not a guessed row.
        }
        return results;
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>, keeping this service's
    /// historical trimmed-output shape and "{exe} timed out." sentinel.</summary>
    private static async Task<string> RunCommandAsync(string exe, string args, int timeoutMs = 15000)
        => (await ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: $"{exe} timed out.")).Output.Trim();
}
