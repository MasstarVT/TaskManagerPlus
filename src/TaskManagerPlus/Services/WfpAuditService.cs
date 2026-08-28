using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TaskManagerPlus.Services;

/// <summary>One Windows Filtering Platform drop event (#569) - Security log 5152 ("blocked a
/// packet") or 5157 ("blocked a connection"). <see cref="MatchedRuleName"/> is null until
/// <see cref="WfpAuditService.ScanAsync"/> successfully cross-references <see cref="FilterRuntimeId"/>
/// against `netsh wfp show filters` - never guessed.</summary>
public sealed record WfpDropEvent(
    DateTime TimeUtc, int EventId, string Direction, string SourceAddress, string SourcePort,
    string DestAddress, string DestPort, string Protocol, string? ApplicationPath, string? FilterRuntimeId, string? MatchedRuleName)
{
    public string EventLabel => EventId == 5157 ? "Blocked connection" : "Blocked packet";
}

/// <summary><see cref="ChannelAvailable"/> false means the Security log itself couldn't be read (or
/// the two audit subcategories were never enabled, so nothing was ever logged - this app can't tell
/// those two apart from an empty result, so the status text hedges rather than claiming either).
/// <see cref="FilterNamesResolved"/> is false when the `netsh wfp show filters` cross-reference
/// itself failed - the events are still returned with raw Filter Run-Time IDs, just without a
/// resolved rule name.</summary>
public sealed record WfpAuditScanResult(List<WfpDropEvent> Events, bool ChannelAvailable, bool FilterNamesResolved);

/// <summary>
/// Item #569 (suggestions.md "Firewall rules and blocked connections"): answers "which rule
/// actually blocked it", which the plain pfirewall.log (#568) can't - that log has no filter/rule
/// attribution at all. Two steps, both behind an explicit action per CLAUDE.md's disruptive-action
/// and on-demand-event-log-scan conventions:
///
/// 1. <see cref="EnableAuditingAsync"/> flips on the "Filtering Platform Packet Drop" and "Filtering
///    Platform Connection" audit subcategories via `auditpol.exe /set` (Windows' own tool for this -
///    there's no WMI/registry equivalent that's documented as stable). Off by default; turning it on
///    adds Security-log volume on a busy machine, so this always sits behind the caller's own
///    confirm dialog (this service has no confirmation of its own, matching every other disruptive
///    action's "caller owns the prompt" convention elsewhere in this app).
/// 2. <see cref="ScanAsync"/> reads Security log events 5152/5157 over a caller-chosen window (same
///    EventLogQuery + ReverseDirection shape DhcpEventLogService/DnsEventLogService already
///    establish for this tab's other on-demand scans), pulling fields out of each event's formatted
///    description via label-keyword search - the exact field text ("Source Address:", "Filter
///    Run-Time ID:", ...) is Microsoft's own documented auditing schema wording, but like every
///    other FormatDescription-based scan in this app it's still English-locale text, not a typed
///    contract, so a non-English install degrades to fewer resolved fields rather than a crash.
///    Each event's Filter Run-Time ID is then resolved to a display name via `netsh wfp show
///    filters` (again, the only source for this mapping - there's no WMI class or event field that
///    already carries the human-readable name).
/// </summary>
public static class WfpAuditService
{
    private const string PacketDropSubcategory = "Filtering Platform Packet Drop";
    private const string ConnectionSubcategory = "Filtering Platform Connection";
    private const int MaxEvents = 2000;

    public static async Task<string> EnableAuditingAsync()
    {
        string a = await RunAsync("auditpol.exe", $"/set /subcategory:\"{PacketDropSubcategory}\" /success:enable /failure:enable", 15000);
        string b = await RunAsync("auditpol.exe", $"/set /subcategory:\"{ConnectionSubcategory}\" /success:enable /failure:enable", 15000);
        string combined = $"{a}\n{b}".Trim();
        return combined.Length == 0 ? "Auditing enabled for both subcategories." : combined;
    }

    public static async Task<WfpAuditScanResult> ScanAsync(TimeSpan window)
    {
        var events = new List<WfpDropEvent>();
        bool channelOk = TryScanSecurityLog(window, events);

        bool resolved = false;
        if (events.Count > 0)
        {
            var filterNames = await ReadFilterNamesAsync();
            if (filterNames.Count > 0)
            {
                resolved = true;
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.FilterRuntimeId is { Length: > 0 } id && filterNames.TryGetValue(id, out var name))
                        events[i] = e with { MatchedRuleName = name };
                }
            }
        }

        return new WfpAuditScanResult(events.OrderByDescending(e => e.TimeUtc).ToList(), channelOk, resolved);
    }

    private static bool TryScanSecurityLog(TimeSpan window, List<WfpDropEvent> sink)
    {
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery("Security", PathType.LogName,
                $"*[System[(EventID=5152 or EventID=5157) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered locally - a known, common gap

                    sink.Add(new WfpDropEvent(
                        record.TimeCreated ?? DateTime.MinValue,
                        record.Id,
                        ExtractField(message, "Direction") ?? "Unknown",
                        ExtractField(message, "Source Address") ?? "-",
                        ExtractField(message, "Source Port") ?? "-",
                        ExtractField(message, "Destination Address") ?? "-",
                        ExtractField(message, "Destination Port") ?? "-",
                        ProtocolName(ExtractField(message, "Protocol")),
                        NormalizeApplicationPath(ExtractField(message, "Application Name")),
                        ExtractField(message, "Filter Run-Time ID"),
                        null));
                }
            }
            return true;
        }
        catch
        {
            // Access denied, or the Security channel couldn't be opened - a real, if uncommon,
            // condition on an elevated app; distinct from "auditing was never enabled" (which just
            // means zero matching events, not a read failure), but this app can't tell those apart
            // from a bare empty result, hence the caller hedges both under one status line.
            return false;
        }
    }

    private static string? NormalizeApplicationPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "-") return null;
        return raw.Trim();
    }

    private static string ProtocolName(string? raw) => raw switch
    {
        null => "Unknown",
        "6" => "TCP",
        "17" => "UDP",
        "1" => "ICMP",
        "58" => "ICMPv6",
        _ => raw,
    };

    // The formatted description text lines look like "Source Address:\t10.0.0.5" - label search
    // rather than a fixed column layout, since FormatDescription's exact whitespace/line-wrapping
    // isn't a stable contract.
    private static string? ExtractField(string message, string label)
    {
        var match = Regex.Match(message, $@"{Regex.Escape(label)}:\s*([^\r\n\t]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Correlates a Filter Run-Time ID to its rule/filter display name via `netsh wfp show
    /// filters file=-` (written straight to stdout rather than a temp file - simpler cleanup, and
    /// this app already reads netsh's stdout for everything else). Defensively extracts just the
    /// XML substring in case netsh prints any leading/trailing text around it, and never throws up
    /// to the caller - an unresolved ID is still shown raw in the grid, never fabricated.</summary>
    private static async Task<Dictionary<string, string>> ReadFilterNamesAsync()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string output = await RunAsync("netsh.exe", "wfp show filters file=-", 45000);
            int start = output.IndexOf('<');
            int end = output.LastIndexOf('>');
            if (start < 0 || end <= start) return map;

            var doc = XDocument.Parse(output[start..(end + 1)]);
            foreach (var item in doc.Descendants("item"))
            {
                string? id = item.Element("filterId")?.Value?.Trim();
                if (string.IsNullOrEmpty(id)) continue;
                string? name = item.Element("displayData")?.Element("name")?.Value?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                map[id] = name;
            }
        }
        catch
        {
            // Best-effort - raw Filter Run-Time IDs are still shown in the grid without this.
        }
        return map;
    }

    private static async Task<string> RunAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return $"Couldn't start {exe}.";

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
                return $"{exe} timed out.";
            }

            return ((await outputTask) + (await errorTask)).Trim();
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }
}
