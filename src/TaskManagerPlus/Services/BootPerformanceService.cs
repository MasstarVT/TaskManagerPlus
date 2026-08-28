using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Boot time breakdown (#89) and boot-time trend history (#90). Windows records per-boot timing
/// in the Microsoft-Windows-Diagnostics-Performance/Operational event log (event ID 100, "Windows
/// has started up") - the same source Windows' own boot performance troubleshooting tooling reads.
/// The event's exact set of named fields (things like "BootTime", "MainPathBootTime",
/// "BootPostBootTime") is not a documented, versioned schema Microsoft publishes, so rather than
/// hardcode field names this app might get wrong on some Windows build, this reads every
/// millisecond-shaped "Boot...Time..." field the event actually carries and shows them as-is - the
/// largest one found stands in for "total boot time" (sub-phases are necessarily smaller slices of
/// it). This is the same "adaptive, degrade gracefully rather than guess a wrong exact contract"
/// tradeoff EventLogService's own bugcheck-code extraction already documents for a different event.
/// </summary>
public static class BootPerformanceService
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private const int BootEventId = 100;

    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string HistoryPath => AppPaths.GetPath("boot-history.json");

    private const int MaxHistoryEntries = 60;

    /// <summary>Reads the most recent boot-time breakdown, if the event log has one within the
    /// last 30 days. Returns null on any failure (log unavailable, no matching event, access
    /// denied) - the Startup tab should show "not available" rather than an error in that case.</summary>
    public static BootTimeBreakdown? ReadLatest()
    {
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID={BootEventId})]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            using var record = reader.ReadEvent();
            if (record is null) return null;

            var components = new List<BootTimeComponent>();
            foreach (var (label, ms) in ExtractBootTimeFields(record))
                components.Add(new BootTimeComponent { Label = label, Milliseconds = ms });

            return new BootTimeBreakdown
            {
                BootTime = record.TimeCreated ?? DateTime.Now,
                Components = components.OrderByDescending(c => c.Milliseconds).ToList(),
                Type = ReadLatestBootType(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Adaptive scan of the event's rendered XML for any &lt;Data Name="..."&gt; field
    /// whose name mentions both "Boot" and "Time" and whose value parses as a plausible
    /// millisecond duration (a few seconds to a few minutes) - see the class remarks for why this
    /// doesn't hardcode exact field names.</summary>
    private static IEnumerable<(string Label, int Ms)> ExtractBootTimeFields(EventRecord record)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { yield break; }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { yield break; }

        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        foreach (var data in doc.Descendants(ns + "Data"))
        {
            var nameAttr = data.Attribute("Name")?.Value ?? string.Empty;
            if (!nameAttr.Contains("Boot", StringComparison.OrdinalIgnoreCase)) continue;
            if (!nameAttr.Contains("Time", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(data.Value, out int value)) continue;
            if (value < 100 || value > 30 * 60 * 1000) continue; // plausible boot-phase duration only

            yield return (SplitLabel(nameAttr), value);
        }
    }

    /// <summary>"MainPathBootTime" -&gt; "Main Path Boot Time" - a light PascalCase splitter so an
    /// unfamiliar field name still reads reasonably in the UI.</summary>
    private static string SplitLabel(string pascalCase)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascalCase.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascalCase[i]) && !char.IsUpper(pascalCase[i - 1]))
                sb.Append(' ');
            sb.Append(pascalCase[i]);
        }
        return sb.ToString();
    }

    /// <summary>#90: appends one sample to the persisted boot-time trend, capped to the most
    /// recent MaxHistoryEntries - a small local log this app builds up itself over time, the same
    /// %AppData%\TaskManagerPlus JSON persistence pattern ThemeService/AlertThresholdsService use.
    /// De-duplicates by boot timestamp so calling this more than once for the same boot (e.g. the
    /// user revisits the Startup tab) doesn't add a repeat entry.</summary>
    public static List<BootHistoryEntry> RecordAndLoadHistory(BootTimeBreakdown? latest)
    {
        var history = LoadHistory();

        if (latest?.TotalMs is { } totalMs && !history.Any(h => h.Timestamp == latest.BootTime))
        {
            // #705: tag the newly recorded entry with this boot's type (if the Kernel-Boot event
            // is available) - only the current boot can be classified this way; earlier persisted
            // entries stay whatever they were tagged with (or null) since there's no way to
            // retroactively classify a boot that already happened.
            history.Add(new BootHistoryEntry { Timestamp = latest.BootTime, TotalMs = totalMs, Type = latest.Type });
            history = history.OrderBy(h => h.Timestamp).TakeLast(MaxHistoryEntries).ToList();
            SaveHistory(history);
        }

        return history;
    }

    #region #701/#702 - Diagnostics-Performance degradation event family (101/102/103/106/109/110)

    // Round 13, #701: the four "something specific took too long" events the culprit board (#702)
    // ranks by name; 106/110 are broader summaries rather than a single named culprit, so they're
    // read for the "what slowed this boot down" grid but excluded from the ranked board below.
    private static readonly int[] DegradationEventIds = { 101, 102, 103, 106, 109, 110 };
    private static readonly int[] CulpritEventIds = { 101, 102, 103, 109 };
    private const int MaxDegradationEvents = 3000; // generous - Windows retains at most a few hundred boots' worth in this channel

    private static string CategoryFor(int eventId) => eventId switch
    {
        101 => "Slow application start",
        102 => "Driver init delay",
        103 => "Service start delay",
        106 => "Background optimization",
        109 => "Device init delay",
        110 => "Boot degradation summary",
        _ => "Other",
    };

    /// <summary>#701: reads every 101/102/103/106/109/110 event still retained in the
    /// Diagnostics-Performance channel (up to the same 30-day lookback the rest of this app's
    /// event-log reads use). Returns an empty list - never throws - when the channel/provider
    /// isn't available, same degrade-to-nothing pattern as EventLogService.ReadLog.</summary>
    public static List<BootDegradationEvent> ReadDegradationEvents()
    {
        var results = new List<BootDegradationEvent>();
        try
        {
            long maxAgeMs = 30 * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", DegradationEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxDegradationEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var evt = ParseDegradationEvent(record);
                    if (evt is not null) results.Add(evt);
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - an empty list just means the "what slowed this
            // boot down" grid and culprit board both show nothing, not an error.
        }
        return results;
    }

    /// <summary>Adaptive field read, same "don't hardcode a field-name contract this app can't
    /// verify" tradeoff as ExtractBootTimeFields - looks for a Name/FileName field for the
    /// culprit's identity and *TotalTime*/*DegradationTime* fields for the cost, whichever of
    /// those this particular event ID actually carries.</summary>
    private static BootDegradationEvent? ParseDegradationEvent(EventRecord record)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { return null; }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }

        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        string? name = null;
        int? totalMs = null, degradationMs = null;
        foreach (var data in doc.Descendants(ns + "Data"))
        {
            var attrName = data.Attribute("Name")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(name) && attrName.Equals("Name", StringComparison.OrdinalIgnoreCase))
                name = data.Value;
            else if (string.IsNullOrEmpty(name) && attrName.Equals("FileName", StringComparison.OrdinalIgnoreCase))
                name = data.Value;
            else if (attrName.Contains("TotalTime", StringComparison.OrdinalIgnoreCase) && int.TryParse(data.Value, out var t))
                totalMs = t;
            else if (attrName.Contains("DegradationTime", StringComparison.OrdinalIgnoreCase) && int.TryParse(data.Value, out var d))
                degradationMs = d;
        }

        return new BootDegradationEvent
        {
            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
            EventId = record.Id,
            Category = CategoryFor(record.Id),
            Name = string.IsNullOrWhiteSpace(name) ? CategoryFor(record.Id) : name,
            TotalTimeMs = totalMs,
            DegradationTimeMs = degradationMs,
        };
    }

    /// <summary>#701: narrows the full degradation-event read down to just the entries that
    /// belong to one specific boot - everything logged at or after that boot's own event-100
    /// timestamp (and, if known, before the next recorded boot started).</summary>
    public static List<BootDegradationEvent> FilterForBoot(List<BootDegradationEvent> all, DateTime bootTime, DateTime? nextBootTime)
        => all.Where(e => e.TimeCreated >= bootTime && (nextBootTime is null || e.TimeCreated < nextBootTime.Value))
              .OrderByDescending(e => e.ImpactMs ?? 0)
              .ToList();

    /// <summary>#100 boot-completion timestamps still retained in the channel - used as real boot
    /// session boundaries for the culprit board below, independent of this app's own
    /// boot-history.json (which may have far fewer/more entries than the channel retains).</summary>
    private static List<DateTime> ReadBootTimestamps()
    {
        var times = new List<DateTime>();
        try
        {
            long maxAgeMs = 30 * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID={BootEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            const int maxEvents = 300;
            int count = 0;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is { } t) times.Add(t);
                }
            }
        }
        catch
        {
            // Channel unavailable - no boundaries, so the culprit board below just comes back empty.
        }
        return times.OrderBy(t => t).ToList();
    }

    /// <summary>#702: groups the 101/102/103/109 culprit-worthy events by name across every boot
    /// still retained in the channel and ranks by summed degradation time, so a driver that costs
    /// a few seconds on every single boot outranks a one-off long stall. Each event is bucketed
    /// into the most recent boot-timestamp at or before it - a real boot that's outside the
    /// channel's own retention (no earlier boot timestamp to attribute it to) is dropped rather
    /// than guessed at.</summary>
    public static List<BootCulprit> BuildCulpritBoard()
    {
        var bootTimes = ReadBootTimestamps();
        if (bootTimes.Count == 0) return new List<BootCulprit>();

        var events = ReadDegradationEvents().Where(e => CulpritEventIds.Contains(e.EventId)).ToList();

        var groups = new Dictionary<(string Name, int EventId), (double TotalMs, HashSet<DateTime> Boots)>();
        foreach (var e in events)
        {
            DateTime? session = null;
            foreach (var b in bootTimes)
            {
                if (b > e.TimeCreated) break;
                session = b;
            }
            if (session is null) continue;

            var key = (e.Name, e.EventId);
            if (!groups.TryGetValue(key, out var agg)) agg = (0, new HashSet<DateTime>());
            agg.TotalMs += e.ImpactMs ?? 0;
            agg.Boots.Add(session.Value);
            groups[key] = agg;
        }

        return groups
            .Select(kv => new BootCulprit
            {
                Name = kv.Key.Name,
                Category = CategoryFor(kv.Key.EventId),
                TotalDegradationMs = kv.Value.TotalMs,
                BootsAffected = kv.Value.Boots.Count,
                BootsObserved = bootTimes.Count,
            })
            .OrderByDescending(c => c.TotalDegradationMs)
            .Take(20)
            .ToList();
    }

    #endregion

    #region #705/#706/#707 - boot-type classification and derived stats

    private const string KernelBootLogName = "Microsoft-Windows-Kernel-Boot/Operational";
    private const int BootTypeEventId = 27;

    /// <summary>#705: reads the most recent Kernel-Boot event 27 to classify the current boot -
    /// 0 = full/cold boot, 1 = hybrid boot (Fast Startup resume), 2 = resume from hibernate. Not
    /// a documented, versioned schema, and the channel itself may not be enabled on every Windows
    /// build/edition, so any failure or out-of-range value degrades to null (Unknown) rather than
    /// a guess.</summary>
    public static BootType? ReadLatestBootType()
    {
        try
        {
            var query = new EventLogQuery(KernelBootLogName, PathType.LogName,
                $"*[System[(EventID={BootTypeEventId})]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record is null ? null : ExtractBootType(record);
        }
        catch
        {
            return null;
        }
    }

    private static BootType? ExtractBootType(EventRecord record)
    {
        try
        {
            if (record.Properties.Count == 0) return null;
            int raw = Convert.ToInt32(record.Properties[0].Value);
            return raw is >= 0 and <= 2 ? (BootType)raw : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#706: median boot time per boot-type bucket, e.g. "Fast Startup resume: 9s ·
    /// full restart: 71s" - the single most common reason a user's perceived boot time swings
    /// wildly. Entries with no recorded boot type (older history, or the Kernel-Boot channel
    /// wasn't available at record time) are excluded rather than lumped into a misleading bucket.</summary>
    public static List<BootTypeStat> ComputeBootTypeStats(List<BootHistoryEntry> history)
        => history
            .Where(h => h.Type is not null)
            .GroupBy(h => h.Type!.Value)
            .Select(g => new BootTypeStat { Type = g.Key, MedianMs = Median(g.Select(h => h.TotalMs).ToList()), Count = g.Count() })
            .OrderBy(s => s.Type)
            .ToList();

    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>#707: "this boot was 2.4x your normal" quick flag - compares the most recent
    /// history entry against the rolling median of earlier entries of the *same* boot type (a
    /// Fast Startup resume compared against full-restart history would always look "fast", which
    /// is meaningless), requiring a handful of same-type samples before flagging so a brand new
    /// history doesn't trigger a flag off one or two data points. A flag, not a verdict - see
    /// BootRegressionFlag's remarks.</summary>
    public static BootRegressionFlag? ComputeRegressionFlag(List<BootHistoryEntry> history, List<BootDegradationEvent> thisBootDegradations)
    {
        if (history.Count < 2) return null;
        var ordered = history.OrderBy(h => h.Timestamp).ToList();
        var latest = ordered[^1];

        var baseline = ordered.Take(ordered.Count - 1).Where(h => h.Type == latest.Type).Select(h => h.TotalMs).ToList();
        if (baseline.Count < 3) return null;

        int median = Median(baseline);
        if (median <= 0) return null;

        double ratio = (double)latest.TotalMs / median;
        if (ratio < 1.5) return null; // not meaningfully worse than usual - no banner

        string typeText = latest.Type.ToDisplayLabel();
        var rows = thisBootDegradations.Where(e => e.EventId is 101 or 102 or 103).ToList();

        return new BootRegressionFlag
        {
            Ratio = ratio,
            Message = $"This boot was {ratio:0.#}x your normal {typeText.ToLowerInvariant()} time.",
            DegradationRows = rows,
        };
    }

    #endregion

    #region #704 - ACPI FPDT firmware boot time

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableID, IntPtr firmwareTableBuffer, uint bufferSize);

    private const uint AcpiProvider = 0x41435049; // 'ACPI'
    private const uint FpdtTableId = 0x46504454; // 'FPDT'

    /// <summary>
    /// #704: "Last BIOS time" - not in any event log; it comes from the ACPI Firmware Performance
    /// Data Table's Firmware Basic Boot Performance Pointer Record, which in turn points at a
    /// physical-memory-resident data record carrying ResetEnd/OSLoaderStartImageStart timestamps.
    /// This safely reads and parses the FPDT table itself via GetSystemFirmwareTable (all within
    /// a managed buffer - no raw pointer access), which is enough to tell "does this firmware
    /// even publish boot-performance data" (older/OEM-locked firmware commonly doesn't) and "is
    /// the pointer record present". It deliberately does not attempt to dereference that record's
    /// physical address: Windows has blocked user-mode access to \Device\PhysicalMemory - even
    /// for elevated processes - since Vista SP1, with no documented replacement API, so any
    /// attempt to do so would either fail (the honest, expected outcome, already covered by
    /// returning Unknown here) or require exactly the kind of raw, unverified interop this app's
    /// conventions call for avoiding. See FirmwareBootTime's remarks for how the two "not found"
    /// cases are distinguished in the UI.
    /// </summary>
    public static FirmwareBootTime ReadFirmwareBootTime()
    {
        try
        {
            var table = ReadRawFirmwareTable(AcpiProvider, FpdtTableId);
            if (table is null || table.Length < 36)
                return new FirmwareBootTime { TableFound = false };

            bool pointerFound = FindBootPerformancePointerRecord(table) is not null;
            return new FirmwareBootTime { TableFound = true, PointerRecordFound = pointerFound, Milliseconds = null };
        }
        catch
        {
            return new FirmwareBootTime { TableFound = false };
        }
    }

    private static byte[]? ReadRawFirmwareTable(uint provider, uint tableId)
    {
        uint size = GetSystemFirmwareTable(provider, tableId, IntPtr.Zero, 0);
        if (size == 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint written = GetSystemFirmwareTable(provider, tableId, buffer, size);
            if (written == 0 || written > size) return null;

            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, (int)written);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Walks the FPDT's records (starting after the 36-byte standard ACPI table header)
    /// looking for record Type 0, the "Firmware Basic Boot Performance Pointer Record" (a 16-byte
    /// record: 2-byte type, 1-byte length, 1-byte revision, 4-byte reserved, 8-byte physical
    /// pointer). Returns the raw 8-byte pointer value found, or null if the table is malformed or
    /// doesn't carry one - never throws on odd/truncated data, since this isn't a documented,
    /// versioned struct this app controls.</summary>
    private static ulong? FindBootPerformancePointerRecord(byte[] table)
    {
        const int headerLength = 36;
        int pos = headerLength;
        while (pos + 4 <= table.Length)
        {
            ushort type = BitConverter.ToUInt16(table, pos);
            byte length = table[pos + 2];
            if (length < 4) break; // malformed - avoid an infinite/zero-length loop

            if (type == 0 && pos + 16 <= table.Length)
                return BitConverter.ToUInt64(table, pos + 8);

            pos += length;
        }
        return null;
    }

    #endregion

    /// <summary>#62 (System Specs): longest continuous-uptime record this month/this year - a pure
    /// derived read over this same persisted boot-history.json, no new sampling. A completed
    /// session's uptime is approximated as the gap between one recorded boot timestamp and the
    /// next (i.e. "the machine presumably stayed up in between", the same approximation
    /// EventLogService's own boot-time correlation already relies on elsewhere in this app); the
    /// most recent boot's still-ongoing session is measured against DateTime.Now instead, since
    /// there isn't a following boot yet. Returns (null, null) when there's fewer than one recorded
    /// boot to compare against.</summary>
    public static (TimeSpan? ThisMonth, TimeSpan? ThisYear) ComputeLongestUptimeRecords()
    {
        var history = LoadHistory().OrderBy(h => h.Timestamp).ToList();
        if (history.Count == 0) return (null, null);

        var sessions = new List<(DateTime Start, TimeSpan Duration)>();
        for (int i = 0; i < history.Count; i++)
        {
            var start = history[i].Timestamp;
            var end = i + 1 < history.Count ? history[i + 1].Timestamp : DateTime.Now;
            if (end > start) sessions.Add((start, end - start));
        }

        var now = DateTime.Now;
        TimeSpan? thisMonth = sessions.Where(s => s.Start.Year == now.Year && s.Start.Month == now.Month)
            .Select(s => (TimeSpan?)s.Duration).OrderByDescending(d => d).FirstOrDefault();
        TimeSpan? thisYear = sessions.Where(s => s.Start.Year == now.Year)
            .Select(s => (TimeSpan?)s.Duration).OrderByDescending(d => d).FirstOrDefault();
        return (thisMonth, thisYear);
    }

    public static List<BootHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<BootHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start a fresh history rather than blocking the tab.
        }
        return new List<BootHistoryEntry>();
    }

    private static void SaveHistory(List<BootHistoryEntry> history)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history));
        }
        catch
        {
            // Best-effort - if we can't persist, the trend just won't include this boot.
        }
    }
}
