using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #344: the System log's "Ntfs" provider corruption/resource-exhaustion signals - 55
/// (corruption detected), 98 (unable to write to the transaction log), 130/137 (volume resource
/// exhaustion / transaction log full), 140/142 (volume no longer writable). A sibling to
/// DiskDiagnosisEventService rather than an extension of it (different provider/event family).
/// Round 18, #370 folds this list into a broader unified storage event timeline
/// (StorageEventTimelineService) rather than replacing it - this stays its own simple,
/// per-volume-grouped list, and the drive-letter resolver it needs is now shared (see
/// DevicePathResolver) rather than owned privately, so the unified timeline can reuse the exact
/// same QueryDosDeviceW-based lookup instead of a second copy.
/// Same EventLogQuery/EventLogReader shape, degrading to empty rather than throwing when the
/// provider has simply never logged anything (the common case on a healthy system).
/// </summary>
public static class NtfsCorruptionEventService
{
    private const string NtfsProviderName = "Ntfs";
    private static readonly int[] EventIds = { 55, 98, 130, 137, 140, 142 };
    private const int LookbackDays = 60;
    private const int MaxEvents = 100;

    /// <summary>Grouped by volume (sorted by volume, most-recent-first within each) rather than
    /// purely by time, per this item's brief - a volume with a cluster of corruption events reads
    /// clearly as a cluster rather than interleaved with unrelated volumes' events.</summary>
    public static List<NtfsCorruptionEvent> ReadEvents()
    {
        var deviceToLetter = DevicePathResolver.BuildDeviceToLetterMap();
        var events = new List<NtfsCorruptionEvent>();
        foreach (int eventId in EventIds)
            ReadFromProviderEventId(events, eventId, deviceToLetter);

        return events
            .OrderBy(e => e.VolumeText, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(e => e.TimeCreated)
            .Take(MaxEvents)
            .ToList();
    }

    private static void ReadFromProviderEventId(List<NtfsCorruptionEvent> into, int eventId, Dictionary<string, string> deviceToLetter)
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{NtfsProviderName}'] and EventID={eventId} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    into.Add(new NtfsCorruptionEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = eventId,
                        VolumeText = DevicePathResolver.ResolveVolumeFromMessage(message, deviceToLetter),
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/event unavailable, or (the common case) this event has simply never fired -
            // contribute nothing for this ID.
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
