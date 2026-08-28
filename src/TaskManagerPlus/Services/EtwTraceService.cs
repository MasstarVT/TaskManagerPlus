using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #146-153: shells out to logman.exe (ETW session/provider/autologger inspection) and
/// wpr.exe/tracerpt.exe (Windows Performance Recorder capture workflows and their post-capture
/// summary), the same "known Windows tool over raw interop" tradeoff every other Services/* class
/// in this app already takes for schtasks/sc/vssadmin/fsutil/defrag/tracert/powercfg/netsh -
/// there is no documented ETW-session or WPR-capture managed API surface to reach for instead.
///
/// Every shell-out uses the same concurrent-read/bounded-timeout/kill-on-timeout process pattern
/// ScheduledTaskService.RunCapturedAsync and VolumeDiagnosticsService already established (see
/// <see cref="RunCapturedAsync"/>). Neither logman nor wpr can be trusted to return exit code 0
/// only on success (logman routinely returns a nonzero code even for a fully successful listing;
/// verified live against `logman query providers`, which prints a complete, well-formed table yet
/// exits 255) - so parsing here never gates on exit code alone; it looks at whether the expected
/// output shape (a header row, a "WPR is not recording" sentinel, a produced summary.txt, ...)
/// actually appeared, exactly the "degrade to Unknown/0/hidden, never fabricate" rule CLAUDE.md
/// already asks for everywhere else in this app.
///
/// A second, real constraint shaped this file: logman's per-session detail text (`logman query
/// "&lt;name&gt;" -ets`) and tracerpt's summary.txt are real, working Windows tools, but neither
/// one publishes a stable, versioned output schema the way logman's own CSV/table list output
/// does - the exact field wording has been observed to drift across Windows releases in public
/// reports. Every field parsed out of either one here is matched defensively (several label
/// variants tried in order) and left at "Unknown"/null/0 rather than guessed when nothing matches,
/// and the full raw text is always kept on the result (RawDetailText / RawSummaryText) so a user
/// can read the real output for themselves if a parsed field ever looks wrong on some Windows
/// build this wasn't validated against.
/// </summary>
public static class EtwTraceService
{
    // ==================== #146: ETW session inspector ====================

    /// <summary>Lists every running ETW session via `logman query -ets` (Name/Type/Status only -
    /// no per-session detail, so this is fast even with dozens of sessions). Callers that also want
    /// provider/buffer/loss detail should follow up with <see cref="QuerySessionDetailAsync"/> per
    /// row - kept as two steps rather than one so the initial list renders immediately and detail
    /// loads progressively instead of one slow up-front sweep.</summary>
    public static async Task<List<EtwSessionRow>> QueryRunningSessionsAsync(CancellationToken ct = default)
    {
        var rows = new List<EtwSessionRow>();
        try
        {
            var (output, _) = await RunCapturedAsync("logman.exe", "query -ets", 15000, ct);
            rows = ParseSessionList(output);
        }
        catch
        {
            // logman unavailable/failed entirely - empty list, same degrade as every other
            // optional data source in this app.
        }
        return rows;
    }

    /// <summary>Parses `logman query -ets`'s fixed-width "Data Collector Set / Type / Status"
    /// table. Column positions are read from the header line itself rather than hardcoded offsets,
    /// so this survives a session name long enough to push the columns over (logman right-pads to
    /// fit the widest entry).</summary>
    internal static List<EtwSessionRow> ParseSessionList(string output)
    {
        var rows = new List<EtwSessionRow>();
        var lines = output.Replace("\r\n", "\n").Split('\n');

        int headerIndex = Array.FindIndex(lines, l => l.Contains("Data Collector Set", StringComparison.OrdinalIgnoreCase)
            && l.Contains("Type", StringComparison.OrdinalIgnoreCase) && l.Contains("Status", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0) return rows; // unexpected output shape - degrade to empty rather than guess

        string header = lines[headerIndex];
        int typeCol = header.IndexOf("Type", StringComparison.OrdinalIgnoreCase);
        int statusCol = header.IndexOf("Status", StringComparison.OrdinalIgnoreCase);
        if (typeCol < 0 || statusCol < 0 || statusCol <= typeCol) return rows;

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || line.StartsWith('-')) continue;
            if (line.Contains("command completed", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Length <= typeCol) continue;

            string name = line[..typeCol].Trim();
            if (name.Length == 0) continue;
            string type = statusCol <= line.Length ? line[typeCol..Math.Min(statusCol, line.Length)].Trim() : string.Empty;
            string status = statusCol < line.Length ? line[statusCol..].Trim() : string.Empty;

            rows.Add(new EtwSessionRow { Name = name, Type = type.Length > 0 ? type : "Trace", Status = status.Length > 0 ? status : "Unknown" });
        }
        return rows;
    }

    /// <summary>Fetches and parses one session's detail (`logman query "&lt;name&gt;" -ets`) -
    /// provider list, buffer size/count, real-time flag, log file, and the two loss counters that
    /// are the actual point of #146. Returns the same row shape with <see
    /// cref="EtwSessionRow.DetailLoaded"/> set, or with <see cref="EtwSessionRow.DetailError"/>
    /// set on failure (an inaccessible session, a name logman can't resolve, ...) rather than
    /// throwing - callers loop this per row and one failure shouldn't stop the rest.</summary>
    public static async Task<EtwSessionRow> QuerySessionDetailAsync(string sessionName, CancellationToken ct = default)
    {
        var row = new EtwSessionRow { Name = sessionName };
        try
        {
            var (output, exitCode) = await RunCapturedAsync("logman.exe", $"query \"{sessionName}\" -ets", 15000, ct);
            row.RawDetailText = output;
            if (string.IsNullOrWhiteSpace(output) || (exitCode is not null && exitCode != 0 && !LooksLikeSessionDetail(output)))
            {
                row.DetailError = "Couldn't read session detail (access denied, or the session ended before this ran).";
                return row;
            }
            ParseSessionDetail(output, row);
            row.DetailLoaded = true;
        }
        catch (Exception ex)
        {
            row.DetailError = ex.Message;
        }
        return row;
    }

    private static bool LooksLikeSessionDetail(string output) =>
        output.Contains("Name:", StringComparison.OrdinalIgnoreCase) || output.Contains("Provider", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex BufferSizeRegex = new(@"(?im)^\s*Buffer\s*Size\s*:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex BufferCountRegex = new(@"(?im)^\s*(Buffer\s*Count\s*\w*|Number\s*of\s*Buffers)\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex RealTimeRegex = new(@"(?im)^\s*Real[- ]?Time(?:\s*Data\s*Collection)?\s*:\s*(On|Off|Yes|No|True|False|\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex LogFileCurrentRegex = new(@"(?im)^\s*Current\s*:\s*(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex LogFileFieldRegex = new(@"(?im)^\s*(?:Output\s*Location|Log\s*File(?:\s*Name)?)\s*:\s*(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex EventsLostRegex = new(@"(?im)^\s*Events?\s*Lost\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex LogBuffersLostRegex = new(@"(?im)^\s*Log\s*Buffers\s*Lost\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex RealTimeBuffersLostRegex = new(@"(?im)^\s*Real[- ]?Time\s*Buffers?\s*Lost\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex GenericBuffersLostRegex = new(@"(?im)^\s*Buffers?\s*Lost\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex NameLineRegex = new(@"(?im)^\s*Name\s*:\s*(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex ProviderGuidRegex = new(@"(?im)^\s*Provider\s*Guid\s*:\s*(\{[0-9A-Fa-f\-]{36}\})\s*$", RegexOptions.Compiled);
    private static readonly Regex ProviderLevelRegex = new(@"(?im)^\s*Level\s*:\s*(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex ProviderKeywordsAnyRegex = new(@"(?im)^\s*KeywordsAny\s*:\s*(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex ProviderKeywordsAllRegex = new(@"(?im)^\s*KeywordsAll\s*:\s*(\S.*)$", RegexOptions.Compiled);

    /// <summary>Fills <paramref name="row"/>'s buffer/loss/provider fields from one session's raw
    /// `-ets` detail text. See the class remarks for why every field here is a best-effort,
    /// several-variants-tried regex match rather than a fixed-schema parse.</summary>
    internal static void ParseSessionDetail(string output, EtwSessionRow row)
    {
        var bufferSizeMatch = BufferSizeRegex.Match(output);
        if (bufferSizeMatch.Success) row.BufferSizeText = bufferSizeMatch.Groups[1].Value.Trim();

        var bufferCountMatches = BufferCountRegex.Matches(output);
        if (bufferCountMatches.Count > 0)
        {
            var parts = bufferCountMatches
                .Select(m => $"{Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ")}: {m.Groups[2].Value}")
                .Distinct()
                .ToList();
            row.BufferCountText = string.Join(", ", parts);
        }

        var realTimeMatch = RealTimeRegex.Match(output);
        if (realTimeMatch.Success)
        {
            string v = realTimeMatch.Groups[1].Value.Trim();
            row.IsRealTime = v.Equals("On", StringComparison.OrdinalIgnoreCase) || v.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1";
        }

        var currentMatch = LogFileCurrentRegex.Match(output);
        var logFieldMatch = LogFileFieldRegex.Match(output);
        string? logFile = currentMatch.Success ? currentMatch.Groups[1].Value.Trim()
            : logFieldMatch.Success ? logFieldMatch.Groups[1].Value.Trim() : null;
        if (!string.IsNullOrWhiteSpace(logFile)) row.LogFileName = logFile;

        var eventsLostMatch = EventsLostRegex.Match(output);
        if (eventsLostMatch.Success && long.TryParse(eventsLostMatch.Groups[1].Value, out long ev)) row.EventsLost = ev;

        var buffersLostMatch = LogBuffersLostRegex.Match(output);
        if (!buffersLostMatch.Success) buffersLostMatch = RealTimeBuffersLostRegex.Match(output);
        if (!buffersLostMatch.Success) buffersLostMatch = GenericBuffersLostRegex.Match(output);
        if (buffersLostMatch.Success && long.TryParse(buffersLostMatch.Groups[1].Value, out long bl)) row.BuffersLost = bl;

        // Providers: each is introduced by a "Provider Guid:" line (an unambiguous marker - very
        // unlikely to collide with anything else in this output) which we pair with the nearest
        // preceding "Name:" line as that provider's friendly name, then scan forward until the
        // next provider (or end) for its Level/KeywordsAny/KeywordsAll.
        var lines = output.Replace("\r\n", "\n").Split('\n');
        string? lastName = null;
        EtwSessionProviderInfo? current = null;
        foreach (var line in lines)
        {
            var nameMatch = NameLineRegex.Match(line);
            if (nameMatch.Success) lastName = nameMatch.Groups[1].Value.Trim();

            var guidMatch = ProviderGuidRegex.Match(line);
            if (guidMatch.Success)
            {
                current = new EtwSessionProviderInfo { Guid = guidMatch.Groups[1].Value, Name = lastName ?? "Unknown" };
                row.Providers.Add(current);
                continue;
            }

            if (current is null) continue;
            var levelMatch = ProviderLevelRegex.Match(line);
            if (levelMatch.Success) { current.Level = levelMatch.Groups[1].Value.Trim(); continue; }
            var anyMatch = ProviderKeywordsAnyRegex.Match(line);
            if (anyMatch.Success) { current.Keywords = anyMatch.Groups[1].Value.Trim(); continue; }
            var allMatch = ProviderKeywordsAllRegex.Match(line);
            if (allMatch.Success && current.Keywords == "Unknown") current.Keywords = allMatch.Groups[1].Value.Trim();
        }
        row.ProviderCount = row.Providers.Count > 0 ? row.Providers.Count : row.ProviderCount;
    }

    // ==================== #147: autologger inspector ====================

    private const string AutologgerKeyPath = @"SYSTEM\CurrentControlSet\Control\WMI\Autologger";

    /// <summary>
    /// Lists boot-start trace sessions ("autologgers") straight from
    /// HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\* - these are dormant configuration
    /// the kernel reads and starts tracing from before any user-mode process (including this app)
    /// could shell out to logman at all, so there is no live-session command that lists them.
    /// `logman query autologger` (with no name, to list all of them) is referenced in some older
    /// docs/blog posts but does not exist as a working command on current Windows - verified live
    /// against a 10.0.26100 install while building this feature (`logman query autologger` returns
    /// "Data Collector Set was not found", and `-help query` lists only the `providers` adverb, no
    /// `autologger` adverb) - so the registry is the only real source here, exactly as #147 itself
    /// specifies. This app runs elevated (CLAUDE.md), so this key should normally be fully
    /// readable; a denied/missing key degrades to an empty list rather than a thrown exception.
    ///
    /// <paramref name="providerNameByGuid"/> is optional - when supplied (typically from
    /// <see cref="QueryEtwProvidersAsync"/>'s already-fetched name/GUID map) each provider subkey's
    /// GUID is shown with its friendly name attached; otherwise the raw GUID is shown alone, which
    /// is still honest (never a guessed name).
    /// </summary>
    public static List<AutologgerRow> ReadAutologgers(IReadOnlyDictionary<string, string>? providerNameByGuid = null)
    {
        var rows = new List<AutologgerRow>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(AutologgerKeyPath);
            if (root is null) return rows;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;

                    bool enabled = TryGetInt64(key, "Start") is long start && start != 0;

                    string logFile = FirstNonEmptyStringValue(key, "LogFileName")
                        ?? (FirstNonEmptyStringValue(key, "OwningChannel") is { } channel ? $"(logs into event channel: {channel})" : "Unknown");

                    string maxSize = FormatAutologgerMaxSize(key);
                    string bufSize = TryGetInt64(key, "BufferSize") is long bs ? $"{bs} KB" : "Unknown";

                    var providers = new List<string>();
                    foreach (var providerGuid in key.GetSubKeyNames())
                    {
                        string label = providerGuid;
                        if (providerNameByGuid is not null && providerNameByGuid.TryGetValue(providerGuid, out var friendly))
                            label = $"{friendly} {providerGuid}";
                        providers.Add(label);
                    }

                    rows.Add(new AutologgerRow
                    {
                        Name = name,
                        Enabled = enabled,
                        LogFileName = logFile,
                        MaxFileSizeText = maxSize,
                        BufferSizeText = bufSize,
                        ProviderNames = providers,
                    });
                }
                catch
                {
                    // One autologger's key denied/corrupt shouldn't drop the rest.
                }
            }
        }
        catch
        {
            // Whole Autologger key inaccessible - empty list, not a thrown exception.
        }
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static long? TryGetInt64(RegistryKey key, string valueName)
    {
        try
        {
            var v = key.GetValue(valueName);
            return v is null ? null : Convert.ToInt64(v);
        }
        catch { return null; }
    }

    private static string? FirstNonEmptyStringValue(RegistryKey key, string valueName)
    {
        try
        {
            var v = key.GetValue(valueName);
            string? s = v?.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>Max log file size: different autologger/provider generations have used different
    /// value names for this (MaximumFileSize is the commonly documented one; FileMax/MaxFileSize
    /// show up too) - tries each in order rather than assuming one.</summary>
    private static string FormatAutologgerMaxSize(RegistryKey key)
    {
        foreach (var valueName in new[] { "MaximumFileSize", "FileMax", "MaxFileSize" })
        {
            if (TryGetInt64(key, valueName) is long mb && mb > 0) return $"{mb} MB";
        }
        return "Unknown";
    }

    // ==================== #148: ETW provider catalog ("ETW Providers", distinct from #113's "Event Providers") ====================

    /// <summary>Lists every registered ETW provider (name + GUID) via `logman query providers`.
    /// Deliberately does not attempt `logman query providers &lt;guid&gt;` per-provider - live
    /// testing while building this showed that command only returns the provider's keyword/level
    /// legend plus a PID/Image registration list, never which sessions have it enabled; "who's
    /// listening" is answered by <see cref="FindListeningSessions"/> instead, against whatever
    /// #146 session detail is already loaded.</summary>
    public static async Task<List<EtwProviderRow>> QueryEtwProvidersAsync(CancellationToken ct = default)
    {
        var rows = new List<EtwProviderRow>();
        try
        {
            var (output, _) = await RunCapturedAsync("logman.exe", "query providers", 15000, ct);
            var lines = output.Replace("\r\n", "\n").Split('\n');

            int headerIndex = Array.FindIndex(lines, l => l.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                && l.Contains("GUID", StringComparison.OrdinalIgnoreCase));
            if (headerIndex < 0) return rows;

            string header = lines[headerIndex];
            int guidCol = header.IndexOf("GUID", StringComparison.OrdinalIgnoreCase);
            if (guidCol < 0) return rows;

            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line.StartsWith('-')) continue;
                if (line.Contains("command completed", StringComparison.OrdinalIgnoreCase)) break;
                if (line.Length <= guidCol) continue;

                string name = line[..guidCol].Trim();
                string guid = line[guidCol..].Trim();
                if (name.Length == 0 || !guid.StartsWith('{')) continue;

                rows.Add(new EtwProviderRow { Name = name, Guid = guid });
            }
        }
        catch
        {
            // logman unavailable - empty catalog, same degrade as everywhere else.
        }
        return rows;
    }

    /// <summary>#148's "who's listening" - which of the already-detail-loaded sessions (from
    /// <see cref="QuerySessionDetailAsync"/>) have this provider enabled, and at what level/
    /// keywords. Pure in-memory filter, no shell-out - matches by GUID first (unambiguous), falling
    /// back to a case-insensitive name match for sessions whose provider block only resolved a
    /// name and not a GUID.</summary>
    public static List<EtwProviderSessionUsage> FindListeningSessions(EtwProviderRow provider, IEnumerable<EtwSessionRow> sessionsWithDetail)
    {
        var results = new List<EtwProviderSessionUsage>();
        foreach (var session in sessionsWithDetail)
        {
            if (!session.DetailLoaded) continue;
            var match = session.Providers.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(provider.Guid) && p.Guid.Equals(provider.Guid, StringComparison.OrdinalIgnoreCase))
                || (string.IsNullOrEmpty(p.Guid) && p.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
                results.Add(new EtwProviderSessionUsage { SessionName = session.Name, Level = match.Level, Keywords = match.Keywords });
        }
        return results;
    }

    // ==================== #149/#150: capture presets ====================

    /// <summary>#150's named scenario presets - the profile names are all real, verified built-in
    /// WPR profiles (checked live via `wpr -profiles` while building this: CPU, GPU, DesktopComposition,
    /// DiskIO, FileIO, Minifilter, Network, Power, Registry all exist). Disk estimates are static,
    /// hand-written rough figures, explicitly labelled as estimates wherever they're shown - not
    /// measured live, the same "quick flag, not a verdict" honesty this app already applies to its
    /// other heuristic numbers.</summary>
    public static List<EtwCapturePreset> GetCapturePresets() => new()
    {
        new EtwCapturePreset
        {
            Name = "Stutter / UI hangs",
            Description = "CPU, GPU, desktop composition, and disk I/O activity - use this when the desktop or an app visibly freezes or stutters.",
            DiskEstimate = "~50-150 MB/min (estimate)",
            WprProfiles = new[] { "CPU", "GPU", "DesktopComposition", "DiskIO" },
        },
        new EtwCapturePreset
        {
            Name = "Disk latency",
            Description = "Disk I/O, file I/O, and minifilter (antivirus/backup driver) activity - use this for slow file opens/saves or a disk that feels sluggish.",
            DiskEstimate = "~30-100 MB/min (estimate)",
            WprProfiles = new[] { "DiskIO", "FileIO", "Minifilter" },
        },
        new EtwCapturePreset
        {
            Name = "Network",
            Description = "Networking I/O activity - use this for slow downloads, dropped connections, or high network-related CPU use.",
            DiskEstimate = "~10-40 MB/min (estimate)",
            WprProfiles = new[] { "Network" },
        },
        new EtwCapturePreset
        {
            Name = "Power / idle drain",
            Description = "Power usage and CPU activity - use this to investigate a laptop that drains its battery unusually quickly at idle.",
            DiskEstimate = "~10-30 MB/min (estimate)",
            WprProfiles = new[] { "Power", "CPU" },
        },
        new EtwCapturePreset
        {
            Name = "Registry",
            Description = "Registry read/write activity - use this for slowdowns caused by heavy registry access (some antivirus/backup tools, some installers).",
            DiskEstimate = "~10-30 MB/min (estimate)",
            WprProfiles = new[] { "Registry" },
        },
    };

    // ==================== #149-152: capture start/stop/status/cancel ====================

    /// <summary>Free-space guard for #149's pre-check, reusing the same DriveInfo.AvailableFreeSpace
    /// read StorageThroughputService's own throughput test already uses rather than adding a new
    /// way to ask Windows the same question. Never blocks on an inconclusive check (unreadable
    /// drive, relative path that doesn't resolve, ...) - only a confirmed shortfall returns false.</summary>
    public static (bool Ok, string Message) CheckFreeDiskSpace(string targetPath, long minimumBytes = 1024L * 1024 * 1024)
    {
        try
        {
            string full = Path.GetFullPath(targetPath);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return (true, string.Empty);

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return (true, string.Empty);

            if (drive.AvailableFreeSpace < minimumBytes)
            {
                return (false, $"Only {Formatting.FormatBytes(drive.AvailableFreeSpace)} free on {root} - a trace capture can grow quickly, "
                    + $"especially in memory mode's own flush-to-disk on stop. Free up space or pick a different folder before starting.");
            }
            return (true, string.Empty);
        }
        catch
        {
            // Can't tell (bad path, removable drive not present, ...) - don't block the capture
            // over an unrelated failure to check.
            return (true, string.Empty);
        }
    }

    /// <summary>Starts a capture with one or more WPR profiles (`wpr -start &lt;profile&gt; [-start
    /// &lt;profile&gt; ...] [-filemode]`). Returns a plain-English failure message via
    /// <see cref="ExplainWprError"/> rather than the raw exit code/stderr - #152's requirement,
    /// applied here too since starting is where "another capture is already running" is actually
    /// hit in practice.</summary>
    public static async Task<(bool Success, string Message)> StartCaptureAsync(IEnumerable<string> wprProfiles, bool fileMode = true, CancellationToken ct = default)
    {
        var profiles = wprProfiles.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (profiles.Count == 0) return (false, "No profile selected.");

        var args = new StringBuilder();
        foreach (var p in profiles) args.Append(" -start ").Append(p);
        if (fileMode) args.Append(" -filemode");

        var (output, exitCode) = await RunCapturedAsync("wpr.exe", args.ToString().Trim(), 30000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output);
        return success ? (true, "Capture started.") : (false, ExplainWprError(output));
    }

    /// <summary>Stops the current capture and merges it into <paramref name="etlPath"/>
    /// (`wpr -stop "&lt;path&gt;"`). Given a generous timeout - merging a longer/verbose trace into
    /// its .etl genuinely can take a couple of minutes, unlike every other command in this file.</summary>
    public static async Task<(bool Success, string Message)> StopCaptureAsync(string etlPath, CancellationToken ct = default)
    {
        var (output, exitCode) = await RunCapturedAsync("wpr.exe", $"-stop \"{etlPath}\"", 300000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output) && File.Exists(etlPath);
        return success ? (true, $"Trace saved to {etlPath}.") : (false, ExplainWprError(output));
    }

    /// <summary>#152's rescue action - discards whatever capture is currently running without
    /// saving it (`wpr -cancel`).</summary>
    public static async Task<(bool Success, string Message)> CancelCaptureAsync(CancellationToken ct = default)
    {
        var (output, exitCode) = await RunCapturedAsync("wpr.exe", "-cancel", 30000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output);
        return success ? (true, "Capture cancelled.") : (false, ExplainWprError(output));
    }

    /// <summary>#152's status card (`wpr -status profiles collectors -details`). The idle sentinel
    /// text ("WPR is not recording") is verified live and stable; the active-recording shape wasn't
    /// verified against a live capture while building this (starting one here requires the
    /// elevation this build/dev environment didn't have), so parsing that branch is deliberately
    /// conservative: IsRecording flips true on anything that isn't the idle sentinel, and
    /// RawText always carries the full output so the UI can show it verbatim rather than a
    /// possibly-wrong parsed summary.</summary>
    public static async Task<WprStatusResult> GetWprStatusAsync(CancellationToken ct = default)
    {
        var result = new WprStatusResult();
        try
        {
            var (output, _) = await RunCapturedAsync("wpr.exe", "-status profiles collectors -details", 15000, ct);
            result.RawText = output;
            result.IsRecording = !output.Contains("is not recording", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(output);

            if (result.IsRecording)
            {
                // Best-effort: a line under a "Profiles:" heading, or a lone recognizable
                // profile-name-looking token, is treated as an active profile name. Left empty
                // (not guessed) if nothing matches - RawText is still shown either way.
                foreach (Match m in Regex.Matches(output, @"(?im)^\s*Profile\s*(?:Name)?\s*:\s*(\S.*)$"))
                    result.ActiveProfiles.Add(m.Groups[1].Value.Trim());
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }
        return result;
    }

    // ==================== #151: boot trace ====================

    /// <summary>
    /// Arms a one-shot boot trace (`wpr -addboot GeneralProfile -filemode`). Deliberately uses
    /// wpr's dedicated -addboot/-stopboot/-cancelboot boot-trace commands rather than the older
    /// `-start ... -onoffscenario boot -numiterations 1` form #151's own spec text names - live
    /// testing against this machine's wpr.exe (10.0.26100) while building this showed
    /// -onoffscenario is no longer listed under `wpr -help start` at all, while `wpr -help
    /// boottrace` documents -addboot/-stopboot/-cancelboot as the current, supported boot-trace
    /// workflow ("-addboot takes the same options as wpr -start"). Same intent as the spec
    /// (arm now, collect after the next boot), the actually-current command surface.
    /// </summary>
    public static async Task<(bool Success, string Message)> ArmBootTraceAsync(CancellationToken ct = default)
    {
        var (output, exitCode) = await RunCapturedAsync("wpr.exe", "-addboot GeneralProfile -filemode", 30000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output);
        return success ? (true, "Boot trace armed - it will record automatically during the next boot.") : (false, ExplainWprError(output));
    }

    /// <summary>Collects a previously-armed boot trace after the reboot has happened
    /// (`wpr -stopboot "&lt;path&gt;"`) - also removes the autologger -addboot configured itself,
    /// so this is a one-shot action, not something that needs a separate "disarm" step.</summary>
    public static async Task<(bool Success, string Message)> CollectBootTraceAsync(string etlPath, CancellationToken ct = default)
    {
        var (output, exitCode) = await RunCapturedAsync("wpr.exe", $"-stopboot \"{etlPath}\"", 300000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output) && File.Exists(etlPath);
        return success ? (true, $"Boot trace saved to {etlPath}.") : (false, ExplainWprError(output));
    }

    /// <summary>Disarms a previously-armed boot trace without collecting it (`wpr -cancelboot`) -
    /// offered alongside the reminder banner in case the user decides against rebooting.</summary>
    public static async Task<(bool Success, string Message)> CancelBootTraceAsync(CancellationToken ct = default)
    {
        var (output, exitCode) = await RunCapturedAsync("wpr.exe", "-cancelboot", 30000, ct);
        bool success = exitCode == 0 && !LooksLikeWprError(output);
        return success ? (true, "Boot trace disarmed.") : (false, ExplainWprError(output));
    }

    private static bool LooksLikeWprError(string output) =>
        Regex.IsMatch(output, @"(?im)^\s*Error\s*:?\s*$") || output.Contains("Error code:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #152: translates wpr's raw error text into a plain-English explanation rather than
    /// surfacing the exit code/stderr as-is. Covers the two failure modes actually observed while
    /// building this feature (both against this machine's live, real wpr.exe): starting a second
    /// capture while one is already running (well-documented as
    /// E_WPRC_DUPLICATE_INSTANCE_RUNNING/0xc5580601, plus the very similarly-worded 0xc5583017
    /// "shutdown recording already enabled and pending stop"), and attempting to start any capture
    /// without the elevation it needs (0xc5585011 "Failed to enable the policy to profile system
    /// performance" - this app runs elevated per CLAUDE.md, so this should be rare, but it's a
    /// completely different root cause from "something else is recording" and deserves a different
    /// explanation, not a guess). "Quick flag, not a verdict" applies here too: an unrecognized
    /// error falls through to the trimmed raw text rather than a made-up explanation.
    /// </summary>
    public static string ExplainWprError(string rawOutput)
    {
        string text = rawOutput ?? string.Empty;

        if (text.Contains("no trace profiles running", StringComparison.OrdinalIgnoreCase))
            return "No trace capture is currently running.";

        if (text.Contains("0xc5585011", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Failed to enable the policy to profile system performance", StringComparison.OrdinalIgnoreCase))
        {
            return "This trace capture needs administrator privileges to enable system profiling. "
                + "Task Manager Plus should already be running elevated - if this still happens, a Group Policy or security "
                + "setting may be blocking the system-profiling privilege (SeSystemProfilePrivilege).";
        }

        bool alreadyRecording = text.Contains("0xc5580601", StringComparison.OrdinalIgnoreCase)
            || text.Contains("0xc5583017", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already recording", StringComparison.OrdinalIgnoreCase)
            || text.Contains("duplicate instance", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already in progress", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already enabled", StringComparison.OrdinalIgnoreCase);
        if (alreadyRecording)
        {
            return "Another WPR trace capture is already running - started by this app, another tool, or WPRUI. "
                + "Only one capture is allowed at a time. Check \"Trace status\" below to see it, or use Cancel to discard it, "
                + "before starting a new one.";
        }

        string trimmed = text.Trim();
        return trimmed.Length switch
        {
            0 => "Unknown error (no output from wpr).",
            <= 400 => trimmed,
            _ => trimmed[..400] + "...",
        };
    }

    // ==================== #153: tracerpt summary ====================

    /// <summary>
    /// Runs `tracerpt &lt;etl&gt; -o dumpfile.xml -of XML -summary summary.txt -report report.html
    /// -f HTML -y` against a finished capture and parses summary.txt for event counts per provider,
    /// lost events, and trace duration. Output lands in a scratch folder under this app's own
    /// settings area (<c>AppPaths.GetPath("Traces","Reports",...)</c>, one subfolder per run) rather
    /// than next to the .etl itself, so repeated runs against the same trace - or traces saved
    /// somewhere the user doesn't want clutter, like Desktop - don't leave dumpfile.xml/summary.txt/
    /// report.html scattered around; this mirrors the "Logs"/"Snapshots"/"Reports" subfolder
    /// convention every other export in this app already uses under AppPaths.
    ///
    /// tracerpt's summary.txt is a real tool's real output but - like logman's per-session detail -
    /// isn't a documented stable schema, and this was never validated against a live capture's real
    /// summary.txt (producing one needs the elevation this build/dev environment didn't have - see
    /// <see cref="GetWprStatusAsync"/>'s remarks for the same constraint). Parsing is therefore
    /// deliberately conservative (several label variants tried, 0/null left in place of a guess on
    /// no match) and <see cref="TracerptSummary.RawSummaryText"/> always carries the complete,
    /// unparsed file so the UI can show the real thing next to whatever this parsed out of it.
    /// </summary>
    public static async Task<TracerptSummary> RunTracerptSummaryAsync(string etlPath, CancellationToken ct = default)
    {
        var result = new TracerptSummary();
        try
        {
            if (!File.Exists(etlPath))
            {
                result.ErrorMessage = "Trace file not found.";
                return result;
            }

            string scratchDir = AppPaths.GetPath("Traces", "Reports",
                $"{Path.GetFileNameWithoutExtension(etlPath)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(scratchDir);

            string dumpPath = Path.Combine(scratchDir, "dumpfile.xml");
            string summaryPath = Path.Combine(scratchDir, "summary.txt");
            string reportPath = Path.Combine(scratchDir, "report.html");

            string args = $"\"{etlPath}\" -o \"{dumpPath}\" -of XML -summary \"{summaryPath}\" -report \"{reportPath}\" -f HTML -y";
            var (output, _) = await RunCapturedAsync("tracerpt.exe", args, 300000, ct); // a large/verbose trace can take minutes to process

            if (!File.Exists(summaryPath))
            {
                result.ErrorMessage = string.IsNullOrWhiteSpace(output)
                    ? "tracerpt didn't produce a summary."
                    : $"tracerpt didn't produce a summary: {output.Trim()}";
                return result;
            }

            string summaryText = await File.ReadAllTextAsync(summaryPath, ct);
            result.RawSummaryText = summaryText;
            result.SummaryTextPath = summaryPath;
            if (File.Exists(reportPath)) result.HtmlReportPath = reportPath;

            ParseTracerptSummary(summaryText, result);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }
        return result;
    }

    private static readonly Regex TotalEventsProcessedRegex = new(@"(?im)^\s*Total\s*Events?\s*Processed\D*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex TotalEventsLostRegex = new(@"(?im)^\s*Total\s*Events?\s*Lost\D*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex ElapsedHmsRegex = new(@"(?im)Elapsed\s*Time\D*?(\d+):(\d{2}):(\d{2})", RegexOptions.Compiled);
    private static readonly Regex ElapsedSecondsRegex = new(@"(?im)Elapsed\s*Time\D*?(\d+(?:\.\d+)?)\s*(?:sec|s)\b", RegexOptions.Compiled);
    private static readonly Regex ProviderCountLineRegex = new(@"^(?<name>[A-Za-z][A-Za-z0-9 _.\-/]{2,80}?)\s*[:=]\s*(?<count>\d{1,12})\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> SummaryStatKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Total Events Processed", "Total Events Lost", "Total Buffers Processed", "Total Buffers Lost",
        "Total Format errors", "Elapsed Time", "Total Events", "Total Buffers",
    };

    /// <summary>Best-effort extraction of the "event counts per provider, lost events, trace
    /// duration" #153 asks for. See the class/method remarks above on why this is deliberately
    /// conservative rather than assuming one exact schema.</summary>
    internal static void ParseTracerptSummary(string summaryText, TracerptSummary result)
    {
        var processedMatch = TotalEventsProcessedRegex.Match(summaryText);
        if (processedMatch.Success && long.TryParse(processedMatch.Groups[1].Value, out long total)) result.TotalEvents = total;

        var lostMatch = TotalEventsLostRegex.Match(summaryText);
        if (lostMatch.Success && long.TryParse(lostMatch.Groups[1].Value, out long lost)) result.LostEvents = lost;

        var hmsMatch = ElapsedHmsRegex.Match(summaryText);
        if (hmsMatch.Success
            && int.TryParse(hmsMatch.Groups[1].Value, out int h) && int.TryParse(hmsMatch.Groups[2].Value, out int m) && int.TryParse(hmsMatch.Groups[3].Value, out int s))
        {
            result.TraceDuration = new TimeSpan(h, m, s);
        }
        else
        {
            var secMatch = ElapsedSecondsRegex.Match(summaryText);
            if (secMatch.Success && double.TryParse(secMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double secs))
                result.TraceDuration = TimeSpan.FromSeconds(secs);
        }

        // Per-provider counts: any "<name>: <count>" line that isn't one of the known
        // summary-statistic labels above is treated as a provider's event count. Heuristic, but
        // paired with RawSummaryText always being shown, it never hides or fabricates anything -
        // worst case is an empty list here while the raw file still shows the real numbers.
        foreach (var rawLine in summaryText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            var match = ProviderCountLineRegex.Match(line);
            if (!match.Success) continue;

            string name = match.Groups["name"].Value.Trim();
            if (SummaryStatKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            if (!long.TryParse(match.Groups["count"].Value, out long count)) continue;

            result.EventsPerProvider.Add(new TracerptProviderCount { ProviderName = name, EventCount = count });
        }
    }

    // ==================== shared process runner ====================

    /// <summary>
    /// Shells out and captures combined stdout+stderr, bounded by a real timeout - the same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern TracerouteService.RunAsync,
    /// ScheduledTaskService.RunCapturedAsync, and VolumeDiagnosticsService's own helpers already
    /// established (both reads started before the bounded wait, so a child that fills a pipe
    /// buffer before exiting can't deadlock the parent). A timed-out run returns ExitCode: null
    /// rather than throwing, so every caller here treats it exactly like any other non-zero/empty
    /// result.
    /// </summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return ("(command timed out or was cancelled)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
