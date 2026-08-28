using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #329/#336: Windows' own disk-diagnosis/bad-block/retry event sources - a different, narrower
/// slice of the event log than EventLogService's general crash/stability scan. Same
/// EventLogQuery/EventLogReader shape and the same "wrapped, degrades to empty rather than
/// throwing" tier - a channel not being registered on this machine, or a source simply never having
/// logged anything, is a normal, expected result, not a bug. A later chunk (#370) folds this into a
/// unified storage event timeline; for now these are two simple, independent, time-ordered lists.
/// </summary>
public static class DiskDiagnosisEventService
{
    private const int LookbackDays = 30;
    private const int MaxEvents = 100;

    // #329: the classic "Disk" source event 52 - "the driver has detected that device ... has
    // predicted that it will fail" (SMART failure prediction surfaced through the legacy disk class
    // driver - a different, independent signal from the WMI-level
    // MSStorageDriver_FailurePredictStatus flag #324 polls).
    private const int PredictedFailureEventId = 52;

    // #336: "the device has a bad block" (7) and an I/O was retried (153) - cross-referenced
    // against the SMART pending-sector counter elsewhere (see StorageViewModel.BuildBadSectorSummary).
    private const int BadBlockEventId = 7;
    private const int IoRetriedEventId = 153;

    private const string DiskDiagnosisChannel = "Microsoft-Windows-DiskDiagnosisDataCollector/Operational";
    private const string ClassicDiskSource = "Disk";

    /// <summary>#329: Microsoft-Windows-DiskDiagnosisDataCollector's own Operational channel, plus
    /// the System log's classic Disk-source event 52 - two different "Windows itself thinks this
    /// disk is failing" signals, combined into one time-ordered list.</summary>
    public static List<DiskDiagnosisEvent> ReadDiskDiagnosisEvents()
    {
        var events = new List<DiskDiagnosisEvent>();
        ReadFromChannel(events, DiskDiagnosisChannel, "DiskDiagnosisDataCollector");
        ReadFromProviderEventId(events, "System", ClassicDiskSource, PredictedFailureEventId, "Disk (event 52 - predicted failure)");
        return events.OrderByDescending(e => e.TimeCreated).Take(MaxEvents).ToList();
    }

    /// <summary>#336: bad-block (7) and I/O-retried (153) events from the classic "Disk" source.
    /// The legacy disk class driver doesn't reliably include a per-disk-number insertion string
    /// across every controller/driver, so this is a system-wide, time-ordered list rather than
    /// pre-split per physical disk - StorageViewModel cross-references the counts against SMART
    /// pending sectors at the "does this drive look actively deteriorating" level.</summary>
    public static List<DiskDiagnosisEvent> ReadBadBlockAndRetryEvents()
    {
        var events = new List<DiskDiagnosisEvent>();
        ReadFromProviderEventId(events, "System", ClassicDiskSource, BadBlockEventId, "Disk (event 7 - bad block)");
        ReadFromProviderEventId(events, "System", ClassicDiskSource, IoRetriedEventId, "Disk (event 153 - I/O retried)");
        return events.OrderByDescending(e => e.TimeCreated).Take(MaxEvents).ToList();
    }

    private static void ReadFromChannel(List<DiskDiagnosisEvent> into, string channel, string label)
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(channel, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            ReadEvents(reader, into, label);
        }
        catch
        {
            // Channel doesn't exist / isn't enabled on this system, or access denied - contribute nothing.
        }
    }

    private static void ReadFromProviderEventId(List<DiskDiagnosisEvent> into, string logName, string providerName, int eventId, string label)
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[Provider[@Name='{providerName}'] and EventID={eventId} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            ReadEvents(reader, into, label);
        }
        catch
        {
            // Provider/log unavailable - contribute nothing, same degrade-to-empty tier as EventLogService.
        }
    }

    private static void ReadEvents(EventLogReader reader, List<DiskDiagnosisEvent> into, string label)
    {
        int count = 0;
        while (count < MaxEvents && reader.ReadEvent() is { } record)
        {
            using (record)
            {
                count++;
                string message;
                try { message = record.FormatDescription() ?? string.Empty; }
                catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                into.Add(new DiskDiagnosisEvent
                {
                    TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                    Source = label,
                    EventId = record.Id,
                    Message = Truncate(message, 250),
                });
            }
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
