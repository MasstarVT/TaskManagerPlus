using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, items 89-95: "Clustering crashes over time and correlating with changes" - a unified
/// cross-cutting layer over EVERY per-source crash/fault collection items 1-88 already parse
/// (bugchecks/minidumps, live kernel reports, WER reports, application crashes/hangs, service
/// failures, TDRs, WHEA errors, unexpected shutdowns), plus a small number of genuinely new,
/// on-demand queries for items 92-94's "what changed" correlation. Every method here is a pure
/// function/query over already-loaded Model data (BuildTimeline/BuildClusters/
/// BuildUptimeHistogramAndMtbf) or a bounded, best-effort, degrade-to-nothing scan
/// (BuildWhatChangedAsync/BuildLogCorrelationAsync) - nothing here re-runs any of the event-log
/// queries StabilityViewModel's earlier refresh steps already paid for.
/// </summary>
public static class CrashCorrelationService
{
    // =====================================================================================
    // Item 89: unified crash timeline - one CrashTimelineRow per already-parsed occurrence,
    // across every source this tab tracks, newest first.
    // =====================================================================================

    public static List<CrashTimelineRow> BuildTimeline(
        List<MinidumpInfo> minidumps,
        List<ParsedDumpInfo> parsedDumps,
        List<LiveKernelReportInfo> liveKernelReports,
        List<WerReport> werCrashReports,
        List<WerReport> werHangReports,
        List<ApplicationCrashEvent> applicationCrashes,
        List<ApplicationHangEvent> applicationHangs,
        List<ServiceFailureEvent> serviceFailures,
        List<TdrEventDetail> tdrEvents,
        List<WheaErrorEvent> wheaErrors,
        List<UnexpectedShutdownRecord> unexpectedShutdowns)
    {
        // Item 90's kernel clustering key needs a blamed module, which only the binary-parsed
        // ParsedDumpInfo carries (MinidumpInfo is the event-log-correlated side) - joined here by
        // file name rather than re-parsing anything.
        var blamedModuleByFileName = parsedDumps
            .Where(p => !string.IsNullOrEmpty(p.FileName))
            .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().BlamedModule, StringComparer.OrdinalIgnoreCase);

        var rows = new List<CrashTimelineRow>();

        foreach (var m in minidumps)
        {
            blamedModuleByFileName.TryGetValue(m.FileName, out var blamedModule);
            rows.Add(new CrashTimelineRow
            {
                Timestamp = m.Timestamp,
                SourceType = CrashTimelineSourceType.Bugcheck,
                SourceTypeText = "Bugcheck",
                Severity = CrashTimelineSeverity.Critical,
                Summary = $"Bugcheck {m.BugcheckCode ?? "Unknown"} — {BugcheckCodeLookup.Describe(m.BugcheckCode)}"
                    + (blamedModule is null ? string.Empty : $" ({blamedModule})"),
                Detail = m.FileName,
                ClusterKey = BuildKernelClusterKey(m.BugcheckCode, blamedModule, m.BugcheckParameters),
            });
        }

        foreach (var l in liveKernelReports)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = l.Timestamp,
                SourceType = CrashTimelineSourceType.LiveKernelReport,
                SourceTypeText = "Live kernel report",
                Severity = CrashTimelineSeverity.Warning,
                Summary = $"Live kernel report — {l.Category}" + (l.WerDescription is null ? string.Empty : $" ({l.WerDescription})"),
                Detail = l.FileName,
                ClusterKey = $"LiveKernel:{l.Category}",
            });
        }

        foreach (var w in werCrashReports)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = w.ReportTimestamp,
                SourceType = CrashTimelineSourceType.WerCrash,
                SourceTypeText = "WER crash report",
                Severity = CrashTimelineSeverity.Critical,
                Summary = $"{w.AppName ?? "Unknown app"} crashed" + (w.ModName is null ? string.Empty : $" on {w.ModName}")
                    + (w.ExceptionCode is null ? string.Empty : $" ({w.ExceptionCode})"),
                Detail = w.ReportFolder,
                ClusterKey = $"Wer:{w.EffectiveBucketKey}",
            });
        }

        foreach (var w in werHangReports)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = w.ReportTimestamp,
                SourceType = CrashTimelineSourceType.WerHang,
                SourceTypeText = "WER hang report",
                Severity = CrashTimelineSeverity.Warning,
                Summary = $"{w.AppName ?? "Unknown app"} stopped responding" + (w.EventType is null ? string.Empty : $" ({w.EventType})"),
                Detail = w.ReportFolder,
                ClusterKey = $"WerHang:{w.EffectiveBucketKey}",
            });
        }

        foreach (var c in applicationCrashes)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = c.TimeCreated,
                SourceType = CrashTimelineSourceType.ApplicationCrash,
                SourceTypeText = "Application crash",
                Severity = CrashTimelineSeverity.Critical,
                Summary = $"{c.AppName ?? "Unknown app"} crashed" + (c.ModName is null ? string.Empty : $" on {c.ModName}")
                    + $" ({c.ExceptionCodeText})",
                Detail = c.IsForeignModule ? $"Foreign module: {c.ForeignModuleReason}" : null,
            });
        }

        foreach (var h in applicationHangs)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = h.TimeCreated,
                SourceType = CrashTimelineSourceType.ApplicationHang,
                SourceTypeText = "Application hang",
                Severity = CrashTimelineSeverity.Warning,
                Summary = $"{h.ProcessName ?? "Unknown app"} stopped responding" + (h.HangType is null ? string.Empty : $" ({h.HangType})"),
            });
        }

        foreach (var s in serviceFailures)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = s.TimeCreated,
                SourceType = CrashTimelineSourceType.ServiceFailure,
                SourceTypeText = "Service failure",
                Severity = CrashTimelineSeverity.Warning,
                Summary = $"{s.ServiceName ?? "Unknown service"} — {s.EventKindText}",
            });
        }

        foreach (var t in tdrEvents)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = t.TimeCreated,
                SourceType = CrashTimelineSourceType.Tdr,
                SourceTypeText = "GPU timeout (TDR)",
                Severity = CrashTimelineSeverity.Warning,
                Summary = "GPU driver timeout/reset" + (t.Driver is null ? string.Empty : $" — {t.Driver}")
                    + (t.Application is null ? string.Empty : $" ({t.Application})"),
            });
        }

        foreach (var w in wheaErrors)
        {
            bool critical = w.Severity.Contains("Fatal", StringComparison.OrdinalIgnoreCase)
                || w.Severity.Contains("Uncorrected", StringComparison.OrdinalIgnoreCase);
            rows.Add(new CrashTimelineRow
            {
                Timestamp = w.TimeCreated,
                SourceType = CrashTimelineSourceType.Whea,
                SourceTypeText = "Hardware error (WHEA)",
                Severity = critical ? CrashTimelineSeverity.Critical : CrashTimelineSeverity.Warning,
                Summary = $"WHEA {w.Severity} — {w.Source}",
                Detail = w.Decoded,
            });
        }

        foreach (var u in unexpectedShutdowns)
        {
            rows.Add(new CrashTimelineRow
            {
                Timestamp = u.TimeCreated,
                SourceType = CrashTimelineSourceType.UnexpectedShutdown,
                SourceTypeText = "Unexpected shutdown",
                Severity = CrashTimelineSeverity.Critical,
                Summary = $"Unexpected shutdown — {u.Cause}" + (u.BugcheckCode is null ? string.Empty : $" ({u.BugcheckCode})"),
                Detail = u.UptimeBeforeCrash is { } up ? $"Uptime before: {FormatTimeSpan(up)}" : null,
            });
        }

        return rows.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>Item 90: coarse parameter "shape" for the kernel clustering key - not the exact
    /// address (crash to crash, a literal pointer value almost never repeats even for genuinely
    /// the same bug), but which parameters are zero / a small value / an address-sized value tends
    /// to be stable for the same underlying fault. "Quick flag, not a verdict" per CLAUDE.md - two
    /// different bugs that happen to share a shape will still merge into one cluster here.</summary>
    private static string ShapeOf(IReadOnlyList<string> parameters)
    {
        var parts = new List<string>();
        foreach (var p in parameters.Take(4))
        {
            if (!BugcheckHex.TryParse(p, out var value)) { parts.Add("?"); continue; }
            parts.Add(value == 0 ? "0" : value <= 0xFFFF ? "S" : "A");
        }
        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    private static string BuildKernelClusterKey(string? bugcheckCode, string? blamedModule, IReadOnlyList<string> parameters)
        => $"Kernel:{bugcheckCode ?? "Unknown"}:{blamedModule ?? "Unknown"}:{ShapeOf(parameters)}";

    // =====================================================================================
    // Item 90: crash signature clusters - groups the same timeline rows BuildTimeline just
    // produced by their own ClusterKey, so "thirty rows" becomes "three distinct problems"
    // without a second pass over the raw per-source data.
    // =====================================================================================

    public static List<CrashCluster> BuildClusters(List<CrashTimelineRow> timeline)
    {
        var clusters = new List<CrashCluster>();

        foreach (var g in timeline.Where(r => r.ClusterKey is not null).GroupBy(r => r.ClusterKey!))
        {
            var ordered = g.OrderBy(r => r.Timestamp).ToList();
            var first = ordered[0];
            var last = ordered[^1];
            bool isKernel = first.SourceType is CrashTimelineSourceType.Bugcheck or CrashTimelineSourceType.LiveKernelReport;
            string kindLabel = first.SourceType switch
            {
                CrashTimelineSourceType.Bugcheck => "Kernel-mode bugcheck",
                CrashTimelineSourceType.LiveKernelReport => "Live kernel report (watchdog)",
                CrashTimelineSourceType.WerCrash => "User-mode crash (WER bucket)",
                CrashTimelineSourceType.WerHang => "User-mode hang (WER bucket)",
                _ => "Fault",
            };

            clusters.Add(new CrashCluster
            {
                ClusterKey = g.Key,
                IsKernelFault = isKernel,
                Title = first.Summary,
                Description = $"{kindLabel} — {ordered.Count} occurrence(s).",
                Count = ordered.Count,
                FirstSeen = first.Timestamp,
                LastSeen = last.Timestamp,
                CadenceText = BuildCadenceText(ordered.Count, first.Timestamp, last.Timestamp),
                Occurrences = ordered,
            });
        }

        return clusters.OrderByDescending(c => c.Count).ThenByDescending(c => c.LastSeen).ToList();
    }

    private static string BuildCadenceText(int count, DateTime first, DateTime last)
    {
        if (count <= 1) return "Seen once.";
        var span = last - first;
        if (span <= TimeSpan.Zero) return $"{count} occurrences (all within the same short window).";
        var avgGap = span / (count - 1);
        return $"{count} occurrences over {FormatTimeSpan(span)} — about every {FormatTimeSpan(avgGap)}.";
    }

    // =====================================================================================
    // Item 91: uptime-at-crash histogram + MTBF/longest-streak.
    // =====================================================================================

    private static readonly (TimeSpan Max, string Label)[] UptimeBuckets =
    {
        (TimeSpan.FromMinutes(2), "< 2 min"),
        (TimeSpan.FromMinutes(15), "2–15 min"),
        (TimeSpan.FromHours(1), "15–60 min"),
        (TimeSpan.FromHours(6), "1–6 h"),
        (TimeSpan.FromHours(24), "6–24 h"),
        (TimeSpan.MaxValue, "> 24 h"),
    };

    /// <summary>
    /// Item 91: buckets each crash timestamp by how long the machine had been up (since the
    /// nearest preceding boot marker) before it crashed, and computes mean time between failures
    /// plus the longest crash-free streak (including the ongoing streak since the most recent
    /// crash, if that's actually the longest one seen). Crash timestamps within 5 minutes of each
    /// other are treated as one event (the same underlying crash often shows up more than once
    /// across sources, e.g. both a minidump and an unexpected-shutdown record).
    /// </summary>
    public static (List<UptimeAtCrashBucket> Histogram, MtbfSummary Mtbf) BuildUptimeHistogramAndMtbf(
        List<DateTime> crashTimes, List<DateTime> bootTimes)
    {
        var crashes = Deduplicate(crashTimes, TimeSpan.FromMinutes(5));
        var boots = bootTimes.Distinct().OrderBy(t => t).ToList();

        var counts = new int[UptimeBuckets.Length];
        int unknown = 0;
        foreach (var crash in crashes)
        {
            DateTime? boot = null;
            foreach (var b in boots)
            {
                if (b > crash) break;
                boot = b;
            }
            if (boot is null) { unknown++; continue; }

            var uptime = crash - boot.Value;
            for (int i = 0; i < UptimeBuckets.Length; i++)
            {
                if (uptime <= UptimeBuckets[i].Max) { counts[i]++; break; }
            }
        }

        var histogram = UptimeBuckets.Select((b, i) => new UptimeAtCrashBucket { Label = b.Label, Count = counts[i] }).ToList();

        TimeSpan? mtbf = null;
        TimeSpan? longestStreak = null;
        if (crashes.Count >= 2)
        {
            var gaps = new List<TimeSpan>();
            for (int i = 1; i < crashes.Count; i++) gaps.Add(crashes[i] - crashes[i - 1]);
            mtbf = TimeSpan.FromTicks((long)gaps.Average(g => g.Ticks));
            longestStreak = gaps.Max();
        }
        if (crashes.Count >= 1)
        {
            // The streak since the last known crash can itself be the longest one - a machine
            // that crashed constantly last month and hasn't since deserves "longest streak" to
            // say so, not just report the largest historical gap.
            var current = DateTime.Now - crashes[^1];
            if (longestStreak is null || current > longestStreak) longestStreak = current;
        }

        return (histogram, new MtbfSummary
        {
            CrashCount = crashes.Count,
            BootCount = boots.Count,
            MeanTimeBetweenFailures = mtbf,
            LongestCrashFreeStreak = longestStreak,
            UnknownUptimeCount = unknown,
        });
    }

    private static List<DateTime> Deduplicate(List<DateTime> times, TimeSpan window)
    {
        var sorted = times.Distinct().OrderBy(t => t).ToList();
        var result = new List<DateTime>();
        foreach (var t in sorted)
        {
            if (result.Count > 0 && t - result[^1] <= window) continue;
            result.Add(t);
        }
        return result;
    }

    public static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }

    // =====================================================================================
    // Items 92-94: "what changed in the 48 hours before this cluster's first occurrence" - the
    // only genuinely new queries in this chunk, and deliberately NOT run for every cluster on
    // every refresh (see CrashClusterViewModel) - setupapi.dev.log parsing / event-log queries /
    // WMI / registry sweeps here are cheap individually but not free, and most clusters are never
    // actually expanded.
    // =====================================================================================

    private const int WhatChangedWindowHours = 48;

    public static async Task<WhatChangedResult> BuildWhatChangedAsync(DateTime clusterFirstSeen)
    {
        var start = clusterFirstSeen.AddHours(-WhatChangedWindowHours);
        try
        {
            var entries = new List<WhatChangedEntry>();
            entries.AddRange(ReadDriverInstallsFromSetupApiLog(start, clusterFirstSeen));
            entries.AddRange(ReadUserPnpDriverInstalls(start, clusterFirstSeen));
            entries.AddRange(await ReadPnputilDriverDatesAsync(start, clusterFirstSeen));
            entries.AddRange(ReadWindowsUpdateInstalls(start, clusterFirstSeen));
            entries.AddRange(ReadQuickFixEngineering(start, clusterFirstSeen));
            entries.AddRange(ReadMsiInstalls(start, clusterFirstSeen));
            entries.AddRange(ReadRegistryAppInstalls(start, clusterFirstSeen));

            // The same real-world change is sometimes visible from more than one source (e.g. a
            // driver install showing up in both setupapi.dev.log and UserPnp 20001) - collapse
            // near-duplicates (same category, same hour, same description) rather than showing
            // the same change twice.
            var deduped = entries
                .GroupBy(e => (e.Category, Description: e.Description.Trim().ToLowerInvariant(), Hour: new DateTime(e.Timestamp.Year, e.Timestamp.Month, e.Timestamp.Day, e.Timestamp.Hour, 0, 0)))
                .Select(g => g.OrderBy(e => e.Timestamp).First())
                .OrderBy(e => e.Timestamp)
                .ToList();

            return new WhatChangedResult { ComputedOk = true, Entries = deduped };
        }
        catch (Exception ex)
        {
            return new WhatChangedResult { ComputedOk = false, ErrorText = ex.Message };
        }
    }

    // ---- Item 92: driver installs ----------------------------------------------------------

    private static readonly Regex SetupApiHeaderRegex = new(@"^>>>\s*\[(.+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex SetupApiSectionStartRegex = new(@"^>>>\s*Section start\s+(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)", RegexOptions.Compiled);

    /// <summary>Item 92: %SystemRoot%\INF\setupapi.dev.log - a plain text log every PnP driver
    /// install writes a "Section start"-timestamped block to, in a stable, well-known shape
    /// (">>>  [Device Install ... - <device>]" immediately followed by ">>>  Section start
    /// yyyy/MM/dd HH:mm:ss.fff") that's been consistent since Windows Vista. Read forwards (the
    /// log is append-only, oldest first) with a bounded line count as a safety valve for an old
    /// machine's multi-year log.</summary>
    private static List<WhatChangedEntry> ReadDriverInstallsFromSetupApiLog(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");
            if (!File.Exists(path)) return results;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? pendingHeader = null;
            int linesScanned = 0;
            const int maxLines = 500_000;
            const int maxResults = 100;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (++linesScanned > maxLines || results.Count >= maxResults) break;

                var headerMatch = SetupApiHeaderRegex.Match(line);
                if (headerMatch.Success) { pendingHeader = headerMatch.Groups[1].Value; continue; }

                var startMatch = SetupApiSectionStartRegex.Match(line);
                if (!startMatch.Success) continue;

                if (DateTime.TryParseExact(startMatch.Groups[1].Value, "yyyy/MM/dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                    && ts >= start && ts <= end && pendingHeader is not null)
                {
                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = ts,
                        Category = "Driver install",
                        Description = pendingHeader,
                        Source = "setupapi.dev.log",
                    });
                }
                pendingHeader = null;
            }
        }
        catch
        {
            // Log missing/locked/unreadable - contributes nothing, degrades gracefully.
        }
        return results;
    }

    /// <summary>Item 92: Microsoft-Windows-UserPnp event 20001 ("Driver Management concluded the
    /// process to install driver ..."), a second independent source for the same driver-install
    /// timeline as setupapi.dev.log above (BuildWhatChangedAsync de-dupes the two).</summary>
    private static List<WhatChangedEntry> ReadUserPnpDriverInstalls(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            long maxAgeMs = (long)Math.Max(1, (DateTime.Now - start).TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-UserPnp'] and (EventID=20001) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var time = record.TimeCreated ?? DateTime.MinValue;
                    if (time < start || time > end) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                    string firstLine = message.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;

                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = time,
                        Category = "Driver install",
                        Description = firstLine.Length > 0 ? firstLine : "Driver installed",
                        Source = "Microsoft-Windows-UserPnp/20001",
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - contributes nothing.
        }
        return results;
    }

    /// <summary>Item 92: `pnputil /enum-drivers` - lists every driver package in the DriverStore
    /// with its own "Driver Version:" line (a date + version number, e.g. "01/15/2024  1.2.3.4").
    /// That date is the driver package's own build/version date, not necessarily the exact install
    /// moment, so this is a weaker (day-precision only) signal than the two event-based sources
    /// above - kept clearly labelled as such via Detail rather than presented with the same
    /// confidence.</summary>
    private static async Task<List<WhatChangedEntry>> ReadPnputilDriverDatesAsync(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            var (output, exitCode) = await RunCapturedAsync("pnputil.exe", "/enum-drivers", 20000);
            if (exitCode is null || string.IsNullOrEmpty(output)) return results;

            string? originalName = null;
            string? providerName = null;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    originalName = providerName = null;
                    continue;
                }

                int idx = line.IndexOf(':');
                if (idx < 0) continue;
                string key = line[..idx].Trim();
                string value = line[(idx + 1)..].Trim();

                if (key.Contains("Original Name", StringComparison.OrdinalIgnoreCase)) originalName = value;
                else if (key.Contains("Provider Name", StringComparison.OrdinalIgnoreCase)) providerName = value;
                else if (key.Contains("Driver Version", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                    if (date.Date < start.Date || date.Date > end.Date) continue;

                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = date.Date,
                        Category = "Driver install",
                        Description = (originalName ?? "Unknown driver") + (providerName is null ? string.Empty : $" ({providerName})"),
                        Detail = "Date is the driver package's own version date (day precision only), not necessarily the exact install time.",
                        Source = "pnputil /enum-drivers",
                    });
                }
            }
        }
        catch
        {
            // pnputil unavailable/failed - contributes nothing.
        }
        return results;
    }

    // ---- Item 93: Windows Update installs --------------------------------------------------

    private static readonly Regex WuTitleRegex = new(@"update:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex KbRegex = new(@"KB\d{6,7}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Item 93: Microsoft-Windows-WindowsUpdateClient event 19 ("Installation Successful:
    /// Windows successfully installed the following update: ...") - the KB number, when present in
    /// the title, is pulled out into Detail so the user can look it up or uninstall it directly.</summary>
    private static List<WhatChangedEntry> ReadWindowsUpdateInstalls(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            long maxAgeMs = (long)Math.Max(1, (DateTime.Now - start).TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var time = record.TimeCreated ?? DateTime.MinValue;
                    if (time < start || time > end) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                    var titleMatch = WuTitleRegex.Match(message);
                    string title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "Windows Update installed";
                    var kbMatch = KbRegex.Match(title.Length > 0 ? title : message);

                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = time,
                        Category = "Windows Update",
                        Description = title,
                        Detail = kbMatch.Success ? kbMatch.Value : null,
                        Source = "Microsoft-Windows-WindowsUpdateClient/19",
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - contributes nothing.
        }
        return results;
    }

    /// <summary>Item 93: Win32_QuickFixEngineering - InstalledOn is day-precision only, so this
    /// complements (rather than replaces) the WindowsUpdateClient event above, catching updates
    /// installed before this app's own 30-day event-log lookback window (or logged by a
    /// non-standard update path event 19 doesn't cover, e.g. some out-of-band/manual .msu
    /// installs).</summary>
    private static List<WhatChangedEntry> ReadQuickFixEngineering(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string? installedOnRaw = mo["InstalledOn"] as string;
                    if (string.IsNullOrWhiteSpace(installedOnRaw)) continue;
                    if (!DateTime.TryParse(installedOnRaw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                        && !DateTime.TryParse(installedOnRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        continue;
                    if (date.Date < start.Date || date.Date > end.Date) continue;

                    string hotfix = mo["HotFixID"] as string ?? "Unknown";
                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = date.Date,
                        Category = "Windows Update",
                        Description = $"{hotfix} installed",
                        Detail = hotfix,
                        Source = "Win32_QuickFixEngineering",
                    });
                }
            }
        }
        catch
        {
            // WMI namespace/class unavailable - contributes nothing.
        }
        return results;
    }

    // ---- Item 94: application installs -----------------------------------------------------

    private static readonly Regex MsiProductRegex = new(@"Product:\s*(.+?)\s*--\s*Installation completed successfully", RegexOptions.Compiled);
    private static readonly Regex MsiProductNameFallbackRegex = new(@"Product Name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Item 94: MsiInstaller events 11707 ("Product: X -- Installation completed
    /// successfully") and 1033, both on the Application log - MSI-based third-party installs
    /// (the large majority of desktop installers).</summary>
    private static List<WhatChangedEntry> ReadMsiInstalls(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();
        try
        {
            long maxAgeMs = (long)Math.Max(1, (DateTime.Now - start).TotalMilliseconds);
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='MsiInstaller'] and (EventID=11707 or EventID=1033) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var time = record.TimeCreated ?? DateTime.MinValue;
                    if (time < start || time > end) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                    var m = MsiProductRegex.Match(message);
                    string product = m.Success
                        ? m.Groups[1].Value.Trim()
                        : MsiProductNameFallbackRegex.Match(message) is { Success: true } fm
                            ? fm.Groups[1].Value.Trim()
                            : Truncate(message, 120);

                    results.Add(new WhatChangedEntry
                    {
                        Timestamp = time,
                        Category = "Application install",
                        Description = $"{product} installed",
                        Source = $"MsiInstaller/{record.Id}",
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - contributes nothing.
        }
        return results;
    }

    private static readonly string[] AppInstallRegistryRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>Item 94: InstallDate under the per-app Uninstall registry keys (both native and
    /// the 32-bit view, machine-wide and per-user) - day precision only (the registry value is a
    /// plain "yyyyMMdd" string, not a real timestamp), so this is a coarse fallback alongside the
    /// MSI event-log source above; it also catches non-MSI installers that still register an
    /// Uninstall entry.</summary>
    private static List<WhatChangedEntry> ReadRegistryAppInstalls(DateTime start, DateTime end)
    {
        var results = new List<WhatChangedEntry>();

        void ScanHive(RegistryKey hive)
        {
            foreach (var keyPath in AppInstallRegistryRoots)
            {
                try
                {
                    using var root = hive.OpenSubKey(keyPath);
                    if (root is null) continue;

                    foreach (var subName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = root.OpenSubKey(subName);
                            if (sub is null) continue;

                            string name = (sub.GetValue("DisplayName") as string ?? string.Empty).Trim();
                            if (name.Length == 0) continue;
                            if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                            string dateRaw = (sub.GetValue("InstallDate") as string ?? string.Empty).Trim();
                            if (dateRaw.Length != 8 || !DateTime.TryParseExact(dateRaw, "yyyyMMdd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var installDate))
                                continue;
                            if (installDate.Date < start.Date || installDate.Date > end.Date) continue;

                            string publisher = (sub.GetValue("Publisher") as string ?? string.Empty).Trim();
                            results.Add(new WhatChangedEntry
                            {
                                Timestamp = installDate,
                                Category = "Application install",
                                Description = publisher.Length > 0 ? $"{name} ({publisher})" : name,
                                Detail = "InstallDate has day precision only - no time of day recorded.",
                                Source = "Uninstall registry key",
                            });
                        }
                        catch
                        {
                            // One malformed subkey shouldn't stop the rest of the scan.
                        }
                    }
                }
                catch
                {
                    // Registry hive/path unavailable - contributes nothing.
                }
            }
        }

        try { ScanHive(Registry.LocalMachine); } catch { /* best-effort */ }
        try { ScanHive(Registry.CurrentUser); } catch { /* best-effort */ }

        return results.GroupBy(r => r.Description, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>Shells out and captures stdout under a bounded timeout - the same pattern
    /// CrashDumpConfigService/PowerPlanService/DriverVerifierService each already establish
    /// independently elsewhere in this app.</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch { return (string.Empty, null); }
        if (proc is null) return (string.Empty, null);

        using (proc)
        {
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* best-effort */ }
                return (string.Empty, null);
            }

            string output = await outputTask;
            await errorTask;
            return (output, proc.ExitCode);
        }
    }

    // =====================================================================================
    // Item 95: correlate a crash timestamp with this app's own logged telemetry (a manual log
    // or the always-on rolling buffer) - the two minutes immediately before the crash.
    // =====================================================================================

    private const int LogCorrelationWindowMinutes = 2;

    public static Task<CrashLogCorrelationResult> BuildLogCorrelationAsync(DateTime crashTimestamp) => Task.Run(() =>
    {
        var windowStart = crashTimestamp.AddMinutes(-LogCorrelationWindowMinutes);
        string logsDir = AppPaths.GetPath("Logs");
        if (!Directory.Exists(logsDir))
        {
            return new CrashLogCorrelationResult
            {
                HasCoverage = false,
                StatusText = "No Logs folder found - logging has never run on this machine.",
            };
        }

        List<string> candidates;
        try
        {
            // A 60MB safety cap on an on-demand, per-row parse - this app's own logs rotate at
            // 100MB (LoggingService.MaxLogFileBytes), so a file bigger than that here is almost
            // certainly something else entirely, not one of this app's own CSVs.
            candidates = Directory.EnumerateFiles(logsDir, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(f => new FileInfo(f).Length is > 0 and < 60L * 1024 * 1024)
                .ToList();
        }
        catch (Exception ex)
        {
            return new CrashLogCorrelationResult { HasCoverage = false, StatusText = $"Couldn't list the Logs folder: {ex.Message}" };
        }

        CrashLogCorrelationResult? best = null;
        int bestPointCount = 0;
        foreach (var path in candidates)
        {
            var (result, _) = LogReplayService.Parse(path);
            if (result is null || result.Timestamps.Count == 0) continue;
            // Quick reject: this file's own time range doesn't even touch the crash window.
            if (result.Timestamps[0] > crashTimestamp || result.Timestamps[^1] < windowStart) continue;

            var points = new List<CrashLogCorrelationPoint>();
            for (int i = 0; i < result.Timestamps.Count; i++)
            {
                var ts = result.Timestamps[i];
                if (ts < windowStart || ts > crashTimestamp) continue;
                points.Add(new CrashLogCorrelationPoint
                {
                    Timestamp = ts,
                    CpuPercent = i < result.CpuPercent.Count ? result.CpuPercent[i] : 0,
                    RamPercent = i < result.RamPercent.Count ? result.RamPercent[i] : 0,
                    TemperatureC = result.TemperatureC is { } t && i < t.Count ? t[i] : null,
                    PowerW = result.PowerW is { } p && i < p.Count ? p[i] : null,
                });
            }

            // More than one log file can technically overlap the window (e.g. a manual log and
            // the rolling buffer both running) - prefer whichever actually has the most samples
            // in the window rather than just the first candidate found.
            if (points.Count > bestPointCount)
            {
                bestPointCount = points.Count;
                best = new CrashLogCorrelationResult
                {
                    HasCoverage = points.Count > 0,
                    SourceFileName = Path.GetFileName(path),
                    Points = points,
                    StatusText = $"{points.Count} sample(s) from {Path.GetFileName(path)}, covering the {LogCorrelationWindowMinutes} minute(s) before this event.",
                };
            }
        }

        return best ?? new CrashLogCorrelationResult
        {
            HasCoverage = false,
            StatusText = "No logged telemetry (manual log or rolling buffer) covers this event's timestamp.",
        };
    });
}
