using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #197-199: "log health, retention and evidence" - per-channel health (EventLogConfiguration next
/// to EventLogInformation, plus a derived "about how many days of history this channel holds", #197),
/// retention recommendations with a confirmed, backed-up, revertible one-click apply via `wevtutil sl`
/// (#198), and log-clearing/record-gap detection (#199). Every read degrades to Unknown/empty rather
/// than throwing - a locked-down channel, a channel with zero records, or a `wevtutil` failure are
/// all real, expected conditions here, not bugs. The one write path (#198's ApplyMaxSizeAsync/
/// EnableChannelAsync) is never called without the caller (StabilityViewModel-style explicit
/// MessageBox confirmation, same shape as WerReportService's LocalDumps toggle) having already
/// backed up the previous value via the event-log-config.json helpers below.
/// </summary>
public sealed class EventLogHealthService
{
    private readonly EventLogExplorerService _explorer;

    public EventLogHealthService() : this(new EventLogExplorerService()) { }
    public EventLogHealthService(EventLogExplorerService explorer) => _explorer = explorer;

    // ==================== #197: per-channel health ====================

    /// <summary>#197: EventLogConfiguration (enabled/max size/log mode/file path/provider list) next
    /// to EventLogInformation (record count/file size/IsLogFull), plus a derived "about how many
    /// days of history this log currently holds" read from a single forward-direction read of the
    /// oldest record (cheap - one record, not a full-log walk). The config half and the info half
    /// are read independently (IsConfigReadable/IsInfoReadable) since a channel can fail one without
    /// the other (e.g. GetLogInformation denied on a channel whose EventLogConfiguration still
    /// opens fine).</summary>
    public ChannelHealthInfo GetChannelHealth(string channelName)
    {
        bool configReadable = false;
        bool? isEnabled = null;
        string logModeText = "Unknown";
        long? maxSizeBytes = null;
        string? filePath = null;
        var providerNames = new List<string>();

        try
        {
            using var config = new EventLogConfiguration(channelName);
            isEnabled = config.IsEnabled;
            logModeText = config.LogMode.ToString();
            maxSizeBytes = config.MaximumSizeInBytes;
            filePath = config.LogFilePath;
            try { providerNames = config.ProviderNames?.ToList() ?? new List<string>(); }
            catch { /* provider list unreadable for this channel - config's other fields still stand */ }
            configReadable = true;
        }
        catch
        {
            // Access denied / channel not actually configurable - degrade to Unknown for this half.
        }

        bool infoReadable = false;
        long? recordCount = null;
        long? fileSizeBytes = null;
        bool? isLogFull = null;
        string? readError = null;
        try
        {
            var info = EventLogSession.GlobalSession.GetLogInformation(channelName, PathType.LogName);
            recordCount = info.RecordCount;
            fileSizeBytes = info.FileSize;
            isLogFull = info.IsLogFull;
            infoReadable = true;
        }
        catch (Exception ex)
        {
            readError = ex.Message;
        }

        DateTime? oldest = recordCount is > 0 ? ReadOldestRecordTime(channelName) : null;
        double? retentionDays = oldest is { } o ? Math.Max(0, (DateTime.Now - o).TotalDays) : null;

        return new ChannelHealthInfo
        {
            ChannelName = channelName,
            IsConfigReadable = configReadable,
            IsEnabled = isEnabled,
            LogModeText = logModeText,
            MaxSizeBytes = maxSizeBytes,
            FilePath = filePath,
            ProviderNames = providerNames,
            IsInfoReadable = infoReadable,
            RecordCount = recordCount,
            FileSizeBytes = fileSizeBytes,
            IsLogFull = isLogFull,
            OldestRecordTime = oldest,
            EffectiveRetentionDays = retentionDays,
            ReadError = readError,
        };
    }

    /// <summary>Reads just the single oldest record's TimeCreated - a forward-direction
    /// (ReverseDirection=false, the default) EventLogReader stops after its first ReadEvent() call,
    /// so this never walks the whole channel just to find where its history starts.</summary>
    private static DateTime? ReadOldestRecordTime(string channelName)
    {
        try
        {
            var query = new EventLogQuery(channelName, PathType.LogName, "*");
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.TimeCreated;
        }
        catch
        {
            return null;
        }
    }

    // ==================== #198: retention recommendation + one-click apply ====================

    private const int TargetLookbackDays = 30;
    private const long MinRoundingUnitBytes = 64 * 1024; // wevtutil requires a 64KB-aligned max size

    /// <summary>Diagnostic channels this app already reads from elsewhere (KernelEventFamilyService/
    /// EventTimelineService/EventAnomalyDetectionService) that ship disabled on a stock Windows
    /// install - worth an explicit "enable this" suggestion when the user is looking at one of them,
    /// since a disabled channel silently means those other cards/scans just find nothing. Not an
    /// exhaustive list of every disabled channel on the system (that would be most Analytic/Debug
    /// channels, the great majority of which nobody needs) - just the handful this app itself
    /// already has a reason to read.</summary>
    private static readonly HashSet<string> KnownDiagnosticChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-DNS-Client/Operational",
        "Microsoft-Windows-TaskScheduler/Operational",
    };

    /// <summary>#198: null when there's nothing worth recommending for this channel - either its
    /// effective retention already covers the app's 30-day lookback, its config wasn't readable at
    /// all, or it's disabled but not one of the handful of diagnostic channels this app has a
    /// specific reason to suggest enabling. SuggestedMaxSizeBytes is a simple doubling of the
    /// current configured size (rounded up to the required 64KB alignment) - a rough estimate, not a
    /// precise days-to-bytes projection, stated as such in the confirmation text the caller shows.</summary>
    public static RetentionRecommendation? GetRetentionRecommendation(ChannelHealthInfo health)
    {
        if (!health.IsConfigReadable) return null;

        if (health.IsEnabled == false && KnownDiagnosticChannels.Contains(health.ChannelName))
        {
            return new RetentionRecommendation
            {
                ChannelName = health.ChannelName,
                CurrentRetentionDays = 0,
                CurrentMaxSizeBytes = health.MaxSizeBytes ?? 0,
                SuggestedMaxSizeBytes = health.MaxSizeBytes ?? 0,
                SuggestEnabling = true,
            };
        }

        if (health.IsEnabled == true && health.MaxSizeBytes is { } current && current > 0
            && health.EffectiveRetentionDays is { } days && days < TargetLookbackDays)
        {
            long suggested = RoundUpToAlignment(current * 2, MinRoundingUnitBytes);
            if (suggested <= current) return null; // nothing further to suggest

            return new RetentionRecommendation
            {
                ChannelName = health.ChannelName,
                CurrentRetentionDays = days,
                CurrentMaxSizeBytes = current,
                SuggestedMaxSizeBytes = suggested,
                SuggestEnabling = false,
            };
        }

        return null;
    }

    private static long RoundUpToAlignment(long value, long alignment)
        => (value + alignment - 1) / alignment * alignment;

    /// <summary>#198: `wevtutil sl "&lt;channel&gt;" /ms:&lt;bytes&gt;` - the actual registry-affecting
    /// write. Performs no confirmation of its own (see class remarks); the caller must have already
    /// shown the explicit MessageBox and recorded the previous value via AppendConfigChange before
    /// calling this.</summary>
    public static async Task<(bool Success, string Output)> ApplyMaxSizeAsync(string channelName, long newSizeBytes, CancellationToken ct = default)
        => await RunWevtutilAsync($"sl \"{channelName}\" /ms:{newSizeBytes}", ct);

    /// <summary>#198: `wevtutil sl "&lt;channel&gt;" /e:true` - enables a disabled channel.</summary>
    public static async Task<(bool Success, string Output)> EnableChannelAsync(string channelName, CancellationToken ct = default)
        => await RunWevtutilAsync($"sl \"{channelName}\" /e:true", ct);

    /// <summary>#198: `wevtutil sl "&lt;channel&gt;" /e:false` - the revert counterpart to
    /// EnableChannelAsync above.</summary>
    public static async Task<(bool Success, string Output)> DisableChannelAsync(string channelName, CancellationToken ct = default)
        => await RunWevtutilAsync($"sl \"{channelName}\" /e:false", ct);

    private static async Task<(bool Success, string Output)> RunWevtutilAsync(string args, CancellationToken ct)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("wevtutil.exe", args, 15000, ct);
            if (exitCode is null) return (false, "wevtutil.exe timed out.");
            return (exitCode == 0, string.IsNullOrWhiteSpace(output) ? "OK" : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- #198 persisted change log: event-log-config.json, same shape as every other settings
    // file in this app (silent fallback to defaults on a missing/corrupt file). Unlike WerReportService's
    // single-backup-slot shape (#165), this channel can have several independent changes outstanding
    // at once (different channels, or both a size and an enabled change on the same channel), so it's
    // a growing list rather than one slot. ----

    private static string ConfigPath => AppPaths.GetPath("event-log-config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<EventLogConfigChangeRecord> LoadConfigChanges()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new List<EventLogConfigChangeRecord>();
            var settings = JsonSerializer.Deserialize<EventLogConfigSettings>(File.ReadAllText(ConfigPath));
            return settings?.Changes ?? new List<EventLogConfigChangeRecord>();
        }
        catch
        {
            // Missing/corrupt file - degrade to "nothing recorded", same as every other settings
            // file in this app.
            return new List<EventLogConfigChangeRecord>();
        }
    }

    private static void SaveConfigChanges(List<EventLogConfigChangeRecord> changes)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new EventLogConfigSettings { Changes = changes }, JsonOptions));
        }
        catch { /* best-effort - worst case only the in-session state survives */ }
    }

    /// <summary>Records one applied change (called right after a successful ApplyMaxSizeAsync/
    /// EnableChannelAsync) - replaces any existing record for the same (channel, changeType) rather
    /// than appending a duplicate, so "revert" always restores the value from *before this app's
    /// first* change to that channel/property, not an intermediate one.</summary>
    public static void RecordConfigChange(string channelName, EventLogConfigChangeType changeType, string previousValue, string newValue)
    {
        var changes = LoadConfigChanges();
        // Keep the earliest PreviousValue already on file for this (channel, type) if one exists -
        // otherwise a second raise-then-raise-again on the same channel would overwrite the true
        // "before this app touched it" baseline with an intermediate value.
        var existing = changes.FirstOrDefault(c => string.Equals(c.ChannelName, channelName, StringComparison.OrdinalIgnoreCase) && c.ChangeType == changeType);
        string baselinePrevious = existing?.PreviousValue ?? previousValue;
        changes.RemoveAll(c => string.Equals(c.ChannelName, channelName, StringComparison.OrdinalIgnoreCase) && c.ChangeType == changeType);
        changes.Add(new EventLogConfigChangeRecord { ChannelName = channelName, ChangeType = changeType, PreviousValue = baselinePrevious, NewValue = newValue, Timestamp = DateTime.Now });
        SaveConfigChanges(changes);
    }

    /// <summary>Removes the recorded change for (channel, type) - called after a successful revert,
    /// so a channel this app already put back the way it found it doesn't keep showing a stale
    /// "revert" option.</summary>
    public static void RemoveConfigChange(string channelName, EventLogConfigChangeType changeType)
    {
        var changes = LoadConfigChanges();
        changes.RemoveAll(c => string.Equals(c.ChannelName, channelName, StringComparison.OrdinalIgnoreCase) && c.ChangeType == changeType);
        SaveConfigChanges(changes);
    }

    public static EventLogConfigChangeRecord? FindConfigChange(string channelName, EventLogConfigChangeType changeType)
        => LoadConfigChanges().FirstOrDefault(c => string.Equals(c.ChannelName, channelName, StringComparison.OrdinalIgnoreCase) && c.ChangeType == changeType);

    // ==================== #199: log-clearing and gap detection ====================

    /// <summary>#199: System event 104 ("the System log was cleared") and Security event 1102 ("the
    /// audit log was cleared"), each scoped to the specific channel it's about (a clear is always
    /// logged into the log it cleared, so no provider-name guess is needed - just the right channel
    /// + event ID). A cleared log must be flagged prominently, never silently folded into "no
    /// problems found" - see StabilityViewModel/StabilityView.xaml for where this surfaces.</summary>
    public List<LogClearEvent> DetectLogClearEvents(int lookbackDays = 30)
    {
        var results = new List<LogClearEvent>();
        results.AddRange(ReadClearEvents("System", 104, "System"));
        results.AddRange(ReadClearEvents("Security", 1102, "Security"));
        return results.OrderByDescending(e => e.TimeCreated).ToList();

        List<LogClearEvent> ReadClearEvents(string channel, int eventId, string clearedName)
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            string xpath = $"*[System[(EventID={eventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";
            try
            {
                var result = _explorer.ReadPage(channel, xpath, null, 200);
                if (result.ErrorText is not null) return new List<LogClearEvent>();

                return result.Rows.Select(r => new LogClearEvent
                {
                    TimeCreated = r.TimeCreated,
                    SourceChannel = channel,
                    ClearedChannelName = ExtractClearedLogName(r.Message) ?? clearedName,
                    Account = _explorer.ResolveUserAccount(r.UserSid),
                }).ToList();
            }
            catch { return new List<LogClearEvent>(); }
        }
    }

    private static readonly Regex ClearedLogNameRegex = new(@"The\s+(.+?)\s+log (?:file )?was cleared", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ExtractClearedLogName(string message)
    {
        var match = ClearedLogNameRegex.Match(message);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // Heuristic thresholds for #199's gap flag - deliberately conservative (a wide margin over the
    // sample's own median inter-record time) since a channel that simply logs rarely can show a wide
    // *legitimate* gap too. "Quick flag, not a verdict."
    private const double GapMedianMultiplier = 20.0;
    private static readonly TimeSpan MinFlaggedGap = TimeSpan.FromHours(6);

    /// <summary>#199: reads the most recent <paramref name="sampleSize"/> records of one channel
    /// (the same bounded, single-page shape every other on-demand scan in this app uses - never a
    /// full-log walk) and flags: (1) any record-ID discontinuity between consecutive records - a
    /// missing range almost always means the log rotated/was trimmed since those records were
    /// written; (2) any time gap between consecutive records much larger than this sample's own
    /// typical inter-record spacing; (3) an EventID 104 found anywhere in the sample (a clear that
    /// happened recently enough to still be within the sampled page, surfaced here too as a second
    /// path to the same #199 signal besides DetectLogClearEvents above, for whichever channel the
    /// user is actually looking at - not just System/Security).</summary>
    public List<LogGapFlag> DetectRecordGaps(string channelName, int sampleSize = 500)
    {
        var flags = new List<LogGapFlag>();
        EventLogExplorerService.EventReadResult result;
        try { result = _explorer.ReadPage(channelName, "*", null, sampleSize); }
        catch { return flags; }
        if (result.ErrorText is not null) return flags;

        var rows = result.Rows; // newest-first (ReadPage uses ReverseDirection=true)
        if (rows.Count < 2) return flags;

        foreach (var row in rows.Where(r => r.EventId == 104))
        {
            flags.Add(new LogGapFlag
            {
                ChannelName = channelName,
                Description = $"This log was cleared at {row.TimeCreated:g} - any history before that point is gone.",
                FromTime = row.TimeCreated,
                ToTime = row.TimeCreated,
            });
        }

        var deltas = new List<double>();
        for (int i = 0; i < rows.Count - 1; i++)
        {
            var gap = rows[i].TimeCreated - rows[i + 1].TimeCreated;
            if (gap > TimeSpan.Zero) deltas.Add(gap.TotalSeconds);
        }
        double medianSeconds = deltas.Count == 0 ? 0 : Median(deltas);
        double thresholdSeconds = Math.Max(medianSeconds * GapMedianMultiplier, MinFlaggedGap.TotalSeconds);

        for (int i = 0; i < rows.Count - 1; i++)
        {
            long? idNewer = rows[i].RecordId;
            long? idOlder = rows[i + 1].RecordId;
            if (idNewer is { } a && idOlder is { } b && a - b > 1)
            {
                flags.Add(new LogGapFlag
                {
                    ChannelName = channelName,
                    Description = $"Record IDs {b + 1}-{a - 1} ({a - b - 1} record(s)) are missing between these two entries - the log likely rotated or was trimmed here.",
                    FromTime = rows[i + 1].TimeCreated,
                    ToTime = rows[i].TimeCreated,
                });
            }

            var gapSeconds = (rows[i].TimeCreated - rows[i + 1].TimeCreated).TotalSeconds;
            if (gapSeconds > thresholdSeconds)
            {
                flags.Add(new LogGapFlag
                {
                    ChannelName = channelName,
                    Description = $"No records between {rows[i + 1].TimeCreated:g} and {rows[i].TimeCreated:g} - much longer than this log's typical spacing.",
                    FromTime = rows[i + 1].TimeCreated,
                    ToTime = rows[i].TimeCreated,
                });
            }
        }

        return flags.OrderByDescending(f => f.ToTime).Take(10).ToList();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
