using System.Diagnostics.Eventing.Reader;

namespace TaskManagerPlus.Models;

/// <summary>
/// #101-108: models backing the Events tab (a full Event Viewer replacement, distinct from the
/// Stability tab's fixed 60-row Critical/Error digest - see EventLogExplorerService's remarks).
/// </summary>

/// <summary>Which of Event Viewer's three top-level buckets a channel belongs in (#102). Windows
/// Logs is the five classic named logs (Application/Security/Setup/System/ForwardedEvents);
/// Analytic-Debug is anything EventLogConfiguration reports as LogType Analytical or Debug (disabled
/// by default on a stock machine, high-volume when enabled); everything else - the hundreds of
/// per-component "Applications and Services Logs/..." operational/admin channels - falls into
/// AppsAndServices.</summary>
public enum EventChannelGroup
{
    WindowsLogs,
    AppsAndServices,
    AnalyticDebug,
}

/// <summary>One row in the left channel tree - either a top-level group heading (IsGroup=true,
/// Children populated) or a leaf channel (IsGroup=false, Name is the real channel name passed to
/// EventLogQuery/EventLogConfiguration). A leaf with IsAccessible=false means enumeration reached
/// the channel's name but a subsequent read (EventLogConfiguration or GetLogInformation) was denied
/// or threw - it is still shown, just greyed and non-queryable, rather than disappearing (#102's
/// "degrade to Unknown, never fabricate" rule applied to a tree node).</summary>
public sealed class EventChannelNode
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public EventChannelGroup Group { get; init; }
    public bool IsGroup { get; init; }
    public bool IsAccessible { get; init; } = true;
    public long? RecordCount { get; init; }
    public DateTime? LastWriteTime { get; init; }
    public List<EventChannelNode> Children { get; init; } = new();

    /// <summary>#110: true for a node representing an opened .evtx file (PathType.FilePath) rather
    /// than a live registered channel (PathType.LogName) - Name holds the full file path in that
    /// case. Read-only (no live tail: EventLogExplorerService.StartWatch is never called against a
    /// file-path node, since a static exported log never gets new records).</summary>
    public bool IsFilePath { get; init; }

    /// <summary>#112: plain mutable checkbox state for the channel tree's multi-select, backing
    /// "Multi-channel query" - not init-only like the rest of this node, since it's flipped by the
    /// tree's checkboxes after the node is built (no INotifyPropertyChanged needed here: the
    /// CheckBox's own two-way binding drives the visual, this is just where the value lands for the
    /// query-building code to read back).</summary>
    public bool IsSelectedForMulti { get; set; }
}

/// <summary>One event record as shown in the center grid (#103) and read by the detail pane
/// (#105/#106). Everything the detail pane needs (raw XML, positional property values, the raw
/// SID string) is captured up front while the EventRecord is still open, since EventLogReader
/// disposes each record right after it's read during paging - there's no "go re-fetch this record
/// later" once the page has moved on.</summary>
public sealed class EventRecordRow
{
    public DateTime TimeCreated { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public int LevelValue { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Task { get; init; } = string.Empty;
    public string Opcode { get; init; } = string.Empty;
    public long? RecordId { get; init; }
    public int? ProcessId { get; init; }
    public string? UserSid { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Full record.ToXml() output (System/EventData/Correlation/Execution/Security blocks)
    /// - the raw-XML half of #105's friendly/raw toggle, and the fallback shown when
    /// FormatDescription() came back empty because the provider's message file isn't registered.</summary>
    public string RawXml { get; init; } = string.Empty;

    /// <summary>EventRecord.Properties, captured as strings in original order (#105). Standard
    /// EventRecord property values have no per-value name attached on their own - #123 labels them
    /// with the real field names parsed from the provider's manifest &lt;template&gt; XML
    /// (EventLogExplorerService.GetProviderEventDetail) when one is registered, falling back to
    /// positional "Property[0]", "Property[1]", ... naming (what EventRecord.Properties itself
    /// actually is) when it isn't.</summary>
    public List<string> PropertyValues { get; init; } = new();

    /// <summary>An EventBookmark captured for this specific record - lets the grid resume paging
    /// from exactly this row (see EventLogExplorerService.ReadPage).</summary>
    public EventBookmark? Bookmark { get; init; }

    /// <summary>#116: EventRecord.ActivityId/RelatedActivityId - the only fields that stitch a
    /// multi-channel/multi-component operation together (a provider stamps the same ActivityId on
    /// every event it logs for one logical operation, and stamps RelatedActivityId when it started
    /// that operation on behalf of a different activity). Hidden by default in the grid (most
    /// records don't set them at all - null, not a fabricated zero-guid) and toggled on via the
    /// filter bar's "Correlation IDs" checkbox.</summary>
    public Guid? ActivityId { get; init; }
    public Guid? RelatedActivityId { get; init; }

    // #117/#120/#121: knowledge-base annotations - plain mutable properties (unlike every field
    // above, which is init-only and filled in by EventLogExplorerService.ConvertRecord) because
    // these are filled in by a separate pass (EventKnowledgeBaseService.Annotate, called from
    // EventsViewModel right after a row is read, before it's added to any bound collection) that
    // EventLogExplorerService itself has no knowledge of - keeps the KB entirely out of the
    // Event Viewer replacement's core read path.
    public bool KbHasEntry { get; set; }
    public int KbSeverityRank { get; set; }
    public string KbSeverityLabel { get; set; } = string.Empty;
    public bool KbIsBenign { get; set; }
    public string? KbNextStep { get; set; }
}

/// <summary>Composes the XPath used by EventLogExplorerService.BuildXPath (#104) - a structured,
/// UI-bound stand-in for the single hardcoded query string EventLogService.ReadLog uses. Keyword is
/// deliberately NOT part of the generated XPath: full-text search over arbitrary EventData/UserData
/// shapes isn't reliably expressible as XPath across every provider's schema, so it's applied as a
/// simple client-side Message.Contains(...) filter after a page is read instead of pretending the
/// server-side query can do it.</summary>
public sealed class EventFilterCriteria
{
    public HashSet<int> Levels { get; } = new();
    public List<string> Providers { get; } = new();
    public List<int> EventIds { get; } = new();
    public List<(int From, int To)> EventIdRanges { get; } = new();
    public int? LookbackDays { get; set; }
    public DateTime? StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }
    public string? UserSid { get; set; }
    public string? Keyword { get; set; }

    /// <summary>Parses a free-text "1000, 4624, 7000-7010" box into discrete IDs + ranges - the
    /// "event ID list/ranges" half of #104's filter bar. Unparseable tokens are skipped rather than
    /// throwing, since this runs on every keystroke-driven rebuild.</summary>
    public static (List<int> Ids, List<(int, int)> Ranges) ParseEventIds(string? text)
    {
        var ids = new List<int>();
        var ranges = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(text)) return (ids, ranges);

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            if (dash > 0 && int.TryParse(raw[..dash], out int from) && int.TryParse(raw[(dash + 1)..], out int to))
            {
                ranges.Add((Math.Min(from, to), Math.Max(from, to)));
            }
            else if (int.TryParse(raw, out int id))
            {
                ids.Add(id);
            }
        }
        return (ids, ranges);
    }
}

/// <summary>One row of the detail pane's friendly property grid (#105) - a positional
/// EventRecord.Properties value, or the resolved user/process rows EventsViewModel prepends.</summary>
public sealed class EventPropertyDisplay
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>A named filter preset - channel(s) + XPath + column layout, persisted to
/// event-filters.json (#108).</summary>
public sealed class SavedEventFilter
{
    public string Name { get; set; } = string.Empty;
    public List<string> Channels { get; set; } = new();
    public string XPath { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
}

/// <summary>event-filters.json root, same shape as PollIntervalSettings/ThemeService's settings
/// files - a plain serializable object with a static Defaults factory supplying the built-in
/// presets (#108), loaded/saved by EventFilterSettingsService.</summary>
public sealed class EventFilterSettings
{
    public List<SavedEventFilter> Filters { get; set; } = new();

    private static readonly string[] DefaultColumns =
        { "Time", "Level", "Provider", "EventId", "Task", "RecordId", "ProcessId", "User" };

    /// <summary>Built-in starter presets (#108). Each is a reasonable "look here first" starting
    /// point, not an exhaustive or authoritative diagnosis - the same "quick flag, not a verdict"
    /// spirit as this app's other heuristic features; a user can edit/duplicate/delete any of them
    /// like their own saved filters once loaded.</summary>
    public static EventFilterSettings Defaults => new()
    {
        Filters = new List<SavedEventFilter>
        {
            new()
            {
                Name = "Crash triage",
                Channels = new List<string> { "System", "Application" },
                XPath = "*[System[(Level=1 or Level=2) and (Provider[@Name='Microsoft-Windows-Kernel-Power'] or Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='Application Error'] or Provider[@Name='EventLog'])]]",
                Columns = new List<string>(DefaultColumns),
            },
            new()
            {
                Name = "Storage errors",
                Channels = new List<string> { "System" },
                XPath = "*[System[(Level=1 or Level=2) and (Provider[@Name='disk'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='volsnap'])]]",
                Columns = new List<string>(DefaultColumns),
            },
            new()
            {
                Name = "Service failures",
                Channels = new List<string> { "System" },
                XPath = "*[System[(Level=1 or Level=2) and Provider[@Name='Service Control Manager']]]",
                Columns = new List<string>(DefaultColumns),
            },
            new()
            {
                Name = "Boot problems",
                Channels = new List<string> { "System" },
                XPath = "*[System[(Level=1 or Level=2) and (Provider[@Name='EventLog'] or Provider[@Name='Microsoft-Windows-Kernel-Boot'] or Provider[@Name='Microsoft-Windows-Kernel-General'] or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=6008 or EventID=1)))]]",
                Columns = new List<string>(DefaultColumns),
            },
        },
    };
}

/// <summary>#109: one parsed *.xml file under %ProgramData%\Microsoft\Event Viewer\Views (Event
/// Viewer's own "Create Custom View..." storage), offered as an importable saved filter - see
/// EventLogExplorerService.GetImportableCustomViews.</summary>
public sealed class ImportableCustomView
{
    public string Name { get; init; } = string.Empty;
    public List<string> Channels { get; init; } = new();
    public string XPath { get; init; } = string.Empty;
    public string SourceFilePath { get; init; } = string.Empty;
}

/// <summary>#110: one %SystemRoot%\System32\Winevt\Logs\Archive-*.evtx autobackup file, offered as
/// a "Recent archives" quick-pick next to the general Open .evtx file picker.</summary>
public sealed class RecentArchiveEntry
{
    public string Path { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime LastWriteTimeUtc { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>#113: one event ID a provider's manifest declares it can emit, read from
/// ProviderMetadata.Events - the machine-accurate, always-current answer to "what does event 129
/// from provider X mean," never a bundled/guessed lookup table.</summary>
public sealed class ProviderEventMetadataRow
{
    public int EventId { get; init; }
    public int Version { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    public string Opcode { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Channels { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
}
