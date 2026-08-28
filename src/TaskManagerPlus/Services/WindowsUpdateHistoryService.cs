using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #769/#771: real Windows Update install/download/failure history, plus servicing-channel
/// correlation - the "Update history" section at the top of the Windows Health tab.
///
/// Win32_QuickFixEngineering (already read for the System tab's "Recently installed updates" card
/// - see SystemSpecsService.ReadRecentHotfixes) only ever sees CBS-installed hotfixes; it misses
/// driver updates, definition updates, and feature updates entirely, and it never records a
/// *failed* install at all. Microsoft-Windows-WindowsUpdateClient/Operational is the actual source
/// Windows' own Update history page reads from, and covers all of those plus errors.
///
/// #771 correlates a failed WU-client event against the Setup channel's own failure events
/// (provider Microsoft-Windows-Servicing, events 3/4) within a short time window, so a failed KB
/// shows both "what the WU client reported" and "what servicing itself said went wrong with which
/// package" in one place - the two logs are written by different components and neither one alone
/// tells the whole story for a failed install.
/// </summary>
public static class WindowsUpdateHistoryService
{
    // Update history matters over a much longer window than the Stability tab's 30-day crash
    // lookback - #775's whole point is diagnosing a machine that hasn't updated in months, so a
    // 30-day window would show nothing at all for exactly the case this tab exists to explain.
    private const int LookbackDays = 180;

    private const string WuClientLog = "Microsoft-Windows-WindowsUpdateClient/Operational";
    private static readonly int[] WuClientEventIds = { 19, 20, 21, 25, 31, 43, 44 };
    private static readonly HashSet<int> WuFailureEventIds = new() { 20, 25 };

    // Not anchored to the word "error" - Setup-channel failure messages phrase this a few
    // different ways ("... with error 0x...", "HRESULT = 0x...", "Error: 0x..."), and an 8-digit
    // 0x-prefixed hex token essentially only ever shows up as an HRESULT in these two providers'
    // message templates.
    private static readonly Regex ErrorCodeRegex = new(@"(0x[0-9A-Fa-f]{7,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#769: reads Microsoft-Windows-WindowsUpdateClient/Operational for install-success
    /// (19), install-failure (20), download-success (21), download-failure (25), reboot-required
    /// (31), install-started (43) and download-started (44) events. Degrades to an empty list when
    /// the channel is unavailable/access denied, same as every other event-log read in this app.</summary>
    public static List<WindowsUpdateEvent> ReadUpdateClientHistory()
    {
        var result = new List<WindowsUpdateEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", WuClientEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(WuClientLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    result.Add(new WindowsUpdateEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        Kind = KindLabel(record.Id),
                        IsFailure = WuFailureEventIds.Contains(record.Id),
                        Title = ExtractTitle(message),
                        ErrorCode = ExtractErrorCode(message),
                        RawMessage = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - degrade to "no update history found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static string KindLabel(int eventId) => eventId switch
    {
        19 => "Install succeeded",
        20 => "Install failed",
        21 => "Download succeeded",
        25 => "Download failed",
        31 => "Reboot required",
        43 => "Install started",
        44 => "Download started",
        _ => $"Event {eventId}",
    };

    #region #771 - Setup channel (servicing) correlation

    private const string SetupLog = "Setup";
    private const string ServicingProvider = "Microsoft-Windows-Servicing";
    private static readonly int[] SetupEventIds = { 1, 2, 3, 4 };
    private static readonly HashSet<int> SetupFailureEventIds = new() { 3, 4 };

    // Every observed Microsoft-Windows-Servicing message names the affected package right after
    // the word "package" - the same "extract from the rendered message, not an unverified indexed
    // property" approach EventLogService.ScmServiceNamePatterns already takes for a different
    // provider whose property layout isn't a documented, stable contract either.
    private static readonly Regex PackageNameRegex = new(@"package\s+([^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#771: reads the Setup channel (provider Microsoft-Windows-Servicing) for change-
    /// initiated (1), state-changed (2), and failure (3/4) events. Degrades to an empty list the
    /// same way every other event-log read in this service does - the Setup channel is enabled by
    /// default on a stock Windows install, unlike Task Scheduler's operational log, so no
    /// enable-prompt is needed here.</summary>
    public static List<ServicingChannelEvent> ReadSetupChannelEvents()
    {
        var result = new List<ServicingChannelEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", SetupEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(SetupLog, PathType.LogName,
                $"*[System[Provider[@Name='{ServicingProvider}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 1000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var packageMatch = PackageNameRegex.Match(message);

                    result.Add(new ServicingChannelEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        PackageName = packageMatch.Success ? packageMatch.Groups[1].Value.Trim().TrimEnd('.', ',') : "(unknown package)",
                        Detail = Truncate(message, 300),
                        ErrorCode = ExtractErrorCode(message),
                        IsFailure = SetupFailureEventIds.Contains(record.Id),
                    });
                }
            }
        }
        catch
        {
            // Log/provider unavailable/access denied - degrade to "no servicing history found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    /// <summary>#771: mutates each failed WU-client event's ServicingCorrelationText in place with
    /// the nearest Setup-channel failure within a 15-minute window (both logs are written within
    /// seconds of each other for the same failed install in practice; 15 minutes gives slack for
    /// clock/flush skew without risking pairing two unrelated failures). Pure post-processing over
    /// two already-read lists - no new query.</summary>
    public static void Correlate(List<WindowsUpdateEvent> wuEvents, List<ServicingChannelEvent> setupEvents)
    {
        var failures = setupEvents.Where(s => s.IsFailure).ToList();
        if (failures.Count == 0) return;

        foreach (var e in wuEvents.Where(e => e.IsFailure))
        {
            var nearest = failures
                .Where(s => Math.Abs((s.TimeCreated - e.TimeCreated).TotalMinutes) <= 15)
                .OrderBy(s => Math.Abs((s.TimeCreated - e.TimeCreated).TotalMinutes))
                .FirstOrDefault();
            if (nearest is null) continue;

            string errorSuffix = nearest.ErrorCode is { } code ? $" ({code})" : string.Empty;
            e.ServicingCorrelationText = $"Setup log: {nearest.PackageName}{errorSuffix} - {nearest.Detail}";
        }
    }

    #endregion

    #region #781 - "Did an update break this?" correlation with the Stability tab

    /// <summary>
    /// #781: flags an installed KB that landed within 48 hours before a faulting module (see
    /// StabilityEvent.FaultingModule) started recurring (2+ occurrences) in the Stability tab's own
    /// crash timeline - the same window used elsewhere in this app for "close enough to plausibly be
    /// related" correlation (see #771's own 15-minute Setup-channel pairing, just wider here since
    /// an update-induced crash pattern can take a day or two of use to first surface, not seconds).
    /// Pure post-processing over two already-read lists (StabilityViewModel.RefreshAsync supplies
    /// both) - no new query. A quick flag, not a verdict: plenty of recurring crashes have nothing to
    /// do with whatever update happened to land beforehand.
    /// </summary>
    public static List<UpdateBreakageFlag> CorrelateWithStabilityFailures(
        IReadOnlyList<WindowsUpdateEvent> wuEvents, IReadOnlyList<StabilityEvent> stabilityEvents)
    {
        var result = new List<UpdateBreakageFlag>();

        var installs = wuEvents.Where(e => e.EventId == 19).ToList(); // "Install succeeded"
        if (installs.Count == 0) return result;

        var recurringModules = stabilityEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.FaultingModule))
            .GroupBy(e => e.FaultingModule!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => new { Module = g.Key, FirstSeen = g.Min(e => e.TimeCreated), Count = g.Count() });

        foreach (var recurring in recurringModules)
        {
            var nearestInstall = installs
                .Where(i => i.TimeCreated <= recurring.FirstSeen && (recurring.FirstSeen - i.TimeCreated).TotalHours <= 48)
                .OrderByDescending(i => i.TimeCreated) // closest install before the first failure
                .FirstOrDefault();
            if (nearestInstall is null) continue;

            result.Add(new UpdateBreakageFlag
            {
                InstallTime = nearestInstall.TimeCreated,
                UpdateTitle = nearestInstall.Title,
                FaultingModule = recurring.Module,
                FirstFailureTime = recurring.FirstSeen,
                FailureCount = recurring.Count,
            });
        }

        return result.OrderByDescending(f => f.FirstFailureTime).ToList();
    }

    #endregion

    private static string? ExtractErrorCode(string message)
    {
        var m = ErrorCodeRegex.Match(message);
        return m.Success ? "0x" + m.Groups[1].Value[2..].ToUpperInvariant().PadLeft(8, '0') : null;
    }

    /// <summary>Best-effort update title extraction: every observed event template in this family
    /// puts the update's title after the message's final ": " segment - the same "pull it out of
    /// the rendered text, not a stable indexed property" tradeoff ExtractFaultingModule/
    /// ScmServiceNamePatterns already take elsewhere in this app. Empty when the message doesn't
    /// contain that shape at all.</summary>
    private static string ExtractTitle(string message)
    {
        int idx = message.LastIndexOf(": ", StringComparison.Ordinal);
        if (idx < 0 || idx + 2 >= message.Length) return string.Empty;
        return message[(idx + 2)..].Trim().TrimEnd('.', ' ');
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
