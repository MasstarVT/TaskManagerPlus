using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #101-107: backs the "Events" tab - a real Event Viewer replacement (channel tree + paged grid +
/// detail pane + XPath filter builder + live tail), separate from the existing EventLogService
/// (which stays exactly as-is, feeding the Stability tab's fixed 60-row Critical/Error digest -
/// see that class's remarks). Every public method here follows the same "degrade to
/// empty/Unknown/inaccessible, never throw out of the service, never fabricate a value" rule the
/// rest of this app's Services/ layer uses - a locked-down channel, a missing provider message
/// file, or a denied SID translation are all real, expected conditions here, not bugs.
/// </summary>
public sealed class EventLogExplorerService
{
    private static readonly HashSet<string> ClassicWindowsLogs =
        new(StringComparer.OrdinalIgnoreCase) { "Application", "Security", "Setup", "System", "ForwardedEvents" };

    // Level 1..5 = Critical/Error/Warning/Information/Verbose - EventRecord.Level is sometimes 0
    // ("LogAlways", no explicit level set by the provider), which LevelDisplayName can render as
    // an empty string; this table backs the client-side fallback used when that happens.
    private static readonly Dictionary<int, string> LevelNames = new()
    {
        [0] = "Information",
        [1] = "Critical",
        [2] = "Error",
        [3] = "Warning",
        [4] = "Information",
        [5] = "Verbose",
    };

    // #106: SecurityIdentifier.Translate does a domain/AD round-trip per unique SID - cached for
    // the lifetime of this service instance so re-selecting rows for the same account (the common
    // case - most events on one machine come from a handful of accounts) doesn't repeat it.
    private readonly Dictionary<string, string> _sidTranslationCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>#102: every registered channel, grouped into Windows Logs / Applications and
    /// Services / Analytic-Debug, each annotated with record count + last-write time where
    /// readable. Enumeration itself (GetLogNames) is one call; a channel that can't be inspected
    /// past its bare name becomes a greyed "no access" leaf instead of being dropped.</summary>
    public List<EventChannelNode> GetChannelTree()
    {
        var windowsLogs = new List<EventChannelNode>();
        var appsAndServices = new List<EventChannelNode>();
        var analyticDebug = new List<EventChannelNode>();

        IEnumerable<string> names;
        try
        {
            names = EventLogSession.GlobalSession.GetLogNames().ToList();
        }
        catch
        {
            // No access to enumerate logs at all (unusual - this app runs elevated) - empty tree.
            return new List<EventChannelNode>();
        }

        foreach (var name in names)
        {
            var node = BuildChannelNode(name);
            var bucket = node.Group switch
            {
                EventChannelGroup.WindowsLogs => windowsLogs,
                EventChannelGroup.AnalyticDebug => analyticDebug,
                _ => appsAndServices,
            };
            bucket.Add(node);
        }

        var result = new List<EventChannelNode>();
        if (windowsLogs.Count > 0) result.Add(MakeGroupNode("Windows Logs", EventChannelGroup.WindowsLogs, windowsLogs));
        if (appsAndServices.Count > 0) result.Add(MakeGroupNode("Applications and Services", EventChannelGroup.AppsAndServices, appsAndServices));
        if (analyticDebug.Count > 0) result.Add(MakeGroupNode("Analytic and Debug Logs", EventChannelGroup.AnalyticDebug, analyticDebug));
        return result;
    }

    private static EventChannelNode MakeGroupNode(string name, EventChannelGroup group, List<EventChannelNode> children)
    {
        children.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new EventChannelNode { Name = name, DisplayName = name, Group = group, IsGroup = true, IsAccessible = true, Children = children };
    }

    private static EventChannelNode BuildChannelNode(string name)
    {
        var group = ClassicWindowsLogs.Contains(name) ? EventChannelGroup.WindowsLogs : EventChannelGroup.AppsAndServices;

        bool? isEnabled = null;
        long? maxSizeBytes = null;
        try
        {
            using var config = new EventLogConfiguration(name);
            if (config.LogType is EventLogType.Analytical or EventLogType.Debug)
                group = EventChannelGroup.AnalyticDebug;
            isEnabled = config.IsEnabled;
            maxSizeBytes = config.MaximumSizeInBytes;
        }
        catch
        {
            // Config unreadable for this channel - keep the name-based guess above; the
            // accessibility check right below is what actually decides "no access" display. #135's
            // silent-channel flag below just has less to go on (isEnabled/maxSizeBytes stay null).
        }

        try
        {
            var info = EventLogSession.GlobalSession.GetLogInformation(name, PathType.LogName);
            return new EventChannelNode
            {
                Name = name,
                DisplayName = name,
                Group = group,
                IsAccessible = true,
                RecordCount = info.RecordCount,
                LastWriteTime = info.LastWriteTime,
                MaxSizeBytes = maxSizeBytes,
                IsSilent = IsChannelSilent(isEnabled, maxSizeBytes, info.LastWriteTime, info.RecordCount),
            };
        }
        catch
        {
            // Access denied / channel not actually openable - #102: show a greyed no-access node
            // rather than silently dropping it from the tree.
            return new EventChannelNode { Name = name, DisplayName = name, Group = group, IsAccessible = false };
        }
    }

    // #135: a "non-trivial" configured max size - below this, a channel that's never written
    // anything isn't worth flagging (a lot of near-unused Analytic/Debug channels ship with a tiny
    // default cap and are *supposed* to sit empty until someone explicitly enables tracing on them).
    private const long SilentChannelNonTrivialMaxSizeBytes = 1024 * 1024; // 1 MB
    private const int SilentChannelStaleDays = 14;

    /// <summary>#135: flags a channel that's enabled, has a real configured capacity, and hasn't
    /// written a record in a long time (or ever) - usually a broken provider registration or a
    /// corrupt .evtx, not "this channel is just quiet." Disabled channels and channels with only a
    /// token max size are never flagged - there's nothing actionable to say about either.</summary>
    private static bool IsChannelSilent(bool? isEnabled, long? maxSizeBytes, DateTime? lastWriteTime, long? recordCount)
    {
        if (isEnabled != true) return false;
        if (maxSizeBytes is not { } size || size < SilentChannelNonTrivialMaxSizeBytes) return false;

        if (recordCount is > 0)
            return lastWriteTime is { } lw && (DateTime.Now - lw).TotalDays >= SilentChannelStaleDays;

        // Zero records ever written despite being enabled with real configured capacity.
        return true;
    }

    /// <summary>#104: turns a structured filter into the same style of "*[System[...]]" XPath
    /// EventLogService.ReadLog already hardcodes for its one fixed query - here composed from
    /// whatever the filter bar's controls currently hold, and handed back as plain text so it can
    /// be shown (and hand-edited) in the query box.</summary>
    public static string BuildXPath(EventFilterCriteria criteria)
    {
        var clauses = new List<string>();

        if (criteria.Levels.Count > 0)
            clauses.Add("(" + string.Join(" or ", criteria.Levels.OrderBy(l => l).Select(l => $"Level={l}")) + ")");

        if (criteria.Providers.Count > 0)
            clauses.Add("(" + string.Join(" or ", criteria.Providers.Select(p => $"Provider[@Name={QuoteXPathLiteral(p)}]")) + ")");

        if (criteria.EventIds.Count > 0 || criteria.EventIdRanges.Count > 0)
        {
            var idClauses = criteria.EventIds.Select(id => $"EventID={id}")
                .Concat(criteria.EventIdRanges.Select(r => $"(EventID>={r.From} and EventID<={r.To})"));
            clauses.Add("(" + string.Join(" or ", idClauses) + ")");
        }

        // Raw XPath text handed straight to EventLogQuery (a plain C# string, not parsed out of an
        // XML document) - so comparison operators are literal '<'/'>', matching
        // EventLogService.ReadLog's existing "timediff(@SystemTime) <= N" query.
        if (criteria.StartTimeUtc is { } start)
            clauses.Add($"TimeCreated[@SystemTime>='{start:o}']");
        if (criteria.EndTimeUtc is { } end)
            clauses.Add($"TimeCreated[@SystemTime<='{end:o}']");
        else if (criteria.LookbackDays is { } days && days > 0)
            clauses.Add($"TimeCreated[timediff(@SystemTime) <= {days * 24L * 60 * 60 * 1000}]");

        if (!string.IsNullOrWhiteSpace(criteria.UserSid))
            clauses.Add($"Security[@UserID={QuoteXPathLiteral(criteria.UserSid)}]");

        if (clauses.Count == 0) return "*";
        return "*[System[" + string.Join(" and ", clauses) + "]]";
    }

    /// <summary>Wraps a value as an XPath 1.0 string literal. XPath has no in-literal escape
    /// sequence, so a value containing a single quote is instead wrapped in double quotes (and one
    /// containing both, the vanishingly rare case, falls back to stripping single quotes rather
    /// than emitting an unparseable query). Public (not just used by BuildXPath above) since #136's
    /// watchlist builds its own provider+eventId XPath clauses the same way, rather than duplicating
    /// this escaping logic.</summary>
    public static string QuoteXPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return $"'{value.Replace("'", "")}'";
    }

    public sealed class EventReadResult
    {
        public List<EventRecordRow> Rows { get; init; } = new();
        public EventBookmark? Bookmark { get; init; }
        public bool HasMore { get; init; }
        public string? ErrorText { get; init; }
    }

    /// <summary>#103: reads one page (~500 records by default) starting after <paramref
    /// name="bookmark"/> (null = start from the newest record, since ReverseDirection=true), on
    /// whatever thread the caller runs this on - callers use Task.Run, same as every other
    /// on-demand service call in this app. Keeps at most one page's worth of EventRecord objects
    /// alive at a time rather than materializing the whole log, so a 100MB channel opens as fast
    /// as its first ~500 records instead of being read in full up front.</summary>
    public EventReadResult ReadPage(string channelName, string xpath, EventBookmark? bookmark, int pageSize = 500, PathType pathType = PathType.LogName)
    {
        var rows = new List<EventRecordRow>(pageSize);
        EventBookmark? lastBookmark = bookmark;
        try
        {
            var query = new EventLogQuery(channelName, pathType, xpath) { ReverseDirection = true };
            using var reader = bookmark is null ? new EventLogReader(query) : new EventLogReader(query, bookmark);

            // Continuing from a bookmark re-returns the bookmarked record itself first (documented
            // EventLogReader behavior) - skip it so paging doesn't repeat the last row of the
            // previous page.
            bool skipFirst = bookmark is not null;
            int count = 0;
            while (count < pageSize)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;

                if (skipFirst)
                {
                    skipFirst = false;
                    lastBookmark = SafeBookmark(record) ?? lastBookmark;
                    continue;
                }

                rows.Add(ConvertRecord(record, channelName));
                lastBookmark = SafeBookmark(record) ?? lastBookmark;
                count++;
            }

            // Approximate: a full page suggests there may be more; an under-full page (including
            // zero) means the query ran dry. Not a precise "exactly N left" count, just enough to
            // decide whether to offer "load more".
            return new EventReadResult { Rows = rows, Bookmark = lastBookmark, HasMore = rows.Count == pageSize };
        }
        catch (Exception ex)
        {
            return new EventReadResult { ErrorText = ex.Message };
        }
    }

    private static EventBookmark? SafeBookmark(EventRecord record)
    {
        try { return record.Bookmark; }
        catch { return null; }
    }

    private EventRecordRow ConvertRecord(EventRecord record, string channelName)
    {
        // #112: a multi-channel structured query (ReadMultiChannel) has no single "the channel" -
        // callers pass string.Empty for channelName in that case and this falls back to the
        // record's own LogName (which EventRecord always knows, regardless of query shape) so each
        // row still shows which channel it actually came from.
        string actualChannel = channelName;
        try
        {
            var logName = record.LogName;
            if (!string.IsNullOrEmpty(logName)) actualChannel = logName;
        }
        catch { /* keep whatever channelName the caller already knew */ }

        Guid? activityId = null;
        try { activityId = record.ActivityId; } catch { }
        Guid? relatedActivityId = null;
        try { relatedActivityId = record.RelatedActivityId; } catch { }

        string message;
        try { message = record.FormatDescription() ?? string.Empty; }
        catch { message = string.Empty; } // provider's message file isn't registered - #105 falls back to raw XML

        string rawXml;
        try { rawXml = record.ToXml(); }
        catch { rawXml = string.Empty; }

        var properties = new List<string>();
        try
        {
            foreach (var p in record.Properties) properties.Add(p.Value?.ToString() ?? string.Empty);
        }
        catch { /* leave whatever was collected before the failure */ }

        string? userSid = null;
        try { userSid = record.UserId?.Value; }
        catch { }

        int? processId = null;
        try { processId = record.ProcessId; }
        catch { }

        long? recordId = null;
        try { recordId = record.RecordId; }
        catch { }

        string level;
        try { level = record.LevelDisplayName ?? string.Empty; }
        catch { level = string.Empty; }
        if (string.IsNullOrWhiteSpace(level)) level = LevelNames.TryGetValue(record.Level ?? 0, out var name) ? name : "Unknown";

        string task;
        try { task = record.TaskDisplayName ?? string.Empty; }
        catch { task = string.Empty; }

        string opcode;
        try { opcode = record.OpcodeDisplayName ?? string.Empty; }
        catch { opcode = string.Empty; }

        return new EventRecordRow
        {
            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
            ChannelName = actualChannel,
            Level = level,
            LevelValue = record.Level ?? 0,
            ProviderName = record.ProviderName ?? string.Empty,
            EventId = record.Id,
            Task = task,
            Opcode = opcode,
            RecordId = recordId,
            ProcessId = processId,
            UserSid = userSid,
            Message = message,
            RawXml = rawXml,
            PropertyValues = properties,
            Bookmark = SafeBookmark(record),
            ActivityId = activityId,
            RelatedActivityId = relatedActivityId,
        };
    }

    /// <summary>#106: SecurityIdentifier -&gt; "DOMAIN\account" (or just the account name for a
    /// local/well-known SID). Falls back to returning the raw SID string unchanged when
    /// translation fails (deleted account, no network path to the domain, etc.) - never guesses a
    /// name.</summary>
    public string ResolveUserAccount(string? sidString)
    {
        if (string.IsNullOrWhiteSpace(sidString)) return "Unknown";
        if (_sidTranslationCache.TryGetValue(sidString, out var cached)) return cached;

        string resolved = sidString;
        try
        {
            var sid = new SecurityIdentifier(sidString);
            resolved = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
        }
        catch
        {
            // Deleted account / untranslatable well-known SID / no domain reachable - the raw SID
            // is still meaningful to show, just not a friendly name.
        }

        _sidTranslationCache[sidString] = resolved;
        return resolved;
    }

    /// <summary>An open EventLogWatcher (#107), handed back as a disposable handle so the
    /// ViewModel doesn't need to reference System.Diagnostics.Eventing.Reader types directly.
    /// Disposing stops the subscription (Enabled=false then Dispose) - safe to call more than
    /// once.</summary>
    public sealed class EventWatchHandle : IDisposable
    {
        private readonly EventLogWatcher _watcher;
        private bool _disposed;

        internal EventWatchHandle(EventLogWatcher watcher) => _watcher = watcher;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _watcher.Enabled = false; } catch { /* best-effort */ }
            _watcher.Dispose();
        }
    }

    /// <summary>#107: push-subscribes to new records on one channel matching an XPath filter -
    /// used for the "Follow" live-tail toggle. onRecord fires once per new record (on a
    /// worker/threadpool thread - the caller marshals to the UI thread); onError fires once if the
    /// subscription itself can't be created, or if a later read failure is reported through
    /// EventRecordWrittenEventArgs.EventException. Returns null (no handle) if the watcher
    /// couldn't even be constructed - callers should treat that as "Follow" failing to turn on.</summary>
    public EventWatchHandle? StartWatch(string channelName, string xpath, Action<EventRecordRow> onRecord, Action<string>? onError = null)
    {
        try
        {
            var query = new EventLogQuery(channelName, PathType.LogName, xpath);
            var watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += (_, e) =>
            {
                if (e.EventException is not null)
                {
                    onError?.Invoke(e.EventException.Message);
                    return;
                }
                if (e.EventRecord is null) return;
                using var record = e.EventRecord;
                try { onRecord(ConvertRecord(record, channelName)); }
                catch { /* one bad conversion shouldn't kill the subscription */ }
            };
            watcher.Enabled = true;
            return new EventWatchHandle(watcher);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return null;
        }
    }

    // ---- #109: import/export Event Viewer custom views ----

    private static string CustomViewsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Event Viewer", "Views");

    /// <summary>#109: parses every *.xml file under %ProgramData%\Microsoft\Event Viewer\Views -
    /// the same folder Event Viewer itself stores "Create Custom View..." definitions in - and
    /// offers each as an importable saved filter, so a view a user already built in the real Event
    /// Viewer works here without re-authoring its XPath by hand. A file that isn't the expected
    /// ViewerConfig shape, or fails to parse at all, is skipped rather than aborting the whole
    /// scan (same "one bad item shouldn't drop the rest" pattern GetProviderMetadata below uses).</summary>
    public List<ImportableCustomView> GetImportableCustomViews()
    {
        var results = new List<ImportableCustomView>();
        string dir;
        try { dir = CustomViewsDirectory; } catch { return results; }
        if (!Directory.Exists(dir)) return results;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.xml"); }
        catch { return results; }

        foreach (var file in files)
        {
            try
            {
                var doc = XDocument.Load(file);
                var queryNode = doc.Root?.Element("QueryConfig")?.Element("QueryNode");
                if (queryNode is null) continue;

                string name = queryNode.Element("Name")?.Value?.Trim() is { Length: > 0 } n
                    ? n
                    : Path.GetFileNameWithoutExtension(file);

                var selects = queryNode.Element("QueryList")?.Elements("Query").Elements("Select").ToList()
                    ?? new List<XElement>();
                if (selects.Count == 0) continue;

                var channels = selects
                    .Select(s => s.Attribute("Path")?.Value ?? string.Empty)
                    .Where(c => c.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // Event Viewer repeats (usually) the same XPath fragment per <Select> when one view
                // spans several channels - the first one is a reasonable single XPath to carry
                // forward into this app's one-XPath-per-filter model.
                string xpath = selects[0].Value?.Trim() is { Length: > 0 } x ? x : "*";

                results.Add(new ImportableCustomView { Name = name, Channels = channels, XPath = xpath, SourceFilePath = file });
            }
            catch
            {
                // Not a recognizable custom-view XML (or unreadable/locked) - skip this file, keep scanning.
            }
        }
        return results;
    }

    /// <summary>#109 export half: writes a filter as a minimal Event-Viewer-compatible custom view
    /// XML, so a filter built in this app shows up under Event Viewer's own "Custom Views" node
    /// too. Covers the subset of ViewerConfig/QueryConfig Event Viewer actually needs to open a
    /// view (Name + one &lt;Select&gt; per channel) - not every field Event Viewer's own "New
    /// Custom View" wizard can produce, since this app has no equivalent UI for those (e.g.
    /// by-source event selection).</summary>
    public bool ExportCustomView(SavedEventFilter filter, string path)
    {
        try
        {
            var channels = filter.Channels.Count > 0 ? filter.Channels : new List<string> { "System" };
            var queryList = new XElement("QueryList",
                new XElement("Query", new XAttribute("Id", "0"), new XAttribute("Path", channels[0]),
                    channels.Select(c => new XElement("Select", new XAttribute("Path", c), filter.XPath))));

            var viewerConfig = new XElement("ViewerConfig",
                new XElement("QueryConfig",
                    new XElement("QueryParams", new XElement("UserQuery")),
                    new XElement("QueryNode",
                        new XElement("Name", filter.Name),
                        new XElement("Description", "Exported from Task Manager Plus"),
                        queryList)));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, new XDocument(viewerConfig).ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- #110: open an external .evtx file ----

    /// <summary>#110: the %SystemRoot%\System32\Winevt\Logs\Archive-*.evtx autobackup files Windows
    /// creates when a channel with auto-backup logging hits its size cap - offered as a "Recent
    /// archives" quick-pick next to the general Open .evtx file picker. Sorted newest-first;
    /// returns an empty list (never throws) if the Logs folder can't be enumerated at all.</summary>
    public List<RecentArchiveEntry> GetRecentArchives()
    {
        var results = new List<RecentArchiveEntry>();
        try
        {
            string logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Winevt", "Logs");
            if (!Directory.Exists(logsDir)) return results;

            foreach (var file in Directory.EnumerateFiles(logsDir, "Archive-*.evtx"))
            {
                try
                {
                    var info = new FileInfo(file);
                    results.Add(new RecentArchiveEntry
                    {
                        Path = file,
                        FileName = info.Name,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                        SizeBytes = info.Length,
                    });
                }
                catch { /* one unreadable archive file shouldn't drop the rest */ }
            }
        }
        catch { /* Logs folder unreadable - empty list */ }

        results.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        return results;
    }

    /// <summary>#110: builds a channel-tree leaf for an arbitrary .evtx file (picked via
    /// OpenFileDialog or one of the recent-archives quick-picks), the same shape ReadPage/
    /// StartWatch already understand via PathType.FilePath. Returns null if the file doesn't exist
    /// or can't even be opened for a basic info read - degrade to "can't open this", never a
    /// half-populated node.</summary>
    public EventChannelNode? OpenEvtxFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var info = EventLogSession.GlobalSession.GetLogInformation(path, PathType.FilePath);
            return new EventChannelNode
            {
                Name = path,
                DisplayName = Path.GetFileName(path),
                Group = EventChannelGroup.AppsAndServices,
                IsAccessible = true,
                IsFilePath = true,
                RecordCount = info.RecordCount,
                LastWriteTime = info.LastWriteTime,
            };
        }
        catch
        {
            // Corrupt file / not a valid .evtx / locked by another process - nothing usable to show.
            return null;
        }
    }

    // ---- #111: cross-channel full-text search (button-gated) ----

    public sealed class CrossChannelSearchProgress
    {
        public string CurrentChannel { get; init; } = string.Empty;
        public int ChannelsCompleted { get; init; }
        public int ChannelsTotal { get; init; }
        public int MatchesSoFar { get; init; }
    }

    /// <summary>#111: an explicit, button-gated sweep across every readable channel - level/time
    /// bounded via <paramref name="xpath"/> (built by the caller from the same filter bar a
    /// single-channel query uses), then a client-side substring or regex match against each
    /// record's formatted description (full-text search isn't reliably expressible as XPath across
    /// every provider's schema - same reasoning EventFilterCriteria.Keyword's remarks give for the
    /// single-channel case). Capped per-channel and in total, and cooperatively cancellable via
    /// <paramref name="ct"/> - this is a "quick flag, not an exhaustive audit" sweep of a busy
    /// machine's logs, not a guarantee every matching record was seen, which is exactly why it's
    /// gated behind an explicit button rather than ever running on a tick.</summary>
    public List<EventRecordRow> SearchAllChannels(string xpath, string? keyword, bool isRegex, int maxPerChannel, int maxTotalResults, IProgress<CrossChannelSearchProgress>? progress, CancellationToken ct)
    {
        var results = new List<EventRecordRow>();

        List<string> channels;
        try
        {
            channels = EventLogSession.GlobalSession.GetLogNames()
                .Where(n =>
                {
                    try { return EventLogSession.GlobalSession.GetLogInformation(n, PathType.LogName).RecordCount is > 0; }
                    catch { return false; }
                })
                .ToList();
        }
        catch { return results; }

        Regex? regex = null;
        if (isRegex && !string.IsNullOrWhiteSpace(keyword))
        {
            try { regex = new Regex(keyword, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { return results; } // invalid pattern - degrade to no results rather than throw
        }

        int completed = 0;
        foreach (var channel in channels)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CrossChannelSearchProgress { CurrentChannel = channel, ChannelsCompleted = completed, ChannelsTotal = channels.Count, MatchesSoFar = results.Count });

            try
            {
                var query = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                int readInChannel = 0;
                while (readInChannel < maxPerChannel && results.Count < maxTotalResults)
                {
                    if (readInChannel % 200 == 0) ct.ThrowIfCancellationRequested();

                    using var record = reader.ReadEvent();
                    if (record is null) break;
                    readInChannel++;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    bool isMatch = string.IsNullOrWhiteSpace(keyword)
                        || (regex is not null ? regex.IsMatch(message) : message.Contains(keyword!, StringComparison.OrdinalIgnoreCase));
                    if (isMatch) results.Add(ConvertRecord(record, channel));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // This one channel became unreadable mid-sweep (permissions, cleared/rotated
                // between the tree scan and now, etc.) - skip it, keep going with the rest.
            }

            completed++;
            if (results.Count >= maxTotalResults) break;
        }

        progress?.Report(new CrossChannelSearchProgress { CurrentChannel = "Done", ChannelsCompleted = completed, ChannelsTotal = channels.Count, MatchesSoFar = results.Count });
        return results;
    }

    // ---- #112: one structured query across several channels at once ----

    /// <summary>#112: builds an EvtQuery structured-XML string with one &lt;Select Path="..."&gt;
    /// per channel, so "all Errors from System + Application + ..." is a single time-ordered
    /// EventLogReader pass instead of N sequential ReadPage calls merged in memory afterward.
    /// Channel names and the XPath are inserted as XElement content/attributes, which handles all
    /// XML escaping (an XPath predicate routinely contains &lt;/&gt;/&amp;/quotes) rather than
    /// hand-building the string.</summary>
    public static string BuildStructuredQuery(IEnumerable<string> channels, string xpath)
    {
        var distinctChannels = channels
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var query = new XElement("Query", new XAttribute("Id", "0"));
        foreach (var channel in distinctChannels)
            query.Add(new XElement("Select", new XAttribute("Path", channel), xpath));

        return new XElement("QueryList", query).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>#112: reads one page from a structured multi-channel query built by
    /// BuildStructuredQuery - same paging/bookmark contract as ReadPage, just against
    /// PathType.LogName with path=null (the structured XML itself carries every channel). Each
    /// row's ChannelName comes back correctly per-record via ConvertRecord's LogName fallback,
    /// since a multi-channel result set has no single "the channel" to pass in up front.</summary>
    public EventReadResult ReadMultiChannel(string structuredXml, EventBookmark? bookmark, int pageSize = 500)
    {
        var rows = new List<EventRecordRow>(pageSize);
        EventBookmark? lastBookmark = bookmark;
        try
        {
            var query = new EventLogQuery(null, PathType.LogName, structuredXml) { ReverseDirection = true };
            using var reader = bookmark is null ? new EventLogReader(query) : new EventLogReader(query, bookmark);

            bool skipFirst = bookmark is not null;
            int count = 0;
            while (count < pageSize)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;

                if (skipFirst)
                {
                    skipFirst = false;
                    lastBookmark = SafeBookmark(record) ?? lastBookmark;
                    continue;
                }

                rows.Add(ConvertRecord(record, string.Empty));
                lastBookmark = SafeBookmark(record) ?? lastBookmark;
                count++;
            }

            return new EventReadResult { Rows = rows, Bookmark = lastBookmark, HasMore = rows.Count == pageSize };
        }
        catch (Exception ex)
        {
            return new EventReadResult { ErrorText = ex.Message };
        }
    }

    // ---- #113: provider catalog browser ----

    /// <summary>#113: every provider registered on this machine (session.GetProviderNames()),
    /// sorted for a browsable list - just names, cheap enough to call eagerly when the catalog
    /// panel opens (the expensive part is GetProviderMetadata per selected provider, below).</summary>
    public List<string> GetProviderNames()
    {
        try { return EventLogSession.GlobalSession.GetProviderNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>#113: every event ID one provider's manifest declares it can emit
    /// (ProviderMetadata.Events), with level/task/opcode/keywords/message-template plus which
    /// channels it writes to (ProviderMetadata.LogLinks) - the built-in, always-accurate answer to
    /// "what does provider X's event 129 even mean," needing no bundled lookup data. A provider
    /// with no locally-registered manifest (common for a remote-only or uninstalled provider name)
    /// comes back as an empty list, never a guessed one; one malformed event definition inside an
    /// otherwise-good manifest is skipped rather than dropping the whole catalog.</summary>
    public List<ProviderEventMetadataRow> GetProviderMetadata(string providerName)
    {
        var rows = new List<ProviderEventMetadataRow>();
        if (string.IsNullOrWhiteSpace(providerName)) return rows;

        try
        {
            using var metadata = new ProviderMetadata(providerName);

            var channels = new List<string>();
            try { channels = metadata.LogLinks.Select(l => l.LogName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { /* channel links unavailable - the per-event metadata below is still useful without it */ }
            string channelsJoined = string.Join(", ", channels);

            IEnumerable<EventMetadata> events;
            try { events = metadata.Events; }
            catch { return rows; } // no locally-registered manifest for this provider - empty, not fabricated

            foreach (var evt in events)
            {
                try
                {
                    rows.Add(new ProviderEventMetadataRow
                    {
                        EventId = (int)evt.Id,
                        Version = evt.Version,
                        Level = SafeDisplayName(() => evt.Level?.DisplayName, () => evt.Level?.Name),
                        Task = SafeDisplayName(() => evt.Task?.DisplayName, () => evt.Task?.Name),
                        Opcode = SafeDisplayName(() => evt.Opcode?.DisplayName, () => evt.Opcode?.Name),
                        Keywords = SafeKeywordList(evt),
                        Channels = channelsJoined,
                        Template = SafeTemplate(evt),
                    });
                }
                catch { /* one malformed event definition shouldn't drop the whole catalog */ }
            }
        }
        catch
        {
            // Provider not registered on this machine, or its metadata is otherwise inaccessible -
            // empty catalog, per #113's "machine-accurate, never fabricated" contract.
        }
        return rows;
    }

    private static string SafeDisplayName(Func<string?> primary, Func<string?> fallback)
    {
        try { if (primary() is { Length: > 0 } v) return v; } catch { }
        try { if (fallback() is { Length: > 0 } v) return v; } catch { }
        return "Unknown";
    }

    private static string SafeKeywordList(EventMetadata evt)
    {
        try
        {
            return string.Join(", ", evt.Keywords
                .Select(k => { try { return k.DisplayName ?? k.Name; } catch { return null; } })
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch { return string.Empty; }
    }

    private static string SafeTemplate(EventMetadata evt)
    {
        try { return evt.Template ?? string.Empty; } catch { return string.Empty; }
    }

    // ---- #119/#123: per-(provider,eventId) manifest lookup for the detail pane ----

    /// <summary>#119's provider-description fallback and #123's named-field labels, bundled
    /// together since both come from the same ProviderMetadata.Events scan - a null
    /// DescriptionTemplate/FieldNames means the manifest genuinely doesn't have one (unregistered
    /// provider, or an event with no &lt;template&gt;), never a guessed value.</summary>
    public sealed class ProviderEventDetail
    {
        public List<string>? FieldNames { get; init; }
        public string? DescriptionTemplate { get; init; }
    }

    private static readonly ProviderEventDetail EmptyProviderEventDetail = new();

    // Cached per (provider, eventId) for the lifetime of this service instance - re-selecting rows
    // for the same event ID (the common case when browsing one channel or one provider's worth of
    // noise) would otherwise repeat a full manifest scan (GetProviderMetadata's own "walk every
    // event this provider declares" cost) on every click.
    private readonly Dictionary<(string Provider, int EventId), ProviderEventDetail> _providerEventDetailCache = new();

    /// <summary>#119: the provider's own registered message-template description for one event ID -
    /// used as the detail pane's fallback "what this usually means" when the local knowledge base
    /// (#117) has no entry, so an unknown event still gets real Windows-authored text instead of
    /// "no information". #123: the same manifest lookup's &lt;template&gt; XML gives the real field
    /// names for EventRecord.Properties, turning today's "Property 0/Property 1" positional
    /// guessing into a labelled grid - falls back to positional naming (handled by the caller) when
    /// no template is registered for this event.</summary>
    public ProviderEventDetail GetProviderEventDetail(string providerName, int eventId)
    {
        var key = (providerName, eventId);
        if (_providerEventDetailCache.TryGetValue(key, out var cached)) return cached;

        var detail = EmptyProviderEventDetail;
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            try
            {
                using var metadata = new ProviderMetadata(providerName);
                var evt = metadata.Events.FirstOrDefault(e => (int)e.Id == eventId);
                if (evt is not null)
                {
                    string? description = null;
                    try { description = evt.Description; } catch { /* not every event has one */ }

                    List<string>? fieldNames = null;
                    string template = SafeTemplate(evt);
                    if (!string.IsNullOrWhiteSpace(template))
                    {
                        try
                        {
                            var doc = XDocument.Parse(template);
                            var names = doc.Descendants()
                                .Where(e => e.Name.LocalName == "data")
                                .Select(e => e.Attribute("name")?.Value)
                                .Where(n => !string.IsNullOrWhiteSpace(n))
                                .Select(n => n!)
                                .ToList();
                            if (names.Count > 0) fieldNames = names;
                        }
                        catch
                        {
                            // Template XML not well-formed / not the expected shape - fall back to
                            // positional naming rather than guessing field order from a partial parse.
                        }
                    }

                    detail = new ProviderEventDetail { FieldNames = fieldNames, DescriptionTemplate = description };
                }
            }
            catch
            {
                // Provider not locally registered, or its manifest is otherwise unreadable - no
                // field names, no description template; caller falls back to positional naming and
                // "no information" respectively, per #117's degrade-never-fabricate rule.
            }
        }

        _providerEventDetailCache[key] = detail;
        return detail;
    }
}
