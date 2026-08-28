using System.Diagnostics.Eventing.Reader;

namespace TaskManagerPlus.Services;

/// <summary>One event on the unified #596 timeline, flattened from whichever source scan actually
/// produced it - a common shape the grid/chart-marker code can treat uniformly, even though each
/// underlying provider's own event schema is completely different.</summary>
public sealed record NetworkTimelineEvent(DateTime TimeUtc, string Source, string Category, string Message);

/// <summary>#598's derived finding - a resume-from-sleep event followed, within a few minutes, by
/// something that looks like a network problem. Framed as a correlation, not a diagnosis: the
/// timing lines up, which is the most this app can actually claim.</summary>
public sealed record SleepResumeFinding(DateTime ResumeTimeUtc, DateTime FirstIssueTimeUtc, string IssueSource, string IssueSummary);

/// <summary>#596's full scan result. <see cref="UnavailableSources"/> lists which of the (up to)
/// eight sources couldn't be read - several of the channels involved are analytic logs disabled by
/// default, the same well-established caveat DnsEventLogService/DhcpEventLogService/etc. already
/// document individually; this just surfaces the same honesty across all of them in one place.</summary>
public sealed record NetworkEventTimelineResult(
    List<NetworkTimelineEvent> Events, List<string> UnavailableSources, List<SleepResumeFinding> SleepResumeFindings);

/// <summary>
/// Item #596 (suggestions.md "Event correlation and reporting"): merges events from Tcpip,
/// Dhcp-Client, DNS-Client, WLAN-AutoConfig, NlaSvc, NetworkProfile, SMBClient and the NIC miniport
/// driver into one chronological timeline over a caller-chosen window - so "the internet dropped
/// around 2pm" can actually be matched against everything that happened around 2pm instead of
/// checking five different cards' scan results by hand. On-demand only, behind an explicit Scan
/// button, never a timer, per CLAUDE.md's event-log-scan convention (this is a genuinely heavier
/// scan than any single source above, since it runs all of them).
///
/// Five of the eight sources already have a dedicated scanner elsewhere on this tab
/// (DnsEventLogService #524, DhcpEventLogService #530, WifiEventLogService #541, LinkFlapEventLogService
/// #548, SmbClientEventLogService #589) - this class calls each of those directly and flattens their
/// differently-shaped results into one <see cref="NetworkTimelineEvent"/> list, rather than
/// duplicating their query logic. There is no single shared "EventLogService" this app's other
/// event-log scans route through (the one class named that is Stability's own System/Application
/// crash-event reader, a different job entirely - see its own remarks) - so the three sources with
/// no existing scanner (Tcpip's general provider output, NlaSvc, and Microsoft-Windows-NetworkProfile/
/// Operational) are read here directly via EventLogReader, following the exact same
/// EventLogQuery + ReverseDirection + degrade-to-unavailable shape every other scan in this app uses.
///
/// #598 rides the same scan: Kernel-Power's own sleep (ID 42) and resume (ID 107) events are read
/// alongside everything else, and any resume followed within a few minutes by something that looks
/// like a network problem is surfaced as a <see cref="SleepResumeFinding"/> - a correlation, not a
/// diagnosis (CLAUDE.md's "quick flag, not a verdict" convention applies here as much as anywhere
/// else in this app).
/// </summary>
public static class NetworkEventTimelineService
{
    private const int MaxEventsPerSource = 1000;
    private const int MaxTotalEvents = 3000;
    private static readonly TimeSpan PostResumeCorrelationWindow = TimeSpan.FromMinutes(5);

    // Sub-categories, from across every source below, that read as an actual problem rather than a
    // routine/clean transition - used only by #598's sleep/resume correlation. Deliberately a fixed
    // list of categories this class itself assigns (see BuildEvents below), not a keyword search
    // over free-text messages, so it can't be fooled by a provider's own wording changing.
    private static readonly HashSet<string> IssueLikeCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Link down", "NACK / declined", "Lease failure", "Conflict", "Session timeout",
        "Connectivity loss", "Disconnected", "DNS-Client timeout", "Unidentified network", "Network disconnected",
    };

    public static async Task<NetworkEventTimelineResult> ScanAsync(TimeSpan window)
    {
        var tcpipTask = Task.Run(() => ScanGenericProvider("System", "Tcpip", "Tcpip"));
        var nlaSvcTask = Task.Run(() => ScanGenericProvider("System", "NlaSvc", "NlaSvc"));
        var networkProfileTask = Task.Run(() => ScanNetworkProfile(window));
        var kernelPowerTask = Task.Run(() => ScanKernelPower(window));

        var dnsTask = Task.Run(() => DnsEventLogService.Scan(window));
        var dhcpTask = Task.Run(() => DhcpEventLogService.Scan(window));
        var wlanTask = Task.Run(() => WifiEventLogService.Scan(window));
        var linkFlapTask = Task.Run(() => LinkFlapEventLogService.Scan(window));
        var smbTask = Task.Run(() => SmbClientEventLogService.Scan(window));

        await Task.WhenAll(tcpipTask, nlaSvcTask, networkProfileTask, kernelPowerTask,
            dnsTask, dhcpTask, wlanTask, linkFlapTask, smbTask);

        var events = new List<NetworkTimelineEvent>();
        var unavailable = new List<string>();

        AddScan(events, unavailable, "Tcpip", tcpipTask.Result);
        AddScan(events, unavailable, "NlaSvc", nlaSvcTask.Result);
        AddScan(events, unavailable, "NetworkProfile", networkProfileTask.Result);

        var dns = dnsTask.Result;
        foreach (var e in dns.Events) events.Add(new NetworkTimelineEvent(e.TimeUtc, "DNS-Client", e.Source, $"{e.QueriedName}: {e.Summary}"));
        if (!dns.OperationalChannelAvailable) unavailable.Add("DNS-Client/Operational");

        var dhcp = dhcpTask.Result;
        foreach (var e in dhcp.Events) events.Add(new NetworkTimelineEvent(e.TimeUtc, $"Dhcp-Client/{e.Channel}", e.Category, e.Message));
        if (!dhcp.AdminChannelAvailable) unavailable.Add("Dhcp-Client/Admin");
        if (!dhcp.OperationalChannelAvailable) unavailable.Add("Dhcp-Client/Operational");

        var wlan = wlanTask.Result;
        foreach (var e in wlan.Events)
            events.Add(new NetworkTimelineEvent(e.TimeUtc, "WLAN-AutoConfig",
                e.Kind, $"{e.Kind}{(e.Ssid is null ? string.Empty : $" ({e.Ssid})")}{(e.ReasonText is null ? string.Empty : $" - {e.ReasonText}")}"));
        if (!wlan.ChannelAvailable) unavailable.Add("WLAN-AutoConfig/Operational");

        var linkFlap = linkFlapTask.Result;
        foreach (var e in linkFlap.Events)
            events.Add(new NetworkTimelineEvent(e.TimeUtc, $"NIC miniport ({e.ProviderName})", e.Kind, e.Message));
        if (!linkFlap.ChannelAvailable) unavailable.Add("System (NIC miniport link-state)");

        var smb = smbTask.Result;
        foreach (var e in smb.Events) events.Add(new NetworkTimelineEvent(e.TimeUtc, "SMBClient", e.Category, e.Message));
        if (!smb.ConnectivityChannelAvailable) unavailable.Add("SMBClient/Connectivity");
        if (!smb.OperationalChannelAvailable) unavailable.Add("SMBClient/Operational");

        events = events
            .Where(e => DateTime.UtcNow - e.TimeUtc <= window)
            .OrderByDescending(e => e.TimeUtc)
            .Take(MaxTotalEvents)
            .ToList();

        var findings = CorrelateSleepResume(kernelPowerTask.Result, events);

        return new NetworkEventTimelineResult(events, unavailable.Distinct().ToList(), findings);
    }

    private static void AddScan(List<NetworkTimelineEvent> sink, List<string> unavailable, string sourceLabel, (List<NetworkTimelineEvent> Events, bool Available) scan)
    {
        sink.AddRange(scan.Events);
        if (!scan.Available) unavailable.Add(sourceLabel);
    }

    /// <summary>Reads every System-log event from one provider within the (generous, fixed) scan
    /// window - used for Tcpip and NlaSvc, neither of which has its own dedicated channel/event-ID
    /// contract the way DHCP/DNS/WLAN/SMB do, so this is a plain "everything this provider logged"
    /// capture rather than a curated event-ID subset.</summary>
    private static (List<NetworkTimelineEvent> Events, bool Available) ScanGenericProvider(string logName, string providerName, string categoryLabel)
    {
        var events = new List<NetworkTimelineEvent>();
        try
        {
            // A generous fixed lookback (30 days) rather than the caller's own window - the query
            // itself still gets narrowed to the caller's window by the final filter in ScanAsync,
            // this just avoids re-plumbing the window into every one of these small provider reads.
            long maxAgeMs = 30L * 24 * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[Provider[@Name='{providerName}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerSource && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    events.Add(new NetworkTimelineEvent(record.TimeCreated ?? DateTime.MinValue, categoryLabel,
                        $"Event {record.Id} ({record.LevelDisplayName})", Truncate(message, 260)));
                }
            }
            return (events, true);
        }
        catch
        {
            // Provider/log unavailable, or (for NlaSvc especially) this provider simply hasn't
            // logged anything to the System log on this machine - either way, degrade to empty
            // rather than throwing into the caller's Scan command.
            return (events, events.Count > 0);
        }
    }

    private const string NetworkProfileChannel = "Microsoft-Windows-NetworkProfile/Operational";
    private const int NetworkProfileConnected = 10000;
    private const int NetworkProfileUnidentified = 10001;
    private const int NetworkProfileDisconnected = 4004;

    private static (List<NetworkTimelineEvent> Events, bool Available) ScanNetworkProfile(TimeSpan window)
    {
        var events = new List<NetworkTimelineEvent>();
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery(NetworkProfileChannel, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerSource && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string category = record.Id switch
                    {
                        NetworkProfileConnected => "Network connected",
                        NetworkProfileUnidentified => "Unidentified network",
                        NetworkProfileDisconnected => "Network disconnected",
                        _ => $"Event {record.Id}",
                    };
                    events.Add(new NetworkTimelineEvent(record.TimeCreated ?? DateTime.MinValue, "NetworkProfile", category, Truncate(message, 260)));
                }
            }
            return (events, true);
        }
        catch
        {
            // Off by default on some builds, or access denied - a real, expected condition.
            return (events, false);
        }
    }

    // Microsoft-Windows-Kernel-Power's own sleep (42) and resume (107) event IDs, per this item's
    // own text.
    private const int KernelPowerSleepId = 42;
    private const int KernelPowerResumeId = 107;

    private static List<(DateTime TimeUtc, bool IsResume)> ScanKernelPower(TimeSpan window)
    {
        var results = new List<(DateTime, bool)>();
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID={KernelPowerSleepId} or EventID={KernelPowerResumeId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } time) continue;
                    results.Add((time, record.Id == KernelPowerResumeId));
                }
            }
        }
        catch
        {
            // Best-effort - the System log itself being unreadable is unlikely but not impossible
            // (access denied on a locked-down machine); #598 just produces no findings.
        }
        return results;
    }

    /// <summary>#598: for every resume-from-sleep event, looks for the earliest issue-like network
    /// event within <see cref="PostResumeCorrelationWindow"/> after it. A timing correlation only -
    /// see this class's own remarks.</summary>
    private static List<SleepResumeFinding> CorrelateSleepResume(List<(DateTime TimeUtc, bool IsResume)> powerEvents, List<NetworkTimelineEvent> networkEvents)
    {
        var findings = new List<SleepResumeFinding>();
        var resumes = powerEvents.Where(p => p.IsResume).Select(p => p.TimeUtc).Distinct().OrderBy(t => t).ToList();
        if (resumes.Count == 0 || networkEvents.Count == 0) return findings;

        var issueEvents = networkEvents.Where(e => IssueLikeCategories.Contains(e.Category)).OrderBy(e => e.TimeUtc).ToList();
        if (issueEvents.Count == 0) return findings;

        foreach (var resumeTime in resumes)
        {
            var firstIssue = issueEvents.FirstOrDefault(e => e.TimeUtc > resumeTime && e.TimeUtc <= resumeTime + PostResumeCorrelationWindow);
            if (firstIssue is null) continue;

            findings.Add(new SleepResumeFinding(resumeTime, firstIssue.TimeUtc, firstIssue.Source,
                $"{(firstIssue.TimeUtc - resumeTime).TotalSeconds:0}s after resuming from sleep: {firstIssue.Source} — {firstIssue.Category}"));
        }
        return findings;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
