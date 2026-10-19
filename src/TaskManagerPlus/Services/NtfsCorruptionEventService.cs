using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #344: the System log's NTFS corruption signals. Event IDs are per provider - checked
/// against both providers' manifests (`Get-WinEvent -ListProvider`): the legacy "Ntfs" provider
/// defines only 55 (corruption detected) and 130 (structure repaired); 98 (volume health) and 140
/// (failed to flush the transaction log - corruption may occur) belong to "Microsoft-Windows-Ntfs".
/// 98 is logged for every healthy volume at every mount ("is healthy. No action is needed."), so
/// only a 98 that says anything else is kept; Microsoft-Windows-Ntfs 142 is a routine free-space
/// summary and isn't read at all. A sibling to DiskDiagnosisEventService rather than an extension
/// of it (different provider/event family).
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
    private const string LegacyNtfsProvider = "Ntfs";
    private const string NtfsProvider = "Microsoft-Windows-Ntfs";
    private const int VolumeHealthEventId = 98;

    private static readonly (string Provider, int EventId)[] Sources =
    {
        (LegacyNtfsProvider, 55),
        (LegacyNtfsProvider, 130),
        (NtfsProvider, VolumeHealthEventId),
        (NtfsProvider, 140),
    };

    private const int LookbackDays = 60;
    private const int MaxEvents = 100;

    /// <summary>Grouped by volume (sorted by volume, most-recent-first within each) rather than
    /// purely by time, per this item's brief - a volume with a cluster of corruption events reads
    /// clearly as a cluster rather than interleaved with unrelated volumes' events.</summary>
    public static List<NtfsCorruptionEvent> ReadEvents()
    {
        var deviceToLetter = DevicePathResolver.BuildDeviceToLetterMap();
        var events = new List<NtfsCorruptionEvent>();
        foreach (var (provider, eventId) in Sources)
            ReadFromProviderEventId(events, provider, eventId, deviceToLetter);

        return events
            .OrderBy(e => e.VolumeText, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(e => e.TimeCreated)
            .Take(MaxEvents)
            .ToList();
    }

    private static void ReadFromProviderEventId(List<NtfsCorruptionEvent> into, string provider, int eventId, Dictionary<string, string> deviceToLetter)
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{provider}'] and EventID={eventId} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    // Healthy 98s (one per volume per mount) are skipped on the state code before
                    // paying for FormatDescription.
                    bool? unhealthy = eventId == VolumeHealthEventId ? VolumeHealthStateIsUnhealthy(record) : true;
                    if (unhealthy == false) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    if (unhealthy is null && (message.Trim().Length == 0 || message.Contains("is healthy", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    count++;
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

    /// <summary>98's third property is a health-state code that the message maps to text - 0 is
    /// "is healthy. No action is needed.", logged on every routine mount. Language-independent, so
    /// it's checked first; null when it can't be read (the caller then falls back to the formatted
    /// text, and skips the event if that's unreadable too - nothing says the volume is unhealthy).</summary>
    private static bool? VolumeHealthStateIsUnhealthy(EventRecord record)
    {
        try
        {
            if (record.Properties.Count > 2 && record.Properties[2].Value is { } state
                && long.TryParse(Convert.ToString(state, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                return code != 0;
        }
        catch { /* fall back to the message text */ }
        return null;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
