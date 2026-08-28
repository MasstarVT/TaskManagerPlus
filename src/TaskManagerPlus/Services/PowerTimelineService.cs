using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #713: "Power &amp; boot timeline" - correlates the handful of System-log events that together
/// tell the story of every boot/shutdown cycle Windows still has logged: 6005 (Event Log service
/// started, i.e. a boot happened), 6006 (clean shutdown), 6008 (the previous shutdown was
/// unexpected - the plain-text sibling of Kernel-Power 41), 6013 (periodic uptime report),
/// Kernel-Power 41 (no clean shutdown recorded - the same event EventLogService's own
/// WasLastShutdownUnexpected flag already reads, for a different purpose), Kernel-Power 109 (a
/// shutdown's reason code), and User32 1074 (who/what initiated a restart/shutdown, and why) into
/// one chronological strip. Every event is scoped to its real provider in the query itself (not
/// just filtered by numeric ID, since IDs like 41/109/1074 aren't guaranteed unique across
/// providers) - the same Provider[@Name='...'] pattern EventLogService.ReadLowMemoryEvents and
/// ReadServiceStartDurations already use. Read on demand alongside the rest of the Stability tab's
/// event-log query - see StabilityViewModel's remarks on why this tab is on-demand, not polled.
/// </summary>
public static class PowerTimelineService
{
    private const int LookbackDays = 30;
    private const int MaxEvents = 300;

    public static List<PowerTimelineEntry> Read()
    {
        var results = new List<PowerTimelineEntry>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string filter =
                "(Provider[@Name='EventLog'] and (EventID=6005 or EventID=6006 or EventID=6008 or EventID=6013)) or " +
                "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=109)) or " +
                "(Provider[@Name='User32'] and EventID=1074)";
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[({filter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var entry = ParseEntry(record);
                    if (entry is not null) results.Add(entry);
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - an empty timeline, same degrade-to-nothing pattern
            // every other event-log read in this app already uses.
        }
        return results;
    }

    private static PowerTimelineEntry? ParseEntry(EventRecord record)
    {
        if (record.TimeCreated is not { } time) return null;

        string message;
        try { message = record.FormatDescription() ?? string.Empty; }
        catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

        string? kind = record.Id switch
        {
            6005 => "Boot",
            6006 => "CleanShutdown",
            6008 => "UnexpectedShutdown",
            6013 => "Uptime",
            41 => "NoCleanShutdown",
            109 => "ShutdownReason",
            1074 => "RestartInitiated",
            _ => null,
        };
        if (kind is null) return null;

        string summary = kind switch
        {
            "Boot" => "Event Log service started (system boot).",
            "CleanShutdown" => "Clean shutdown.",
            "UnexpectedShutdown" => "Previous shutdown was unexpected.",
            "NoCleanShutdown" => "No clean shutdown was recorded before this boot.",
            // Uptime/ShutdownReason/RestartInitiated already read naturally as Windows wrote them -
            // shown as-is rather than re-parsed, same adaptive-message-read tradeoff this app's
            // other event descriptions already take.
            _ => string.IsNullOrWhiteSpace(message) ? "(no further detail available)" : Truncate(message, 240),
        };

        return new PowerTimelineEntry
        {
            TimeCreated = time,
            EventId = record.Id,
            ProviderName = record.ProviderName ?? string.Empty,
            Kind = kind,
            Summary = summary,
        };
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>#741: correlates Kernel-Boot event 27 boot type 2 (resume from hibernate - see
    /// BootPerformanceService.ReadRecentBootTypeEvents, reused rather than re-querying the
    /// Kernel-Boot channel here) with a following Kernel-Power 41 or 6008 unexpected-shutdown
    /// entry from this same Power &amp; boot timeline to flag a resume that looks like it failed.
    /// <paramref name="timeline"/> lets a caller that already has a fresh Read() result (as
    /// StabilityViewModel.RefreshAsync does) pass it straight in rather than this running a second
    /// System-log query for the same events; omit it to have this call Read() itself.</summary>
    public static List<FailedResumeEntry> ReadFailedResumes(List<PowerTimelineEntry>? timeline = null)
    {
        var results = new List<FailedResumeEntry>();
        try
        {
            var bootEvents = BootPerformanceService.ReadRecentBootTypeEvents();
            var resumeEvents = bootEvents.Where(e => e.Type == BootType.ResumeFromHibernate).OrderBy(e => e.Time).ToList();
            if (resumeEvents.Count == 0) return results;

            var events = timeline ?? Read();
            var failureSignals = events.Where(e => e.Kind is "UnexpectedShutdown" or "NoCleanShutdown")
                .OrderBy(e => e.TimeCreated).ToList();
            if (failureSignals.Count == 0) return results;

            var bootTimesAsc = bootEvents.Select(e => e.Time).OrderBy(t => t).ToList();

            foreach (var resume in resumeEvents)
            {
                DateTime? nextBoot = bootTimesAsc.FirstOrDefault(t => t > resume.Time);
                if (nextBoot == default(DateTime)) nextBoot = null;

                var failure = failureSignals.FirstOrDefault(f =>
                    f.TimeCreated > resume.Time && (nextBoot is null || f.TimeCreated < nextBoot.Value));
                if (failure is not null)
                {
                    results.Add(new FailedResumeEntry
                    {
                        ResumeTime = resume.Time,
                        FailureTime = failure.TimeCreated,
                        FailureKind = failure.KindLabel,
                    });
                }
            }
        }
        catch
        {
            // Degrade to empty - same pattern every other event-log correlation in this app uses.
        }
        return results.OrderByDescending(f => f.ResumeTime).ToList();
    }
}
