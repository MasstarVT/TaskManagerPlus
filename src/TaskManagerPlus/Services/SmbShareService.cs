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
    // #586: read from the "SMB Client Shares" perf category through the shared CategorySampler
    // (one category read per call, CounterSample.Calculate for the rate math). The WMI class this
    // used before (Win32_PerfFormattedData_SMBClientShares_SMBClientShares) doesn't exist - the
    // real one is Win32_PerfFormattedData_Counters_SMBClientShares - and its formatted
    // AvgsecPerRead/Write are UInt32 whole seconds, so any sub-second latency would read 0 anyway.
    // Averages are over the interval since the previous call (the caller's own tick).
    private const string SmbShareCategory = "SMB Client Shares";
    private static readonly CategorySampler SmbShareSampler = new(SmbShareCategory);
    private static readonly object SmbShareSamplerLock = new();
    private static bool _smbShareSamplerPrimed;

    // Latency this app flags as "slow enough to look into" - a couple hundred ms average read/write
    // latency on a LAN share is already well outside "feels instant" territory. Informational only.
    private const double SlowLatencyMsThreshold = 200.0;

    /// <summary>#586: per-connected-share latency/queue-depth/throughput, for charting the slow
    /// ones. Empty (not an exception) when nothing's currently connected via SMB, or the perf
    /// counters aren't available on this machine.</summary>
    public static List<SmbShareLatency> ReadShareLatencies()
    {
        var results = new List<SmbShareLatency>();
        lock (SmbShareSamplerLock)
        {
            try
            {
                SmbShareSampler.Tick();
                if (!_smbShareSamplerPrimed)
                {
                    // Rate/average counters need two samples - prime once with a short interval so
                    // the very first read is a real measurement, not a row of zeros. (This runs
                    // under the caller's Task.Run, never on the UI thread.)
                    _smbShareSamplerPrimed = true;
                    Thread.Sleep(500);
                    SmbShareSampler.Tick();
                }

                foreach (string name in SmbShareSampler.InstanceNames("Avg. sec/Read"))
                {
                    if (name.Length == 0 || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

                    // "Avg. sec/Read"/"Avg. sec/Write" are average-timer counters in seconds -
                    // converted to ms here for a readable UI figure.
                    double readMs = SmbShareSampler.Value("Avg. sec/Read", name) * 1000.0;
                    double writeMs = SmbShareSampler.Value("Avg. sec/Write", name) * 1000.0;
                    double queueLength = SmbShareSampler.Value("Avg. Data Queue Length", name);
                    double dataBytesPerSec = SmbShareSampler.Value("Data Bytes/sec", name);
                    bool looksSlow = readMs > SlowLatencyMsThreshold || writeMs > SlowLatencyMsThreshold;

                    results.Add(new SmbShareLatency(name, readMs, writeMs, queueLength, dataBytesPerSec, looksSlow));
                }
            }
            catch
            {
                // No SMB client shares perf object on this machine - the view just shows an empty
                // list rather than a guessed figure.
            }
        }
        return results.OrderByDescending(r => Math.Max(r.AvgReadLatencyMs, r.AvgWriteLatencyMs)).ToList();
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

        // Mapped drives belong to a logon session. Under UAC this elevated process runs in a
        // separate session from the user's normal (filtered) one unless EnableLinkedConnections is
        // set, so `net use` here lists the user's drives as "Unavailable" (remembered, not connected
        // in THIS session) even while Explorer has them connected - say that instead of reporting
        // the drive itself as unavailable.
        bool splitSession = IsSplitElevatedSession();
        if (splitSession)
        {
            foreach (var d in byLetter.Values)
                d.ConnectionState = "Unknown - not visible from this elevated session (may be connected in your normal session)";
        }

        try
        {
            string output = await RunCommandAsync("net.exe", "use");
            foreach (var (letter, remotePath, rawState) in ParseNetUseOutput(output))
            {
                string state = splitSession && rawState == "Unavailable"
                    ? "Unavailable in this elevated session (may be connected in your normal session)"
                    : rawState;
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

    /// <summary>True when this process is elevated under UAC without linked connections - the
    /// case where its logon session doesn't see the user's own drive mappings. An approximation
    /// (the built-in Administrator account isn't split), so the UI wording says "may".</summary>
    private static bool IsSplitElevatedSession()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                return false;

            using var policy = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            bool uacOn = policy?.GetValue("EnableLUA") is not int lua || lua != 0;
            bool linked = policy?.GetValue("EnableLinkedConnections") is int l && l == 1;
            return uacOn && !linked;
        }
        catch
        {
            return false;
        }
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
