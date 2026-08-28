using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #137-145: cross-channel timeline correlation for the Stability tab - the unified incident
/// timeline (#137), crash-window drill-down support data (#138 reuses
/// EventLogExplorerService.ReadMultiChannel/BuildStructuredQuery directly from StabilityViewModel,
/// not this service), pre-crash change attribution (#139), sleep/resume chain reconstruction
/// (#142), "who rebooted this PC" (#143), and the per-boot session ledger (#144). Every entry point
/// here is called from StabilityViewModel's existing on-demand RefreshCommand (never a new timer),
/// and every read degrades to an empty list on failure rather than throwing - the same "degrade,
/// never fabricate" rule the rest of this app's event-log reads follow.
///
/// Every flag/answer produced is explicitly a correlation over what the event log happened to
/// record, not a diagnosis - CLAUDE.md's "quick flag, not a verdict" convention applies throughout,
/// same as EventAnomalyDetectionService.
/// </summary>
public sealed class EventTimelineService
{
    private readonly EventLogExplorerService _explorer;

    public EventTimelineService(EventLogExplorerService explorer) => _explorer = explorer;

    // Crash-like event IDs on the System/Application logs - the same set EventLogService.Query
    // already treats as "a crash" for its own TimeSinceLastCrashText card.
    private const int KernelPowerUncleanId = 41;
    private const int LegacyUncleanShutdownId = 6008;
    private const int WerBlueScreenId = 1001;

    // ==== #137: unified incident timeline ====

    /// <summary>Merges whatever sources are actually wired today - event-log Critical/Error records,
    /// minidump files, boot markers (reused from EventAnomalyDetectionService.FindBootMarkers, per
    /// this chunk's instructions), shutdown markers (reused from ComputeRebootAttributions' output,
    /// computed once by the caller and passed in here rather than re-queried), and (#161) WER crash
    /// reports - into one time-ordered stream. <paramref name="csvLogMarkers"/> folds in the app's
    /// own CSV log samples when a logging session covers part of the window (#137's "if logging was
    /// running" clause); StabilityViewModel passes an empty sequence when none is available, never
    /// fabricating one.
    ///
    /// Driver/update/service-install events (items 169-183) are NOT wired in yet - no data source
    /// for them exists in this codebase. TimelineEntry/TimelineSource already declare cases for them
    /// (see their remarks) so a later chunk adds an Append* method here without touching anything
    /// that already consumes TimelineEntry.</summary>
    public List<TimelineEntry> BuildTimeline(
        IEnumerable<StabilityEvent> recentEvents,
        IEnumerable<MinidumpInfo> minidumps,
        IReadOnlyList<DateTime> bootMarkers,
        IReadOnlyList<RebootAttribution> shutdownMarkers,
        IEnumerable<TimelineEntry>? csvLogMarkers = null,
        IEnumerable<WerReportInfo>? werReports = null)
    {
        var entries = new List<TimelineEntry>();

        foreach (var e in recentEvents)
        {
            entries.Add(new TimelineEntry
            {
                Source = TimelineSource.EventLog,
                Timestamp = e.TimeCreated,
                Title = $"{e.ProviderName} {e.EventId} ({e.Level})",
                Detail = e.DisplayDetail,
                SourceLabel = "Event log",
                IsCrash = IsCrashLikeEvent(e),
                SourceEvent = new EventRecordRow
                {
                    TimeCreated = e.TimeCreated,
                    ChannelName = e.LogName,
                    Level = e.Level,
                    ProviderName = e.ProviderName,
                    EventId = e.EventId,
                    Message = e.Message,
                },
            });
        }

        foreach (var d in minidumps)
        {
            entries.Add(new TimelineEntry
            {
                Source = TimelineSource.Minidump,
                Timestamp = d.Timestamp,
                Title = $"Minidump: {d.FileName}",
                Detail = d.BugcheckCode is { } code ? $"Bugcheck {code}" : "No bugcheck code correlated with a nearby Kernel-Power 41 event.",
                SourceLabel = "Minidump",
                IsCrash = true,
            });
        }

        foreach (var b in bootMarkers)
        {
            entries.Add(new TimelineEntry
            {
                Source = TimelineSource.Boot,
                Timestamp = b,
                Title = "System boot",
                Detail = "EventLog 6005 / Kernel-General 12",
                SourceLabel = "Boot",
            });
        }

        foreach (var s in shutdownMarkers)
        {
            entries.Add(new TimelineEntry
            {
                Source = TimelineSource.Shutdown,
                Timestamp = s.Timestamp,
                Title = s.WasCleanShutdown ? "Shutdown / restart" : "Unclean shutdown",
                Detail = s.Answer,
                SourceLabel = "Shutdown",
                IsCrash = !s.WasCleanShutdown,
            });
        }

        // #161: WER crash reports - a minidump-shaped entry (mere existence is itself crash
        // evidence, same as the Minidump loop above), just from ReportQueue/ReportArchive instead of
        // %SystemRoot%\Minidump. See WerReportService.ReadReports.
        if (werReports is not null)
        {
            foreach (var w in werReports)
            {
                entries.Add(new TimelineEntry
                {
                    Source = TimelineSource.WerReport,
                    Timestamp = w.Timestamp,
                    Title = w.DisplayTitle,
                    Detail = w.ModName is { Length: > 0 } mod
                        ? $"Faulting module {mod}{(w.ExceptionCode is { Length: > 0 } code ? $", exception {code}" : string.Empty)} ({(w.IsArchived ? "archived" : "queued")})."
                        : $"WER report ({(w.IsArchived ? "archived" : "queued")}).",
                    SourceLabel = "WER report",
                    IsCrash = true,
                });
            }
        }

        if (csvLogMarkers is not null) entries.AddRange(csvLogMarkers);

        return entries.OrderByDescending(x => x.Timestamp).ToList();
    }

    private static bool IsCrashLikeEvent(StabilityEvent e) =>
        e.EventId is KernelPowerUncleanId or LegacyUncleanShutdownId or WerBlueScreenId;

    // ==== shared read helper ====

    /// <summary>One bounded, on-demand System-channel read - the same "build an XPath naming the
    /// exact provider(s)+eventId(s), single page, degrade to empty on any failure" shape
    /// EventLogService.ScanForKnownBadIds already uses, generalized so #139/#142/#143/#144 don't
    /// each hand-roll their own XPath/EventLogReader plumbing.</summary>
    private List<EventRecordRow> ReadEvents(string channel, string innerXPathClause, int lookbackDays, int maxRecords)
    {
        long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
        string xpath = $"*[System[({innerXPathClause}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";
        var result = _explorer.ReadPage(channel, xpath, null, pageSize: maxRecords, pathType: PathType.LogName);
        return result.ErrorText is null ? result.Rows : new List<EventRecordRow>();
    }

    /// <summary>Same as <see cref="ReadEvents"/> but bounded to an explicit [start,end] window
    /// (used by #139, which looks backward from a specific crash time rather than "now").</summary>
    private List<EventRecordRow> ReadEventsInWindow(string channel, string innerXPathClause, DateTime startUtc, DateTime endUtc, int maxRecords)
    {
        string xpath = $"*[System[({innerXPathClause}) and TimeCreated[@SystemTime>='{startUtc:o}'] and TimeCreated[@SystemTime<='{endUtc:o}']]]";
        var result = _explorer.ReadPage(channel, xpath, null, pageSize: maxRecords, pathType: PathType.LogName);
        return result.ErrorText is null ? result.Rows : new List<EventRecordRow>();
    }

    // ==== #139: attribute a crash to the change that preceded it ====

    private const string DriverPnpClause = "(Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=20001 or EventID=219)) or (Provider[@Name='Microsoft-Windows-UserPnp'] and EventID=20003)";
    private const string NewServiceClause = "Provider[@Name='Service Control Manager'] and EventID=7045";
    private const string SetupLogClause = "EventID=1 or EventID=2 or EventID=4";
    private const string WindowsUpdateChannel = "Microsoft-Windows-WindowsUpdateClient/Operational";
    private const string WindowsUpdateClause = "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19";

    /// <summary>#139: searches the 7 days (default) before <paramref name="crashTimeLocal"/> for
    /// driver installs (Kernel-PnP 20001/219, UserPnp 20003), update installs (Setup log 1/2/4,
    /// WindowsUpdateClient/Operational 19), and new services (SCM 7045) - "changes shortly before
    /// this crash," explicitly framed as correlation, never causation (see StabilityView.xaml's
    /// card copy). Each channel is read independently and degrades to nothing on its own if that
    /// channel doesn't exist/isn't enabled on this machine (WindowsUpdateClient/Operational in
    /// particular isn't guaranteed to be enabled) - one missing channel never blanks the others.</summary>
    public List<PreCrashChange> FindChangesBeforeCrash(DateTime crashTimeLocal, int lookbackDays = 7, int maxRecordsPerChannel = 200)
    {
        var startUtc = crashTimeLocal.ToUniversalTime().AddDays(-lookbackDays);
        var endUtc = crashTimeLocal.ToUniversalTime();

        var rows = new List<EventRecordRow>();
        rows.AddRange(ReadEventsInWindow("System", $"({DriverPnpClause}) or ({NewServiceClause})", startUtc, endUtc, maxRecordsPerChannel));
        rows.AddRange(ReadEventsInWindow("Setup", SetupLogClause, startUtc, endUtc, maxRecordsPerChannel));
        rows.AddRange(ReadEventsInWindow(WindowsUpdateChannel, WindowsUpdateClause, startUtc, endUtc, maxRecordsPerChannel));

        return rows
            .OrderByDescending(r => r.TimeCreated)
            .Select(r => new PreCrashChange
            {
                Timestamp = r.TimeCreated,
                Category = CategorizeChange(r.ProviderName, r.EventId, r.ChannelName),
                Provider = r.ProviderName,
                EventId = r.EventId,
                SampleMessage = r.Message,
                TimeBeforeCrash = crashTimeLocal - r.TimeCreated,
            })
            .ToList();
    }

    private static string CategorizeChange(string provider, int eventId, string channel) => (provider, eventId) switch
    {
        ("Microsoft-Windows-Kernel-PnP", 20001) or ("Microsoft-Windows-Kernel-PnP", 219) => "Driver install",
        ("Microsoft-Windows-UserPnp", 20003) => "Driver install",
        ("Microsoft-Windows-WindowsUpdateClient", 19) => "Update install",
        ("Service Control Manager", 7045) => "New service",
        _ when channel.Equals("Setup", StringComparison.OrdinalIgnoreCase) => "Update install",
        _ => "Change",
    };

    // ==== #142: sleep/resume incident chain ====

    private const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    private const string KernelBootProvider = "Microsoft-Windows-Kernel-Boot";

    /// <summary>#142: pairs each Kernel-Power 42 ("entering sleep") with the next Kernel-Power
    /// 107/187/507 ("resumed") or 41 ("rebooted instead of resuming, unclean") - the standard "wakes
    /// up to a black screen / reboots instead of resuming" pattern. Walks the provider's own events
    /// in chronological order rather than nearest-neighbor matching, so a stray unmatched resume
    /// event (left over from just before the lookback window started) is correctly ignored instead
    /// of pairing with the wrong sleep.</summary>
    public List<SleepResumeCycle> ReconstructSleepResumeCycles(int lookbackDays = 30, int maxRecords = 2000)
    {
        var powerEvents = ReadEvents("System", $"Provider[@Name='{KernelPowerProvider}'] and (EventID=42 or EventID=107 or EventID=187 or EventID=507 or EventID=41)", lookbackDays, maxRecords)
            .OrderBy(r => r.TimeCreated).ToList();
        var bootHints = ReadEvents("System", $"Provider[@Name='{KernelBootProvider}'] and (EventID=20 or EventID=27)", lookbackDays, maxRecords)
            .OrderBy(r => r.TimeCreated).ToList();

        var cycles = new List<SleepResumeCycle>();
        DateTime? pendingSleep = null;

        void CloseUnresolved(DateTime sleepTime)
        {
            cycles.Add(new SleepResumeCycle
            {
                SleepTime = sleepTime,
                ResumedCleanly = false,
                Outcome = "No resume record found in the scanned window (still asleep, or the log rotated past it).",
                BootTypeHint = NearestBootTypeHint(bootHints, sleepTime),
            });
        }

        foreach (var e in powerEvents)
        {
            if (e.EventId == 42)
            {
                if (pendingSleep is { } prev) CloseUnresolved(prev);
                pendingSleep = e.TimeCreated;
            }
            else if (pendingSleep is { } sleepTime)
            {
                bool resumedCleanly = e.EventId != KernelPowerUncleanId;
                cycles.Add(new SleepResumeCycle
                {
                    SleepTime = sleepTime,
                    ResumeTime = e.TimeCreated,
                    ResumedCleanly = resumedCleanly,
                    Outcome = resumedCleanly
                        ? $"Resumed cleanly (Kernel-Power {e.EventId})."
                        : "Rebooted instead of resuming (Kernel-Power 41 - unclean) - the classic \"wakes up to a black screen\" pattern.",
                    BootTypeHint = NearestBootTypeHint(bootHints, e.TimeCreated),
                });
                pendingSleep = null;
            }
            // A resume-shaped event with no pending sleep is left over from just before the
            // lookback window started - correctly ignored rather than paired with nothing.
        }

        if (pendingSleep is { } last) CloseUnresolved(last);

        return cycles.OrderByDescending(c => c.SleepTime).ToList();
    }

    private static string? NearestBootTypeHint(List<EventRecordRow> bootHints, DateTime around)
    {
        var nearest = bootHints
            .Where(b => Math.Abs((b.TimeCreated - around).TotalMinutes) <= 10)
            .OrderBy(b => Math.Abs((b.TimeCreated - around).TotalMinutes))
            .FirstOrDefault();
        return nearest is null ? null : ClassifyBootType(nearest.Message);
    }

    /// <summary>Best-effort boot-type read from Kernel-Boot 20/27's own rendered message text - no
    /// documented, stable field layout exists for these (the same caveat EventLogService.
    /// ExtractBugcheckCode already documents for a different event), so this is a keyword match over
    /// Windows' own wording rather than a decoded bitmask, and returns "Unknown" rather than a
    /// guess when nothing recognizable is present.</summary>
    private static string ClassifyBootType(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown";
        if (message.Contains("resum", StringComparison.OrdinalIgnoreCase) && message.Contains("hibernat", StringComparison.OrdinalIgnoreCase))
            return "Resume from hibernate";
        if (message.Contains("hybrid", StringComparison.OrdinalIgnoreCase) || message.Contains("fast startup", StringComparison.OrdinalIgnoreCase))
            return "Hybrid boot (Fast Startup)";
        if (message.Contains("cold", StringComparison.OrdinalIgnoreCase))
            return "Cold boot";
        return "Unknown";
    }

    // ==== #143: "who rebooted this PC" ====

    private static readonly Regex ProcessRegex = new(@"^The process\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UserRegex = new(@"on behalf of user\s+(.+?)\s+for the following reason", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReasonRegex = new(@"following reason:\s*(.+?)(?:\r?\n\s*Reason Code|\r?\nComment|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CommentRegex = new(@"Comment:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>#143: parses User32 1074 (which process/user initiated a shutdown/restart, with
    /// reason and comment) alongside Kernel-General 13 and EventLog 6006/6008, clustering markers
    /// within 5 minutes of each other (the same events routinely get logged seconds apart around one
    /// real shutdown) into one plain-English answer per shutdown instance: a Windows Update restart,
    /// a named user/app, or - when none of the three clean-shutdown markers showed up at all - "not
    /// a clean shutdown". 1074's process/user/reason/comment fields are parsed from its own rendered
    /// message text (no documented, versioned property layout exists across Windows versions for
    /// these), so a wording this app doesn't recognize degrades to nulls rather than a guess -
    /// Answer itself always falls back to the raw message when nothing else could be parsed.</summary>
    public List<RebootAttribution> ComputeRebootAttributions(int lookbackDays = 30, int maxRecords = 2000)
    {
        var markers = new List<(DateTime Time, EventRecordRow Row, int Kind)>(); // Kind: 0=1074, 1=KernelGeneral13, 2=6006, 3=6008
        foreach (var r in ReadEvents("System", "Provider[@Name='User32'] and EventID=1074", lookbackDays, maxRecords))
            markers.Add((r.TimeCreated, r, 0));
        foreach (var r in ReadEvents("System", "Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=13", lookbackDays, maxRecords))
            markers.Add((r.TimeCreated, r, 1));
        foreach (var r in ReadEvents("System", "Provider[@Name='EventLog'] and EventID=6006", lookbackDays, maxRecords))
            markers.Add((r.TimeCreated, r, 2));
        foreach (var r in ReadEvents("System", "Provider[@Name='EventLog'] and EventID=6008", lookbackDays, maxRecords))
            markers.Add((r.TimeCreated, r, 3));

        markers.Sort((a, b) => a.Time.CompareTo(b.Time));

        var results = new List<RebootAttribution>();
        int i = 0;
        while (i < markers.Count)
        {
            int j = i;
            while (j + 1 < markers.Count && (markers[j + 1].Time - markers[j].Time) <= TimeSpan.FromMinutes(5)) j++;
            var cluster = markers.GetRange(i, j - i + 1);
            results.Add(BuildAttribution(cluster));
            i = j + 1;
        }

        return results.OrderByDescending(r => r.Timestamp).ToList();
    }

    private static RebootAttribution BuildAttribution(List<(DateTime Time, EventRecordRow Row, int Kind)> cluster)
    {
        var attribution1074 = cluster.FirstOrDefault(c => c.Kind == 0);
        var has13 = cluster.Any(c => c.Kind == 1);
        var has6006 = cluster.Any(c => c.Kind == 2);
        var has6008 = cluster.Any(c => c.Kind == 3);

        var timestamp = cluster.Min(c => c.Time);
        bool wasClean = attribution1074.Row is not null || has13 || has6006;
        // #143: a 6008 anywhere in the cluster still means the shutdown wasn't orderly, even if a
        // stray 1074/13/6006 also landed nearby (e.g. an app requested a restart that then failed
        // to complete cleanly).
        if (has6008) wasClean = false;

        if (attribution1074.Row is { } row)
        {
            string message = row.Message ?? string.Empty;
            string? process = ProcessRegex.Match(message) is { Success: true } pm ? pm.Groups[1].Value.Trim() : null;
            string? user = UserRegex.Match(message) is { Success: true } um ? um.Groups[1].Value.Trim() : null;
            string? reason = ReasonRegex.Match(message) is { Success: true } rm ? rm.Groups[1].Value.Trim() : null;
            string? comment = CommentRegex.Match(message) is { Success: true } cm && !string.IsNullOrWhiteSpace(cm.Groups[1].Value) ? cm.Groups[1].Value.Trim() : null;

            bool isWindowsUpdate = (reason?.Contains("Operating System: Upgrade", StringComparison.OrdinalIgnoreCase) ?? false)
                || (reason?.Contains("Windows Update", StringComparison.OrdinalIgnoreCase) ?? false)
                || (process?.Contains("TiWorker", StringComparison.OrdinalIgnoreCase) ?? false)
                || (process?.Contains("TrustedInstaller", StringComparison.OrdinalIgnoreCase) ?? false);

            string answer = isWindowsUpdate
                ? $"A Windows Update / servicing restart{(process is null ? string.Empty : $" ({process})")}{(reason is null ? string.Empty : $" — {reason}")}."
                : process is not null || user is not null
                    ? $"{(user is null ? "A user" : user)} restarted/shut down the PC{(process is null ? string.Empty : $" via {process}")}{(reason is null ? string.Empty : $" — {reason}")}."
                    : message;

            return new RebootAttribution
            {
                Timestamp = timestamp,
                Answer = answer,
                InitiatingProcess = process,
                InitiatingUser = user,
                ReasonText = reason,
                Comment = comment,
                WasCleanShutdown = wasClean,
                WasWindowsUpdate = isWindowsUpdate,
            };
        }

        if (has13)
        {
            return new RebootAttribution
            {
                Timestamp = timestamp,
                Answer = "Clean shutdown — Windows logged an orderly shutdown (Kernel-General 13), but no specific initiating process or user was recorded.",
                WasCleanShutdown = true,
            };
        }

        if (has6006)
        {
            return new RebootAttribution
            {
                Timestamp = timestamp,
                Answer = "Clean shutdown (EventLog 6006) — no specific initiating process or user was recorded.",
                WasCleanShutdown = true,
            };
        }

        return new RebootAttribution
        {
            Timestamp = timestamp,
            Answer = "Not a clean shutdown — the system restarted or lost power without an orderly shutdown being logged (EventLog 6008).",
            WasCleanShutdown = false,
        };
    }

    // ==== #144: uptime and session ledger ====

    /// <summary>#144: a per-boot table from EventLog 6005 (session start) to the next 6006 (clean
    /// stop) / 6008 (unclean stop) - EventLog 6009 (OS version, logged once right after 6005) and
    /// 6013 (periodic uptime ping, roughly daily) are read per the item's spec but don't drive
    /// session boundaries here (6005 alone already marks every real boot; 6009 is a near-duplicate
    /// timestamp and 6013 is mid-session telemetry, not a boundary) - both still degrade a session
    /// with no matching end marker to EndTime=null ("still running, or the log rotated past it")
    /// rather than a guessed end time. BootType is a best-effort Kernel-Boot 27 read - see
    /// ClassifyBootType's remarks.</summary>
    public List<BootSessionRow> BuildBootLedger(int lookbackDays = 30, int maxRecords = 2000)
    {
        var events = ReadEvents("System", "Provider[@Name='EventLog'] and (EventID=6005 or EventID=6006 or EventID=6008 or EventID=6009 or EventID=6013)", lookbackDays, maxRecords)
            .OrderBy(r => r.TimeCreated).ToList();
        var bootHints = ReadEvents("System", $"Provider[@Name='{KernelBootProvider}'] and EventID=27", lookbackDays, maxRecords)
            .OrderBy(r => r.TimeCreated).ToList();

        var sessions = new List<BootSessionRow>();
        DateTime? sessionStart = null;
        string bootType = "Unknown";

        void CloseOpenSession(string reason)
        {
            if (sessionStart is not { } start) return;
            sessions.Add(new BootSessionRow { StartTime = start, EndTime = null, EndedCleanly = false, EndReason = reason, BootType = bootType });
        }

        foreach (var e in events)
        {
            if (e.EventId == 6005)
            {
                // A new boot-start marker while the previous session never saw an end marker - the
                // gap between them was a crash/power-loss/rotated-log gap, not a clean end.
                CloseOpenSession("No shutdown record found before the next boot — likely a crash, power loss, or forced power-off.");
                sessionStart = e.TimeCreated;
                bootType = NearestBootTypeHint(bootHints, e.TimeCreated) ?? "Unknown";
            }
            else if ((e.EventId == 6006 || e.EventId == 6008) && sessionStart is { } start)
            {
                sessions.Add(new BootSessionRow
                {
                    StartTime = start,
                    EndTime = e.TimeCreated,
                    EndedCleanly = e.EventId == 6006,
                    EndReason = e.EventId == 6006 ? "Clean shutdown (EventLog 6006)" : "Unclean shutdown/reboot (EventLog 6008)",
                    BootType = bootType,
                });
                sessionStart = null;
            }
            // 6009 (OS version at boot) and 6013 (periodic uptime ping) are read per #144's spec but
            // carry no session-boundary information beyond what 6005/6006/6008 already give.
        }

        if (sessionStart is { } lastStart)
        {
            sessions.Add(new BootSessionRow
            {
                StartTime = lastStart,
                EndTime = null,
                EndedCleanly = false,
                EndReason = "Still running, or no shutdown record found in the scanned window.",
                BootType = bootType,
            });
        }

        return sessions.OrderByDescending(s => s.StartTime).ToList();
    }
}
