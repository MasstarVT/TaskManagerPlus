using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #117: known-bad Event ID knowledge base - "what this event usually means" for the handful of
/// provider/ID combinations that actually matter for desktop troubleshooting (storage failures,
/// WHEA hardware errors, service-control failures, DCOM permission noise, Perflib counter
/// corruption, unclean shutdowns, volume-shadow-copy failures, ESENT database errors, Schannel/TLS
/// failures, Group Policy processing failures, time-sync failures, and TCP/IP port exhaustion).
///
/// Loaded in two layers, same shape as every other settings file's "bundled defaults + user
/// additions" story in this app:
///  1. The bundled, read-only Resources/EventKnowledgeBase.json, embedded into the assembly (never
///     modified in place - see the csproj's EmbeddedResource item).
///  2. A user-editable %AppData%\TaskManagerPlus\event-kb-overrides.json, merged on top - an
///     override entry with the same (Provider, EventId) replaces the bundled one; a new
///     (Provider, EventId) is simply added. Lets a user extend coverage (the #126 "export unknown
///     events" workflow is meant to feed exactly this file) without a rebuild.
///
/// Every entry is explicitly informational, never authoritative - CLAUDE.md's "Quick flag, not a
/// verdict" convention (#118) applies to the whole knowledge base, and a missing/corrupt bundled
/// resource or overrides file degrades to "fewer entries" rather than throwing (this app's
/// standard "degrade, never fabricate" rule).
/// </summary>
public sealed class EventKnowledgeBaseService
{
    private const string OverridesFileName = "event-kb-overrides.json";
    private const string BundledResourceSuffix = "EventKnowledgeBase.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, EventKbEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int BundledEntryCount { get; private set; }
    public int OverrideEntryCount { get; private set; }

    /// <summary>Non-null only when event-kb-overrides.json exists but failed to parse - surfaced in
    /// the UI so a hand-edited overrides file with a typo doesn't silently fail forever. The
    /// bundled resource failing is not reported here (it would mean a broken build, not something a
    /// user can fix), just degrades to an empty bundled set.</summary>
    public string? OverridesLoadError { get; private set; }

    public EventKnowledgeBaseService() => Reload();

    /// <summary>Re-reads both layers from scratch - callers may want this after a user edits
    /// event-kb-overrides.json by hand while the app is running, though nothing currently wires an
    /// automatic file-watcher for it (the same "no live file watching" choice every other settings
    /// file in this app makes - settings are read at load time, not watched).</summary>
    public void Reload()
    {
        _entries.Clear();
        OverridesLoadError = null;

        var bundled = LoadBundled();
        foreach (var entry in bundled) _entries[MakeKey(entry.Provider, entry.EventId)] = entry;
        BundledEntryCount = _entries.Count;

        var overrides = LoadOverrides();
        foreach (var entry in overrides) _entries[MakeKey(entry.Provider, entry.EventId)] = entry;
        OverrideEntryCount = overrides.Count;
    }

    public static string MakeKey(string provider, int eventId) => $"{provider}|{eventId}";

    public EventKbEntry? Lookup(string? provider, int eventId)
        => string.IsNullOrEmpty(provider) ? null : _entries.GetValueOrDefault(MakeKey(provider, eventId));

    /// <summary>#122: every KB entry flagged as a genuinely serious (non-benign, Error/Critical
    /// re-ranked) event - the exact set the Stability tab's "Known-bad IDs present on this PC"
    /// scorecard scopes its scan to, rather than scanning for every entry (including the benign/
    /// informational ones, which don't belong on a "what's wrong" scorecard).</summary>
    public IReadOnlyList<(string Provider, int EventId)> SeriousFlaggedIds()
        => _entries.Values
            .Where(e => !e.IsBenign && e.SeverityRank is EventKbSeverity.Error or EventKbSeverity.Critical)
            .Select(e => (e.Provider, e.EventId))
            .ToList();

    /// <summary>#120/#121: annotates a freshly-read event row with this KB's opinion (or, when
    /// there's no entry, a fallback rank derived from Windows' own level) - called once per row
    /// right after it's read, from EventsViewModel, before the row is added to any bound
    /// collection. EventRecordRow's Kb* properties are plain mutable properties (unlike the
    /// init-only fields EventLogExplorerService fills in) specifically so this second, KB-aware
    /// pass can fill them in without EventLogExplorerService itself needing to know the knowledge
    /// base exists.</summary>
    public void Annotate(EventRecordRow row)
    {
        var entry = Lookup(row.ProviderName, row.EventId);
        if (entry is not null)
        {
            row.KbHasEntry = true;
            row.KbSeverityRank = (int)entry.SeverityRank;
            row.KbSeverityLabel = entry.SeverityRank.ToString();
            row.KbIsBenign = entry.IsBenign;
            row.KbNextStep = string.IsNullOrWhiteSpace(entry.NextStep) ? null : entry.NextStep;
        }
        else
        {
            row.KbHasEntry = false;
            row.KbSeverityRank = (int)FallbackSeverity(row.LevelValue);
            row.KbSeverityLabel = string.Empty;
            row.KbIsBenign = false;
            row.KbNextStep = null;
        }
    }

    /// <summary>Maps EventRecord.Level (1=Critical..5=Verbose, 0="LogAlways"/unset - the same
    /// values EventLogExplorerService.LevelNames documents) onto the KB's own severity scale, so an
    /// un-flagged row still sorts sensibly next to KB-ranked ones instead of always sorting to the
    /// bottom.</summary>
    private static EventKbSeverity FallbackSeverity(int levelValue) => levelValue switch
    {
        1 => EventKbSeverity.Critical,
        2 => EventKbSeverity.Error,
        3 => EventKbSeverity.Warning,
        5 => EventKbSeverity.Verbose,
        _ => EventKbSeverity.Information,
    };

    private static List<EventKbEntry> LoadBundled()
    {
        try
        {
            var asm = typeof(EventKnowledgeBaseService).Assembly;
            string? resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(BundledResourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return new List<EventKbEntry>();

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return new List<EventKbEntry>();

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<List<EventKbEntry>>(json, JsonOptions) ?? new List<EventKbEntry>();
        }
        catch
        {
            // Bundled resource missing/corrupt (shouldn't happen in a normal build) - degrade to an
            // empty bundled set rather than crashing the Events tab; overrides can still load.
            return new List<EventKbEntry>();
        }
    }

    private List<EventKbEntry> LoadOverrides()
    {
        try
        {
            string path = AppPaths.GetPath(OverridesFileName);
            if (!File.Exists(path)) return new List<EventKbEntry>();

            string json = File.ReadAllText(path);
            // Accept either a bare array or the {"Entries": [...]} wrapper shape, so a
            // hand-authored file can be as simple as copy-pasting entries without the wrapper.
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith('['))
                return JsonSerializer.Deserialize<List<EventKbEntry>>(json, JsonOptions) ?? new List<EventKbEntry>();

            var wrapper = JsonSerializer.Deserialize<EventKbOverridesFile>(json, JsonOptions);
            return wrapper?.Entries ?? new List<EventKbEntry>();
        }
        catch (Exception ex)
        {
            OverridesLoadError = ex.Message;
            return new List<EventKbEntry>();
        }
    }
}
