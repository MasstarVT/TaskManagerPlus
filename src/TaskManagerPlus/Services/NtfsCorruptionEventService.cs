using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #344: the System log's "Ntfs" provider corruption/resource-exhaustion signals - 55
/// (corruption detected), 98 (unable to write to the transaction log), 130/137 (volume resource
/// exhaustion / transaction log full), 140/142 (volume no longer writable). A sibling to
/// DiskDiagnosisEventService rather than an extension of it (different provider/event family) - per
/// this round's brief, a future chunk (#370) folds both into one unified storage event timeline.
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
        var deviceToLetter = BuildDeviceToLetterMap();
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
                        VolumeText = ResolveVolume(message, deviceToLetter),
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

    private static readonly Regex HarddiskVolumeRegex = new(@"\\Device\\HarddiskVolume(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ResolveVolume(string message, Dictionary<string, string> deviceToLetter)
    {
        var match = HarddiskVolumeRegex.Match(message);
        if (!match.Success) return "Unknown volume";
        string devicePath = $@"\Device\HarddiskVolume{match.Groups[1].Value}";
        return deviceToLetter.TryGetValue(devicePath, out var letter) ? $"{letter}:" : "Unknown volume";
    }

    /// <summary>Maps every fixed drive's letter to its NT device path (e.g. "C:" ->
    /// "\Device\HarddiskVolume3") via QueryDosDeviceW - the standard, minimal Win32 call for this;
    /// there's no WMI class that exposes a volume's raw NT device path directly, so this is one of
    /// the few raw-interop cases in this app (alongside CpuTopologyService/the PEB walk/the
    /// handle-table walk), reserved for exactly that "no tool or WMI class available" situation per
    /// CLAUDE.md. Best-effort: a drive this can't resolve just doesn't appear in the map, so
    /// ResolveVolume above falls back to "Unknown volume" rather than guessing.</summary>
    private static Dictionary<string, string> BuildDeviceToLetterMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                string letter = drive.Name.TrimEnd('\\', ':');
                var buffer = new StringBuilder(260);
                uint len = QueryDosDeviceW($"{letter}:", buffer, (uint)buffer.Capacity);
                if (len > 0) map[buffer.ToString()] = letter;
            }
        }
        catch { /* best-effort - an empty map just means every event shows "Unknown volume" */ }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
