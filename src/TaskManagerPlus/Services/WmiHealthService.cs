using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #791-793: the Windows Health tab's "WMI" card - repository verify/salvage/reset via
/// winmgmt.exe (#791), a WMI-Activity/Operational error analyzer grouped by client process
/// (#792), and a read-only inventory of permanent WMI event-consumer subscriptions in
/// root\subscription (#793). Grouped in one file (like WindowsServicingService bundles #770/#773)
/// since all three are "is WMI itself healthy/trustworthy" questions, not three unrelated features.
/// </summary>
public static class WmiHealthService
{
    private const string RepositoryPath = @"C:\Windows\System32\wbem\Repository";

    #region #791 - WMI repository verify/salvage/reset

    /// <summary>#791: on-disk footprint of the repository folder - a plain recursive size sum plus
    /// the folder's own last-write time. Cheap enough to read as part of the card's initial load
    /// (no shell-out), unlike the verify/salvage/reset verbs below.</summary>
    public static WmiRepositoryHealth ReadRepositoryFootprint()
    {
        long? size = null;
        DateTime? lastModified = null;
        try
        {
            if (Directory.Exists(RepositoryPath))
            {
                lastModified = Directory.GetLastWriteTime(RepositoryPath);
                size = SumDirectorySize(RepositoryPath);
            }
        }
        catch
        {
            // Access denied/path unavailable - degrade to Unknown, never fabricated.
        }

        return new WmiRepositoryHealth
        {
            RepositoryPath = RepositoryPath,
            RepositorySizeBytes = size,
            RepositoryLastModified = lastModified,
        };
    }

    private static long SumDirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* one unreadable file shouldn't abort the whole sum */ }
            }
        }
        catch { /* a subfolder became inaccessible mid-walk - return what was summed so far */ }
        return total;
    }

    /// <summary>#791: `winmgmt /verifyrepository` - read-only, reports consistent/inconsistent.
    /// No confirmation needed (a read-only check), unlike Salvage/Reset below.</summary>
    public static async Task<WmiRepositoryHealth> VerifyRepositoryAsync()
    {
        var footprint = ReadRepositoryFootprint();
        var (output, _) = await RunCapturedAsync("winmgmt.exe", "/verifyrepository", 60000).ConfigureAwait(false);
        bool? consistent = output.Contains("consistent", StringComparison.OrdinalIgnoreCase) && !output.Contains("inconsistent", StringComparison.OrdinalIgnoreCase)
            ? true
            : output.Contains("inconsistent", StringComparison.OrdinalIgnoreCase) ? false : null;

        return new WmiRepositoryHealth
        {
            RepositoryPath = footprint.RepositoryPath,
            RepositorySizeBytes = footprint.RepositorySizeBytes,
            RepositoryLastModified = footprint.RepositoryLastModified,
            IsConsistent = consistent,
            VerifyOutputText = output.Trim(),
        };
    }

    /// <summary>#791: `winmgmt /salvagerepository` - attempts an in-place repair of an inconsistent
    /// repository. Only ever called after the caller has shown this exact command in a
    /// confirmation dialog (CLAUDE.md's mutating-action convention).</summary>
    public static async Task<(bool Success, string Output)> SalvageRepositoryAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("winmgmt.exe", "/salvagerepository", 120000).ConfigureAwait(false);
        return (exitCode == 0, output.Trim());
    }

    /// <summary>#791: `winmgmt /resetrepository` - the documented last resort: rebuilds the entire
    /// CIM repository from scratch. Labelled as escalation in the ViewModel's confirmation dialog
    /// specifically because it can break third-party management/monitoring agents that registered
    /// their own WMI classes/providers into the now-discarded repository (SCCM, many AV/EDR
    /// suites, backup agents) - they typically need reinstalling or re-registering afterward. Only
    /// ever called after that dialog.</summary>
    public static async Task<(bool Success, string Output)> ResetRepositoryAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("winmgmt.exe", "/resetrepository", 120000).ConfigureAwait(false);
        return (exitCode == 0, output.Trim());
    }

    #endregion

    #region #792 - WMI activity error analyzer

    private const string WmiActivityLog = "Microsoft-Windows-WMI-Activity/Operational";
    private const int WmiQueryFailureEventId = 5858;

    // #792: event 5858's rendered message is a semicolon-separated "Label = value" run
    // ("OperationId = {...}; ClientProcessId = 1234; NamespaceName = ...; Operation = ...;
    // ResultCode = 0x80041010; ...") - not a stable indexed Properties layout across OS builds
    // (verified wording differs even between documented KB articles), so this reads the rendered
    // text by label, the same tradeoff EventLogService's ScmServiceNamePatterns/QuotedTaskNameRegex
    // already take for other event families whose schema isn't a versioned contract.
    private static readonly Regex ClientPidRegex = new(@"ClientProcessId\s*=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex OperationRegex = new(@"Operation\s*=\s*(.+?)\s*;\s*(?:ResultCode|PossibleCause)", RegexOptions.Compiled);
    private static readonly Regex ResultCodeRegex = new(@"ResultCode\s*=\s*(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);

    /// <summary>#792: scans the last 30 days of WMI-Activity/Operational for event 5858 ("a client
    /// query failed") and groups by ClientProcessId - the same lookback window and "degrade to
    /// empty on a disabled/missing channel" tradeoff EventLogService's own reads use. This channel
    /// is enabled by default on modern Windows, unlike Task Scheduler's operational log, so no
    /// enable-first step is offered here.</summary>
    public static List<WmiActivityErrorGroup> ReadActivityErrorGroups(int lookbackDays = 30)
    {
        var events = new List<WmiActivityErrorEvent>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(WmiActivityLog, PathType.LogName,
                $"*[System[(EventID={WmiQueryFailureEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 3000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var pidMatch = ClientPidRegex.Match(message);
                    var opMatch = OperationRegex.Match(message);
                    var codeMatch = ResultCodeRegex.Match(message);

                    events.Add(new WmiActivityErrorEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        ClientProcessId = pidMatch.Success && int.TryParse(pidMatch.Groups[1].Value, out int pid) ? pid : null,
                        OperationText = opMatch.Success ? opMatch.Groups[1].Value.Trim() : "(couldn't parse operation text)",
                        ResultCode = codeMatch.Success ? codeMatch.Groups[1].Value : null,
                        RawMessage = message.Length > 500 ? message[..500] + "…" : message,
                    });
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - degrade to "no failure history", same as every
            // other event-log read in this app.
        }

        return events
            .GroupBy(e => e.ClientProcessId)
            .Select(g =>
            {
                // A PID seen over 30 days may belong to a different process now - only name the
                // current owner when it was already running at the group's oldest error.
                var oldest = g.Min(e => e.TimeCreated);
                string name = "Unknown (process has since exited, or the pid was reused)";
                bool running = false;
                if (g.Key is { } pid && TryGetProcessIdentity(pid, out var procName, out var startTime))
                {
                    if (startTime is { } started && started <= oldest)
                    {
                        name = procName;
                        running = true;
                    }
                    else if (startTime is null)
                    {
                        name = $"Unknown (PID now belongs to {procName}, but its start time couldn't be read to confirm it's the same process)";
                    }
                    else
                    {
                        name = $"Unknown (PID reused - the current {procName} started after the oldest of these errors)";
                    }
                }
                return new WmiActivityErrorGroup
                {
                    ClientProcessId = g.Key,
                    ProcessName = name,
                    ProcessStillRunning = running,
                    ErrorCount = g.Count(),
                    LastErrorTime = g.Max(e => e.TimeCreated),
                    Events = g.OrderByDescending(e => e.TimeCreated).ToList(),
                };
            })
            .OrderByDescending(g => g.ErrorCount)
            .ToList();
    }

    /// <summary>The live process currently holding <paramref name="pid"/>, if any - name plus start
    /// time (null when it can't be read, e.g. access denied), disposing the handle immediately.</summary>
    private static bool TryGetProcessIdentity(int pid, out string name, out DateTime? startTime)
    {
        name = string.Empty;
        startTime = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
            try { startTime = proc.StartTime; } catch { /* protected/inaccessible - leave unknown */ }
            return true;
        }
        catch
        {
            return false; // no such process (exited), or it exited mid-read
        }
    }

    #endregion

    #region #793 - Permanent WMI event consumer inventory

    // root\default ships CommandLineEventConsumer, ActiveScriptEventConsumer and the binding class
    // too, so a complete subscription can live there with no MOF compile. Its rows carry a
    // "[root\default]" prefix on FilterName; root\subscription rows read exactly as before.
    private static readonly string[] SubscriptionNamespaces = { @"root\subscription", @"root\default" };

    /// <summary>#793: enumerates __EventFilter, CommandLineEventConsumer, ActiveScriptEventConsumer
    /// and __FilterToConsumerBinding in root\subscription and root\default - the permanent-WMI-
    /// subscription mechanism (as opposed to a temporary in-process WMI event subscription, which
    /// leaves no registered trace once its process exits). This exact combination is a well-documented
    /// persistence technique, so every filter is listed even when it has no matching binding/
    /// consumer (an orphaned filter, or one whose consumer is missing, is itself worth surfacing -
    /// see BindingFound). A namespace that can't be read gets a row saying so, rather than reading
    /// as an empty (clean) one.</summary>
    public static List<WmiEventConsumerEntry> ReadPermanentConsumers()
    {
        var result = new List<WmiEventConsumerEntry>();
        foreach (var ns in SubscriptionNamespaces)
        {
            try
            {
                AddNamespaceConsumers(result, ns);
            }
            catch (Exception ex)
            {
                result.Add(new WmiEventConsumerEntry
                {
                    FilterName = Label(ns, "(couldn't read this namespace)"),
                    ConsumerType = "(unknown)",
                    ConsumerDetail = $"{ns} couldn't be enumerated, so subscriptions registered there aren't listed: {ex.Message}",
                    BindingFound = false,
                });
            }
        }
        return result;
    }

    private static string Label(string ns, string filterName) =>
        ns.Equals(@"root\subscription", StringComparison.OrdinalIgnoreCase) ? filterName : $"[{ns}] {filterName}";

    private static void AddNamespaceConsumers(List<WmiEventConsumerEntry> result, string ns)
    {
        var filters = ReadFilters(ns);
        var cmdConsumers = ReadCommandLineConsumers(ns);
        var scriptConsumers = ReadScriptConsumers(ns);
        var bindings = ReadBindings(ns);
        var reachedConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WmiEventConsumerEntry Bound(string filterName, string query, string consumerPath)
        {
            reachedConsumers.Add(consumerPath);
            if (cmdConsumers.TryGetValue(consumerPath, out var cmdInfo))
                return new WmiEventConsumerEntry { FilterName = Label(ns, filterName), Query = query, ConsumerType = "CommandLineEventConsumer", ConsumerName = cmdInfo.Name, ConsumerDetail = cmdInfo.CommandLine, BindingFound = true };
            if (scriptConsumers.TryGetValue(consumerPath, out var scriptInfo))
                return new WmiEventConsumerEntry { FilterName = Label(ns, filterName), Query = query, ConsumerType = "ActiveScriptEventConsumer", ConsumerName = scriptInfo.Name, ConsumerDetail = scriptInfo.ScriptText, BindingFound = true };
            return new WmiEventConsumerEntry { FilterName = Label(ns, filterName), Query = query, ConsumerType = "(other consumer type)", ConsumerDetail = consumerPath, BindingFound = true };
        }

        // One row per binding, not per filter: a second consumer bound to a stock filter (the
        // in-box "SCM Event Log Filter") is a known way to blend in.
        var filterPaths = new HashSet<string>(filters.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (filterPath, filterName, query) in filters)
        {
            var bound = bindings.Where(b => b.FilterPath.Equals(filterPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (bound.Count == 0)
            {
                result.Add(new WmiEventConsumerEntry { FilterName = Label(ns, filterName), Query = query, ConsumerType = "(none)", ConsumerDetail = "No __FilterToConsumerBinding found for this filter.", BindingFound = false });
                continue;
            }
            foreach (var b in bound) result.Add(Bound(filterName, query, b.ConsumerPath));
        }

        // Bindings whose filter isn't one of this namespace's filters above (it lives in another
        // namespace, or was deleted) still fire their consumer.
        foreach (var b in bindings.Where(b => !filterPaths.Contains(b.FilterPath)))
            result.Add(Bound(b.FilterPath, $"(filter not found in {ns} - it was deleted or lives in another namespace)", b.ConsumerPath));

        // Command-line/script consumers no binding reaches - inert on their own, but left-over
        // persistence (or one half of a subscription being staged) is worth seeing.
        foreach (var (_, info) in cmdConsumers.Where(c => !reachedConsumers.Contains(c.Key)))
            result.Add(new WmiEventConsumerEntry { FilterName = Label(ns, "(no filter bound to this consumer)"), ConsumerType = "CommandLineEventConsumer", ConsumerName = info.Name, ConsumerDetail = info.CommandLine, BindingFound = false });
        foreach (var (_, info) in scriptConsumers.Where(c => !reachedConsumers.Contains(c.Key)))
            result.Add(new WmiEventConsumerEntry { FilterName = Label(ns, "(no filter bound to this consumer)"), ConsumerType = "ActiveScriptEventConsumer", ConsumerName = info.Name, ConsumerDetail = info.ScriptText, BindingFound = false });
    }

    private static List<(string Path, string Name, string Query)> ReadFilters(string ns)
    {
        var list = new List<(string, string, string)>();
        using var searcher = new ManagementObjectSearcher(ns, "SELECT Name, Query FROM __EventFilter");
        foreach (ManagementObject mo in searcher.Get())
        {
            string name = mo["Name"] as string ?? string.Empty;
            string query = mo["Query"] as string ?? string.Empty;
            string path = mo.Path.RelativePath;
            list.Add((path, name, query));
        }
        return list;
    }

    private static Dictionary<string, (string Name, string CommandLine)> ReadCommandLineConsumers(string ns)
    {
        var dict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, "SELECT Name, CommandLineTemplate, ExecutablePath FROM CommandLineEventConsumer");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? string.Empty;
                string cmd = (mo["CommandLineTemplate"] as string) ?? (mo["ExecutablePath"] as string) ?? "(no command line recorded)";
                dict[mo.Path.RelativePath] = (name, cmd);
            }
        }
        catch { /* class not present - fine, just means no command-line consumers registered */ }
        return dict;
    }

    private static Dictionary<string, (string Name, string ScriptText)> ReadScriptConsumers(string ns)
    {
        var dict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, "SELECT Name, ScriptText, ScriptFileName FROM ActiveScriptEventConsumer");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? string.Empty;
                string script = (mo["ScriptText"] as string) ?? (mo["ScriptFileName"] as string) ?? "(no script text recorded)";
                if (script.Length > 400) script = script[..400] + "…";
                dict[mo.Path.RelativePath] = (name, script);
            }
        }
        catch { /* class not present - fine */ }
        return dict;
    }

    private static List<(string FilterPath, string ConsumerPath)> ReadBindings(string ns)
    {
        var list = new List<(string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, "SELECT Filter, Consumer FROM __FilterToConsumerBinding");
            foreach (ManagementObject mo in searcher.Get())
            {
                string filterRef = (mo["Filter"] as string) ?? string.Empty;
                string consumerRef = (mo["Consumer"] as string) ?? string.Empty;
                // Both come back as embedded object-path strings (e.g. __EventFilter.Name="Foo") -
                // strip down to just the relative path portion the filter/consumer dictionaries above
                // are keyed by.
                list.Add((StripNamespacePrefix(filterRef, ns), StripNamespacePrefix(consumerRef, ns)));
            }
        }
        catch { /* class not present - fine */ }
        return list;
    }

    /// <summary>A reference is either relative (`__EventFilter.Name="Foo"`, same namespace) or
    /// namespace-qualified (`\\HOST\root\subscription:__EventFilter.Name="Foo"`). Only a prefix
    /// naming <paramref name="ns"/> itself is cut off; a reference into another namespace stays whole
    /// so it can't match a same-named filter here. A colon inside the key value isn't a prefix.</summary>
    private static string StripNamespacePrefix(string objectPath, string ns)
    {
        int idx = objectPath.IndexOf(':');
        if (idx < 0) return objectPath;
        string prefix = objectPath[..idx];
        if (prefix.Contains('=') || prefix.Contains('"')) return objectPath;
        return prefix.Equals(ns, StringComparison.OrdinalIgnoreCase)
            || prefix.EndsWith(@"\" + ns, StringComparison.OrdinalIgnoreCase)
            ? objectPath[(idx + 1)..]
            : objectPath;
    }

    #endregion

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
