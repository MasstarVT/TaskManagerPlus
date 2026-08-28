using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
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

        try
        {
            using var config = new EventLogConfiguration(name);
            if (config.LogType is EventLogType.Analytical or EventLogType.Debug)
                group = EventChannelGroup.AnalyticDebug;
        }
        catch
        {
            // Config unreadable for this channel - keep the name-based guess above; the
            // accessibility check right below is what actually decides "no access" display.
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
            };
        }
        catch
        {
            // Access denied / channel not actually openable - #102: show a greyed no-access node
            // rather than silently dropping it from the tree.
            return new EventChannelNode { Name = name, DisplayName = name, Group = group, IsAccessible = false };
        }
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
    /// than emitting an unparseable query).</summary>
    private static string QuoteXPathLiteral(string value)
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
    public EventReadResult ReadPage(string channelName, string xpath, EventBookmark? bookmark, int pageSize = 500)
    {
        var rows = new List<EventRecordRow>(pageSize);
        EventBookmark? lastBookmark = bookmark;
        try
        {
            var query = new EventLogQuery(channelName, PathType.LogName, xpath) { ReverseDirection = true };
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
            ChannelName = channelName,
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
}
