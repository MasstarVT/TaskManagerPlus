using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #192-194: "service, COM and performance-subsystem error families" - service crash-loop
/// detection (SCM 7000/7009/7024/7031/7034, #192), the ServicesPipeTimeout start-timeout
/// explanation extending EventLogService.ReadServiceStartDurations with the failure case it can't
/// represent (#193), and the DistributedCOM CLSID/APPID -&gt; friendly-name resolver (#194). Every
/// read here follows this app's standard "degrade to empty/Unknown, never fabricate, never throw
/// out of the service" rule - a locked-down channel, a missing registry key, or an unregistered
/// CLSID are all real, expected conditions, not bugs.
/// </summary>
public sealed class ServiceHealthEventService
{
    private const int LookbackDays = 30;
    private const string ScmProvider = "Service Control Manager";

    private readonly EventLogExplorerService _explorer;

    public ServiceHealthEventService() : this(new EventLogExplorerService()) { }
    public ServiceHealthEventService(EventLogExplorerService explorer) => _explorer = explorer;

    // ==================== #192: service crash-loop detector ====================

    private static readonly Regex RestartCountRegex = new(@"has done this\s+(\d+)\s+time", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ServiceSpecificExitCodeRegex = new(@"service-specific error\s+(.+?)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#192: correlates SCM 7031/7034 (terminated unexpectedly, carries a restart count in
    /// its own message text), 7000 (failed to start), 7009 (start timeout - also #193's data
    /// source), and 7024 (service-specific exit code) per service name, over the 30-day lookback.
    /// Service name is read the same way EventLogService.ReadServiceStartDurations and
    /// EventsViewModel.ExtractServiceName already do for other SCM events - the first insertion
    /// string. Only services with at least one qualifying event are returned (IsCrashLooping further
    /// narrows which ones are worth a badge - see that property's remarks).</summary>
    public List<ServiceCrashLoopInfo> ReadServiceCrashLoops(int lookbackDays = LookbackDays)
    {
        var pairs = new (string Provider, int EventId)[]
        {
            (ScmProvider, 7000), (ScmProvider, 7009), (ScmProvider, 7024), (ScmProvider, 7031), (ScmProvider, 7034),
        };
        var rows = ReadFamily("System", pairs, lookbackDays);
        if (rows.Count == 0) return new List<ServiceCrashLoopInfo>();

        var byService = new Dictionary<string, List<EventRecordRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            string? name = ExtractServiceName(r);
            if (name is null) continue;
            if (!byService.TryGetValue(name, out var list)) byService[name] = list = new List<EventRecordRow>();
            list.Add(r);
        }

        var result = new List<ServiceCrashLoopInfo>();
        foreach (var (name, events) in byService)
        {
            var ordered = events.OrderByDescending(e => e.TimeCreated).ToList();
            var lastTerminated = ordered.FirstOrDefault(e => e.EventId is 7031 or 7034);
            var lastExitCode = ordered.FirstOrDefault(e => e.EventId == 7024);

            int? restartCount = lastTerminated is not null && RestartCountRegex.Match(lastTerminated.Message) is { Success: true } rm
                ? int.Parse(rm.Groups[1].Value)
                : null;
            string? exitCode = lastExitCode is not null && ServiceSpecificExitCodeRegex.Match(lastExitCode.Message) is { Success: true } em
                ? em.Groups[1].Value.Trim()
                : null;

            result.Add(new ServiceCrashLoopInfo
            {
                ServiceName = name,
                TerminatedCount = events.Count(e => e.EventId is 7031 or 7034),
                FailedToStartCount = events.Count(e => e.EventId == 7000),
                TimeoutCount = events.Count(e => e.EventId == 7009),
                ServiceSpecificExitCodeCount = events.Count(e => e.EventId == 7024),
                LastRestartCount = restartCount,
                LastServiceSpecificExitCode = exitCode,
                LastEventTime = ordered[0].TimeCreated,
            });
        }

        return result.OrderByDescending(r => r.TotalCount).ToList();
    }

    /// <summary>Service Control Manager 7000/7009/7024/7031/7034's first insertion string is always
    /// the service's display/internal name - the same positional convention EventLogService.
    /// ReadServiceStartDurations and EventsViewModel.ExtractServiceName already rely on for 7036/
    /// 7031/7009 respectively.</summary>
    private static string? ExtractServiceName(EventRecordRow row)
        => row.PropertyValues.Count > 0 && !string.IsNullOrWhiteSpace(row.PropertyValues[0]) ? row.PropertyValues[0] : null;

    // ==================== #193: start-timeout diagnosis ====================

    private const string ServicesPipeTimeoutPath = @"SYSTEM\CurrentControlSet\Control";
    private const string ServicesPipeTimeoutValue = "ServicesPipeTimeout";
    private const int DefaultServicesPipeTimeoutMs = 30000;

    /// <summary>#193: the current effective SCM start-timeout - reads
    /// HKLM\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout when present (a DWORD in
    /// milliseconds), otherwise reports the well-known 30000ms default Windows applies when the
    /// value doesn't exist at all (the common case - most machines never set this).</summary>
    public static ServiceStartTimeoutInfo ReadServiceStartTimeout()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServicesPipeTimeoutPath);
            var raw = key?.GetValue(ServicesPipeTimeoutValue);
            if (raw is null) return new ServiceStartTimeoutInfo { EffectiveTimeoutMs = DefaultServicesPipeTimeoutMs, IsCustomized = false };

            int ms = Convert.ToInt32(raw);
            return new ServiceStartTimeoutInfo { EffectiveTimeoutMs = ms, IsCustomized = true };
        }
        catch
        {
            // Key/value unreadable (unexpected under an elevated process, but degrade rather than
            // throw) - report the documented default, same as "value doesn't exist".
            return new ServiceStartTimeoutInfo { EffectiveTimeoutMs = DefaultServicesPipeTimeoutMs, IsCustomized = false };
        }
    }

    // ==================== #194: DistributedCOM CLSID/APPID resolver ====================

    private static readonly Regex ClsidRegex = new(@"CLSID\s*\r?\n?\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.Compiled);
    private static readonly Regex AppidRegex = new(@"APPID\s*\r?\n?\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.Compiled);

    /// <summary>#194: extracts every CLSID/APPID GUID named in a DistributedCOM event's message text
    /// (10016's classic "...CLSID\n{...}\nand APPID\n{...}\n..." shape, and the single-CLSID shape
    /// other DCOM events like 10005/10006/10010 use) and resolves each to a friendly component name
    /// via HKCR\CLSID\{...}/HKCR\AppID\{...}'s own (Default) value - the same registry lookup
    /// Explorer/the Component Services MMC snap-in use to show a real name instead of a bare GUID.
    /// A GUID that doesn't resolve (uninstalled component, or a name that was never registered
    /// locally) comes back with FriendlyName=null, never guessed.</summary>
    public static List<DcomComponentResolution> ResolveDcomComponentsInMessage(string message)
    {
        var results = new List<DcomComponentResolution>();
        if (string.IsNullOrWhiteSpace(message)) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ClsidRegex.Matches(message))
        {
            string guid = m.Groups[1].Value;
            if (!seen.Add("CLSID|" + guid)) continue;
            results.Add(new DcomComponentResolution { Guid = guid, Kind = "CLSID", FriendlyName = ResolveClassesRootDefaultValue($@"CLSID\{guid}") });
        }
        foreach (Match m in AppidRegex.Matches(message))
        {
            string guid = m.Groups[1].Value;
            if (!seen.Add("APPID|" + guid)) continue;
            results.Add(new DcomComponentResolution { Guid = guid, Kind = "APPID", FriendlyName = ResolveClassesRootDefaultValue($@"AppID\{guid}") });
        }
        return results;
    }

    private static string? ResolveClassesRootDefaultValue(string subKeyPath)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(subKeyPath);
            var name = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            // Key doesn't exist, or is otherwise unreadable - not every CLSID/APPID a DCOM error
            // names is still installed on this machine.
            return null;
        }
    }

    // ==================== shared helper ====================

    /// <summary>Same shape as KernelEventFamilyService.ReadFamily - builds a provider+eventId-scoped
    /// XPath and reads it via EventLogExplorerService.ReadPage, degrading to an empty list on any
    /// failure (locked-down channel, etc.) rather than a second bespoke event-log reading path.</summary>
    private List<EventRecordRow> ReadFamily(string logName, IEnumerable<(string Provider, int EventId)> pairs, int lookbackDays, int pageSize = 2000)
    {
        var list = pairs.ToList();
        if (list.Count == 0) return new List<EventRecordRow>();

        string idsClause = string.Join(" or ", list
            .GroupBy(f => f.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"(Provider[@Name={EventLogExplorerService.QuoteXPathLiteral(g.Key)}] and ({string.Join(" or ", g.Select(f => $"EventID={f.EventId}"))}))"));

        long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
        string xpath = $"*[System[({idsClause}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";

        try
        {
            var result = _explorer.ReadPage(logName, xpath, null, pageSize);
            return result.ErrorText is null ? result.Rows : new List<EventRecordRow>();
        }
        catch
        {
            return new List<EventRecordRow>();
        }
    }
}
