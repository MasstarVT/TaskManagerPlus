using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #195-196: "performance-subsystem and assorted high-signal error families" - the Perflib
/// counter-corruption card (#195, directly relevant to this app since a broken counter provider
/// also breaks its own PerformanceCounter reads) and a single rollup card for six more
/// high-signal-but-lower-volume families (#196: Schannel/ESENT/GroupPolicy/Time-Service/DNS
/// Client/Tcpip). Same shape as KernelEventFamilyService (the prior chunk's storage/WHEA/driver
/// cards): each read reuses EventLogExplorerService.ReadPage rather than a new event-log reading
/// path, and degrades to empty on any failure - a locked-down channel or a provider absent on this
/// Windows edition are real, expected conditions, not bugs.
/// </summary>
public sealed class SubsystemErrorFamilyService
{
    private const int LookbackDays = 30;

    private readonly EventLogExplorerService _explorer;

    public SubsystemErrorFamilyService() : this(new EventLogExplorerService()) { }
    public SubsystemErrorFamilyService(EventLogExplorerService explorer) => _explorer = explorer;

    // ==================== #195: Perflib counter-corruption family ====================

    private const string PerflibProvider = "Microsoft-Windows-Perflib";
    private static readonly Regex PerflibServiceNameRegex = new(@"for service\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#195: Perflib 1008/1010/1017/1023/2004 - a performance counter DLL failed to load or
    /// returned bad/inconsistent data for the named counter provider. Listing which providers are
    /// broken (not just a raw event count) is the point here: a broken provider silently breaks this
    /// app's own PerformanceCounter-based tabs (CPU/Memory/Disk/Network) too, so this is one of the
    /// few Stability-tab cards that's also self-diagnostic for the app itself.</summary>
    public PerflibFailureSummary ReadPerflibFailures(int lookbackDays = LookbackDays)
    {
        var pairs = new (string, int)[]
        {
            (PerflibProvider, 1008), (PerflibProvider, 1010), (PerflibProvider, 1017), (PerflibProvider, 1023), (PerflibProvider, 2004),
        };
        var rows = ReadFamily("System", pairs, lookbackDays);
        if (rows.Count == 0) return new PerflibFailureSummary();

        var failures = rows.OrderByDescending(r => r.TimeCreated).Select(r => new PerflibFailureEvent
        {
            TimeCreated = r.TimeCreated,
            EventId = r.EventId,
            CounterProviderName = ExtractCounterProviderName(r),
            Description = PerflibDescription(r.EventId),
        }).ToList();

        var providers = failures
            .Where(f => !string.IsNullOrWhiteSpace(f.CounterProviderName))
            .Select(f => f.CounterProviderName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PerflibFailureSummary { Failures = failures, AffectedProviders = providers };
    }

    /// <summary>Best-effort counter-provider/service name - Perflib's own message text names it
    /// ("...for service "X"...") for most of these event IDs; falls back to the event's first
    /// inserted property (the same positional convention KernelEventFamilyService.
    /// ExtractDriverNameFromProperties already uses for a different provider) when the message
    /// doesn't match.</summary>
    private static string? ExtractCounterProviderName(EventRecordRow row)
    {
        var match = PerflibServiceNameRegex.Match(row.Message);
        if (match.Success) return match.Groups[1].Value.Trim();
        return row.PropertyValues.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
    }

    private static string PerflibDescription(int eventId) => eventId switch
    {
        1008 => "Open procedure failed - the counter DLL threw an exception while initializing.",
        1010 => "Collect procedure returned an unexpected error while gathering counter data.",
        1017 => "Collect procedure ran, but the size reported for the counter data doesn't match what was configured.",
        1023 => "Close procedure failed - the counter DLL threw an exception while shutting down.",
        2004 => "The system-wide counter registry (English) is corrupt and could not be opened.",
        _ => "Performance counter provider error.",
    };

    /// <summary>#195: rebuilds the system's performance-counter registry (`lodctr /R`) - a real
    /// system change, so this method performs no confirmation of its own; the caller (ViewModel) is
    /// responsible for the explicit MessageBox confirmation first, the same "the write itself is
    /// unconditional, the caller decides whether to call it" shape WerReportService.
    /// WriteLocalDumpsSettings documents. Given a generous timeout - lodctr can genuinely take a
    /// minute or more on a machine with many registered counter sets.</summary>
    public static async Task<(bool Success, string Output)> RunLodctrRebuildAsync(CancellationToken ct = default)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("lodctr.exe", "/R", 180000, ct);
            return (exitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== #196: assorted subsystem error families rollup ====================

    private static readonly (string FamilyName, string LogName, string Provider, int[] EventIds, string Summary)[] RollupFamilies =
    {
        ("TLS/SSL (Schannel)", "System", "Schannel", new[] { 36871, 36887 },
            "Failed TLS/SSL handshakes - certificate validation, protocol/cipher mismatch, or a fatal alert from the remote endpoint."),
        ("Database engine (ESENT)", "Application", "ESENT", new[] { 455, 474 },
            "ESE database corruption/initialization failures - this is how Windows Search, Windows Update's datastore, or a mail store typically break."),
        ("Group Policy", "System", "Microsoft-Windows-GroupPolicy", new[] { 1085, 1129 },
            "Group Policy processing failures - expected/harmless on a non-domain-joined PC, otherwise worth checking domain-controller connectivity."),
        ("Time sync (W32Time)", "System", "Microsoft-Windows-Time-Service", new[] { 129, 134, 50 },
            "Clock drift/time-source failures - a drifted clock can break HTTPS certificate validation and license checks."),
        ("DNS Client", "System", "Microsoft-Windows-DNS-Client", new[] { 1014 },
            "DNS name-resolution timeouts - none of the configured DNS servers responded in time."),
        ("Port exhaustion (Tcpip)", "System", "Tcpip", new[] { 4227, 4231 },
            "The local dynamic TCP port range is exhausted or nearly so - usually one process opening far more outbound connections than normal."),
    };

    /// <summary>#196: one rollup card covering six more high-signal-but-lower-volume families than
    /// #184-190/#195 already cover individually - each family read independently (one missing
    /// channel/provider doesn't blank the others) and only families with at least one hit are
    /// returned, so the card can collapse entirely on a clean PC the same way #184/#187/#190's cards
    /// already do.</summary>
    public List<SubsystemFamilyGroup> ReadSubsystemFamilies(int lookbackDays = LookbackDays)
    {
        var results = new List<SubsystemFamilyGroup>();
        foreach (var (familyName, logName, provider, eventIds, summary) in RollupFamilies)
        {
            var pairs = eventIds.Select(id => (provider, id));
            var rows = ReadFamily(logName, pairs, lookbackDays);
            if (rows.Count == 0) continue;

            var hits = rows.OrderByDescending(r => r.TimeCreated)
                .Select(r => new SubsystemFamilyHit
                {
                    TimeCreated = r.TimeCreated,
                    Provider = r.ProviderName,
                    EventId = r.EventId,
                    Description = Truncate(r.Message, 300),
                })
                .ToList();

            results.Add(new SubsystemFamilyGroup
            {
                FamilyName = familyName,
                Summary = summary,
                TotalCount = rows.Count,
                LastSeen = rows.Max(r => r.TimeCreated),
                Hits = hits,
            });
        }
        return results.OrderByDescending(g => g.TotalCount).ToList();
    }

    // ==================== shared helpers ====================

    private List<EventRecordRow> ReadFamily(string logName, IEnumerable<(string Provider, int EventId)> pairs, int lookbackDays, int pageSize = 1000)
    {
        var list = pairs.ToList();
        if (list.Count == 0) return new List<EventRecordRow>();

        string idsClause = string.Join(" or ", list
            .GroupBy(f => f.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"(Provider[@Name={EventLogExplorerService.QuoteXPathLiteral(g.Key)}] and ({string.Join(" or ", g.Select(f => $"EventID={f.EventId}"))}))"));

        long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
        string xpath = $"*[System[({idsClause}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";

        try
        {
            var result = _explorer.ReadPage(logName, xpath, null, pageSize);
            return result.ErrorText is null ? result.Rows : new List<EventRecordRow>();
        }
        catch
        {
            return new List<EventRecordRow>();
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    private static async Task<(string Output, int ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs, CancellationToken ct)
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
            return ("(command timed out or was cancelled)", -1);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
