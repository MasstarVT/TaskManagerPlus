using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #901-908: the check logic behind every Troubleshoot tab symptom branch. Every public method
/// here is a single, focused check that returns a <see cref="DiagnosticStepResult"/> - never
/// throws past this class (each is wrapped in its own try/catch, degrading to a Skipped/Warning
/// result with an explanatory summary rather than propagating an exception into the step runner),
/// mirroring the rest of this app's Services/ layer. Deliberately takes plain data (doubles,
/// <see cref="ProcessRow"/> lists, ...) rather than ViewModel references - CLAUDE.md's Services/
/// layer has no UI dependencies, so TroubleshootViewModel pulls the live values off
/// PerformanceViewModel/ProcessesViewModel itself and passes them in, the same shape any other
/// Service in this app takes a snapshot rather than a ViewModel.
///
/// Where a branch's steps need to share state (a driver-install correlation needs the crash times
/// an earlier step found; the boot/shutdown culprit join needs the events an earlier step parsed),
/// a small mutable *Context class is threaded through the branch's step closures by
/// TroubleshootViewModel - simpler than plumbing return values back out of the fire-and-forget
/// step runner, and scoped to one branch instance so concurrent runs (not currently possible - the
/// UI only allows one run at a time) still wouldn't cross-contaminate.
/// </summary>
public static class TroubleshootService
{
    private const int DefaultLookbackDays = 14;
    private const int CrashLookbackDays = 30;
    private const int BootShutdownLookbackDays = 30;

    // #939/#940/#944 (Timeline, items 938-949): bumped from private to internal so
    // TimelineService can reuse this app's existing event-log-read/shell-out/pnputil-parsing
    // helpers instead of duplicating them - see each call site's own remarks.
    internal static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    #region Shared helpers (event log / shell-out)

    public sealed record RawEvent(DateTime TimeCreated, int EventId, string ProviderName, string Message);

    /// <summary>
    /// Generic "read events matching one or more IDs from a log/provider" reader - the same
    /// EventLogQuery/EventLogReader shape EventLogService already uses for the System/Application
    /// crash scan, generalized to an arbitrary log name, optional provider filter, and ID set so
    /// it can also cover WHEA-Logger, the Diagnostics-Performance boot/shutdown channel, and the
    /// User Profile Service events without duplicating the query-building logic five times.
    /// Degrades to an empty list (channel not enabled/present, access denied, provider unknown on
    /// this Windows build) rather than throwing.
    /// </summary>
    internal static List<RawEvent> ReadProviderEvents(string logName, string? providerName, IReadOnlyList<int> eventIds, int lookbackDays, int maxEvents = 100)
    {
        var results = new List<RawEvent>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));
            string providerFilter = providerName is null ? string.Empty : $"Provider[@Name='{providerName}'] and ";
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[{providerFilter}({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap
                    results.Add(new RawEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, record.ProviderName ?? string.Empty, message));
                }
            }
        }
        catch
        {
            // Log/provider unavailable (channel disabled, access denied, doesn't exist on this
            // Windows build) - degrade to "nothing found", same as every EventLogService read.
        }
        return results;
    }

    /// <summary>
    /// Like <see cref="ReadProviderEvents"/> but with no event-ID filter at all - used for #913's
    /// USBHUB3 diagnostic channel, whose event-ID set isn't documented as stably as Kernel-PnP's
    /// and (on most systems) isn't enabled by default in the first place, so "channel not present"
    /// degrades to the same empty-list result as "nothing logged" rather than a separate failure.
    /// </summary>
    private static List<RawEvent> ReadAllProviderEvents(string logName, int lookbackDays, int maxEvents = 50)
    {
        var results = new List<RawEvent>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }
                    results.Add(new RawEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, record.ProviderName ?? string.Empty, message));
                }
            }
        }
        catch
        {
            // Channel not present/enabled/access denied - degrade to "nothing found".
        }
        return results;
    }

    /// <summary>
    /// Shells out and captures combined stdout+stderr under a real timeout - the same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern PowerPlanService.RunCapturedAsync and
    /// TracerouteService.RunAsync already established, reused here rather than duplicated again.
    /// </summary>
    internal static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }

    #endregion

    #region 902 - "My PC is slow right now"

    /// <summary>Averages the last ~10 samples already sitting in the shared performance sampler's
    /// history buffers rather than sampling fresh - "what's actually been happening for the last
    /// several seconds", not an instantaneous reading that a single busy tick can skew.</summary>
    public static DiagnosticStepResult CheckResourceAverages(IReadOnlyList<double> cpuHistory, IReadOnlyList<double> ramHistory, IReadOnlyList<double> diskHistory)
    {
        try
        {
            double cpuAvg = AverageLast(cpuHistory);
            double ramAvg = AverageLast(ramHistory);
            double diskAvg = AverageLast(diskHistory);

            var evidence = new List<string>
            {
                $"CPU: {cpuAvg:0.#}% (avg of last {Math.Min(10, cpuHistory.Count)}s)",
                $"RAM: {ramAvg:0.#}%",
                $"Disk active time: {diskAvg:0.#}%",
            };

            var flagged = new List<string>();
            if (cpuAvg >= 85) flagged.Add("CPU");
            if (ramAvg >= 90) flagged.Add("RAM");
            if (diskAvg >= 80) flagged.Add("disk");

            if (flagged.Count > 0)
                return DiagnosticStepResult.Warn($"{string.Join(" and ", flagged)} running high over the last several seconds.", evidence);
            return DiagnosticStepResult.Pass("CPU, RAM, and disk all look normal over the last several seconds.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read live performance history: {ex.Message}");
        }
    }

    private static double AverageLast(IReadOnlyList<double> history, int sampleCount = 10)
    {
        if (history.Count == 0) return 0;
        int take = Math.Min(sampleCount, history.Count);
        double sum = 0;
        for (int i = history.Count - take; i < history.Count; i++) sum += history[i];
        return sum / take;
    }

    /// <summary>Top CPU/RAM consumers by the Processes tab's already-tracked 10-second CPU
    /// average (#11's CpuPercent10sAvg), not the instantaneous per-tick reading.</summary>
    public static DiagnosticStepResult CheckTopOffenders(IReadOnlyList<ProcessRow> processes)
    {
        try
        {
            var byCpu = processes.Where(p => p.CpuPercent10sAvg > 1).OrderByDescending(p => p.CpuPercent10sAvg).Take(5).ToList();
            var byMem = processes.OrderByDescending(p => p.MemoryMb).Take(5).ToList();

            if (byCpu.Count == 0)
                return DiagnosticStepResult.Pass("No process is using significant CPU on a 10-second average.");

            var evidence = byCpu.Select(p => $"{p.Name} (PID {p.Pid}): {p.CpuPercent10sAvg:0.#}% CPU (10s avg), {p.MemoryMb:0} MB").ToList();
            evidence.Add("Top memory consumers:");
            evidence.AddRange(byMem.Select(p => $"{p.Name} (PID {p.Pid}): {p.MemoryMb:0} MB"));

            var top = byCpu[0];
            if (top.CpuPercent10sAvg >= 40)
                return DiagnosticStepResult.Warn($"{top.Name} (PID {top.Pid}) is the top CPU consumer at {top.CpuPercent10sAvg:0.#}% (10s average).", evidence);
            return DiagnosticStepResult.Pass($"Top CPU consumer is {top.Name} at {top.CpuPercent10sAvg:0.#}% (10s average) - nothing dominating heavily.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the process list: {ex.Message}");
        }
    }

    /// <summary>LogicalDisk\Avg. Disk sec/Transfer and Current Disk Queue Length - both already
    /// sampled by the shared HardwareMonitorService each tick (PerformanceViewModel.DiskQueueLength/
    /// DiskReadLatencyMs/DiskWriteLatencyMs), reused rather than re-read here.</summary>
    public static DiagnosticStepResult CheckDiskLatency(double diskQueueLength, double diskReadLatencyMs, double diskWriteLatencyMs)
    {
        try
        {
            var evidence = new List<string>
            {
                $"Disk queue length: {diskQueueLength:0.##}",
                $"Avg. read latency: {diskReadLatencyMs:0.#} ms",
                $"Avg. write latency: {diskWriteLatencyMs:0.#} ms",
            };

            bool queueHigh = diskQueueLength >= 4;
            bool latencyHigh = diskReadLatencyMs >= 25 || diskWriteLatencyMs >= 25;

            if (queueHigh || latencyHigh)
                return DiagnosticStepResult.Warn("The disk looks like a bottleneck right now (queue building up and/or elevated latency).", evidence);
            return DiagnosticStepResult.Pass("Disk queue length and latency look normal.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read disk counters: {ex.Message}");
        }
    }

    /// <summary>Memory\Pages Input/sec, sampled fresh over ~1 second - a sustained high page-in
    /// rate is the classic "memory thrashing" signature (RAM full, Windows constantly paging back
    /// in from disk). Not part of the shared HardwareMonitorService sampler, so this is the one
    /// check in this branch that genuinely samples something new rather than reusing live data.</summary>
    public static async Task<DiagnosticStepResult> CheckMemoryThrashingAsync(CancellationToken ct)
    {
        try
        {
            using var counter = new PerformanceCounter("Memory", "Pages Input/sec", readOnly: true);
            counter.NextValue(); // first call on a rate counter always returns 0 - needs a second sample
            await Task.Delay(1000, ct);
            float pagesInPerSec = counter.NextValue();

            var evidence = new List<string> { $"Pages Input/sec: {pagesInPerSec:0.#}" };
            // A rough rule of thumb, not an exact threshold - a sustained rate in the hundreds+
            // while nothing large is actively loading is the shape thrashing takes.
            if (pagesInPerSec >= 500)
                return DiagnosticStepResult.Warn($"High page-in rate ({pagesInPerSec:0} pages/sec) - a sign of memory pressure (thrashing).", evidence);
            return DiagnosticStepResult.Pass("Page-in rate looks normal - no sign of memory thrashing.", evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the Memory\\Pages Input/sec counter: {ex.Message}");
        }
    }

    private static readonly string[] BackgroundMaintenanceProcessNames = { "MsMpEng", "TiWorker", "SearchIndexer" };

    /// <summary>Presence of Windows Defender's scan engine, the servicing worker, and the search
    /// indexer - all legitimate, all capable of a real (if temporary) slowdown while active.
    /// Checks the already-sampled Processes list first (has live CPU/memory figures); falls back
    /// to a raw Process.GetProcessesByName lookup for a process that hasn't been sampled yet.</summary>
    public static DiagnosticStepResult CheckBackgroundMaintenanceProcesses(IReadOnlyList<ProcessRow> processes)
    {
        try
        {
            var found = new List<string>();
            foreach (var name in BackgroundMaintenanceProcessNames)
            {
                var row = processes.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    found.Add($"{row.Name}.exe (PID {row.Pid}): {row.CpuPercent10sAvg:0.#}% CPU, {row.MemoryMb:0} MB");
                    continue;
                }
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0) found.Add($"{name}.exe is running");
                    foreach (var p in procs) p.Dispose();
                }
                catch { /* best-effort */ }
            }

            if (found.Count == 0)
                return DiagnosticStepResult.Pass("Windows Defender scan, servicing worker, and search indexer are not currently running.");
            return DiagnosticStepResult.Warn("Background maintenance work is currently running - can cause a temporary slowdown on its own.", found);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check background maintenance processes: {ex.Message}");
        }
    }

    #endregion

    #region 903 - "It crashes or blue-screens"

    /// <summary>Shared state threaded through the crash branch's steps - the driver-install
    /// correlation (last step) needs the crash timestamps the first step found.</summary>
    public sealed class CrashContext
    {
        public List<DateTime> CrashTimes { get; } = new();
        public StabilitySnapshot? Snapshot { get; set; }
    }

    /// <summary>BugCheck 1001 (WER's own "a blue screen just happened" summary) and Kernel-Power 41
    /// (unclean shutdown), via EventLogService's existing System/Application scan - reused rather
    /// than re-querying, since it already reads exactly these event IDs.</summary>
    public static DiagnosticStepResult CheckCrashEvents(CrashContext ctx)
    {
        try
        {
            var snapshot = new EventLogService().Query();
            ctx.Snapshot = snapshot;

            var crashLikeIds = new HashSet<int> { 41, 6008, 1001 };
            var crashes = snapshot.RecentEvents
                .Where(e => crashLikeIds.Contains(e.EventId))
                .OrderByDescending(e => e.TimeCreated)
                .ToList();
            ctx.CrashTimes.AddRange(crashes.Select(c => c.TimeCreated));

            if (crashes.Count == 0)
                return DiagnosticStepResult.Pass("No BugCheck (1001) or Kernel-Power (41) crash events found in the last 30 days.");

            var evidence = crashes.Take(10)
                .Select(c => $"{c.TimeCreated:g} - {c.LogName} event {c.EventId}{(c.BugcheckCode is { } code ? $" ({code})" : "")}")
                .ToList();
            return DiagnosticStepResult.Warn($"{crashes.Count} crash-like event(s) found in the last 30 days. Most recent: {crashes[0].TimeCreated:g}.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't scan the System/Application logs: {ex.Message}");
        }
    }

    public static DiagnosticStepResult CheckMinidumps(CrashContext ctx)
    {
        try
        {
            var dumps = ctx.Snapshot?.Minidumps ?? new EventLogService().Query().Minidumps;
            if (dumps.Count == 0)
                return DiagnosticStepResult.Pass("No minidump files found under %SystemRoot%\\Minidump.");

            var evidence = dumps.Take(10)
                .Select(d => $"{d.FileName} - {d.Timestamp:g}{(d.BugcheckCode is { } c ? $" ({c})" : "")}")
                .ToList();
            return DiagnosticStepResult.Warn($"{dumps.Count} minidump file(s) found.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't enumerate the Minidump folder: {ex.Message}");
        }
    }

    /// <summary>Win32_ReliabilityRecords entries within a week of the last detected crash - the
    /// same data source Windows' own Reliability Monitor reads from, filtered client-side to a
    /// window around the crash (WMI's DMTF datetime format doesn't lend itself to a clean
    /// server-side range filter here).</summary>
    public static DiagnosticStepResult CheckReliabilityRecords(CrashContext ctx)
    {
        try
        {
            DateTime center = ctx.CrashTimes.Count > 0 ? ctx.CrashTimes.Max() : DateTime.Now;
            var records = new List<(DateTime Time, string Source, string Message)>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT SourceName, Message, TimeGenerated FROM Win32_ReliabilityRecords");
            foreach (ManagementObject mo in searcher.Get())
            {
                DateTime? time = null;
                if (mo["TimeGenerated"] is string wmiTime)
                {
                    try { time = ManagementDateTimeConverter.ToDateTime(wmiTime); } catch { /* leave null */ }
                }
                if (time is null || Math.Abs((time.Value - center).TotalDays) > 3.5) continue;

                records.Add((time.Value, (mo["SourceName"] as string ?? string.Empty).Trim(), (mo["Message"] as string ?? string.Empty).Trim()));
            }
            records = records.OrderByDescending(r => r.Time).ToList();

            if (records.Count == 0)
                return DiagnosticStepResult.Pass("No Reliability Monitor records found in the week surrounding the last crash.");

            var evidence = records.Take(15).Select(r => $"{r.Time:g} - {r.Source}: {Truncate(r.Message, 140)}").ToList();
            return DiagnosticStepResult.Warn($"{records.Count} Reliability Monitor record(s) found within a week of the last crash.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read Win32_ReliabilityRecords: {ex.Message}");
        }
    }

    /// <summary>WHEA-Logger 17/18/19 - Windows Hardware Error Architecture events, a signal of
    /// actual hardware (RAM/CPU/bus) involvement rather than a purely software crash.</summary>
    public static DiagnosticStepResult CheckWheaEvents()
    {
        var events = ReadProviderEvents("System", "Microsoft-Windows-WHEA-Logger", new[] { 17, 18, 19 }, DefaultLookbackDays);
        if (events.Count == 0)
            return DiagnosticStepResult.Pass($"No WHEA-Logger hardware-error events (17/18/19) in the last {DefaultLookbackDays} days.");

        var evidence = events.Take(10).Select(e => $"{e.TimeCreated:g} - event {e.EventId}: {Truncate(e.Message, 140)}").ToList();
        return DiagnosticStepResult.Warn(
            $"{events.Count} hardware-error event(s) logged by WHEA-Logger in the last {DefaultLookbackDays} days - possible hardware involvement.", evidence);
    }

    internal sealed record PnpDriverInfo(string PublishedName, string Provider, DateTime? Date);

    /// <summary>Correlates drivers installed in the 7 days before the first detected crash, via
    /// `pnputil /enum-drivers` (the same "known tool, parse its text output" tradeoff every other
    /// shelled-out check in this app takes). Explicitly presented as a lead, not a verdict - a
    /// driver installed shortly before a crash streak is a plausible suspect, not a proven cause.</summary>
    public static async Task<DiagnosticStepResult> CheckRecentDriverCorrelationAsync(CrashContext ctx)
    {
        try
        {
            if (ctx.CrashTimes.Count == 0)
                return DiagnosticStepResult.Skip("No crash events were found earlier in this run, so there's nothing to correlate driver installs against.");

            var (output, exitCode) = await RunCapturedAsync("pnputil.exe", "/enum-drivers");
            if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
                return DiagnosticStepResult.Skip("Couldn't enumerate installed drivers via pnputil.");

            var drivers = ParsePnpUtilDrivers(output);
            DateTime firstCrash = ctx.CrashTimes.Min();

            var candidates = drivers
                .Where(d => d.Date is { } dt && dt <= firstCrash.AddDays(1) && dt >= firstCrash.AddDays(-7))
                .OrderByDescending(d => d.Date)
                .ToList();

            if (candidates.Count == 0)
                return DiagnosticStepResult.Pass("No drivers were installed in the 7 days before the first detected crash.");

            var evidence = candidates.Select(d => $"{d.Provider} ({d.PublishedName}) - installed {d.Date:d}").ToList();
            var top = candidates[0];
            return DiagnosticStepResult.Warn(
                $"Lead, not a confirmed cause: crashes cluster around \"{top.Provider}\" installed on {top.Date:d}. " +
                $"{candidates.Count} driver(s) total were installed within the week before the first crash.",
                evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check recently-installed drivers: {ex.Message}");
        }
    }

    internal static List<PnpDriverInfo> ParsePnpUtilDrivers(string output)
    {
        var drivers = new List<PnpDriverInfo>();
        foreach (var block in Regex.Split(output.Replace("\r\n", "\n"), "\n\n+"))
        {
            string published = ExtractField(block, @"Published [Nn]ame\s*:\s*(.+)");
            if (published.Length == 0) continue;
            string provider = ExtractField(block, @"Driver package provider\s*:\s*(.+)");
            string dateVersion = ExtractField(block, @"Driver date and version\s*:\s*(.+)");

            DateTime? date = null;
            var dateMatch = Regex.Match(dateVersion, @"(\d{1,2}/\d{1,2}/\d{4})");
            if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups[1].Value, out var parsed))
                date = parsed;

            drivers.Add(new PnpDriverInfo(published.Trim(), provider.Length > 0 ? provider.Trim() : "(unknown provider)", date));
        }
        return drivers;

        static string ExtractField(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        }
    }

    #endregion

    #region 904 - "It won't sleep / it wakes on its own"

    /// <summary>`powercfg /requests` - lists apps/drivers currently holding a power request open
    /// (any of which can prevent sleep). Parses the tool's section-per-category text output; a
    /// section reporting "None." contributes nothing.</summary>
    public static async Task<DiagnosticStepResult> CheckPowerRequestsAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/requests");
            if (exitCode is null) return DiagnosticStepResult.Skip("powercfg /requests timed out.");
            if (string.IsNullOrWhiteSpace(output)) return DiagnosticStepResult.Skip("No output from powercfg /requests.");

            var active = ParsePowerRequestSections(output);
            if (active.Count == 0)
                return DiagnosticStepResult.Pass("No app or driver is currently holding a power request open (all sections report None).");
            return DiagnosticStepResult.Warn($"{active.Count} active power request(s) found - one of these can prevent sleep.", active);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run powercfg /requests: {ex.Message}");
        }
    }

    private static List<string> ParsePowerRequestSections(string output)
    {
        var result = new List<string>();
        string? currentSection = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':') && !line.StartsWith('['))
            {
                currentSection = line.TrimEnd(':');
                continue;
            }
            if (line.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;
            if (currentSection is not null)
                result.Add($"{currentSection}: {line}");
        }
        return result;
    }

    /// <summary>`powercfg /waketimers` - active wake timers, any of which can wake the PC on its
    /// own outside of user interaction.</summary>
    public static async Task<DiagnosticStepResult> CheckWakeTimersAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/waketimers");
            if (exitCode is null) return DiagnosticStepResult.Skip("powercfg /waketimers timed out.");

            string trimmed = output.Trim();
            if (trimmed.Length == 0 || trimmed.Contains("no active wake timers", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("There are no wake timers", StringComparison.OrdinalIgnoreCase))
                return DiagnosticStepResult.Pass("No active wake timers.");

            var lines = trimmed.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            return DiagnosticStepResult.Warn("Active wake timer(s) found - one of these can wake the PC on its own.", lines);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run powercfg /waketimers: {ex.Message}");
        }
    }

    /// <summary>`powercfg /lastwake` - what woke the system last (a device, a timer, or unknown).</summary>
    public static async Task<DiagnosticStepResult> CheckLastWakeAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/lastwake");
            if (exitCode is null) return DiagnosticStepResult.Skip("powercfg /lastwake timed out.");

            string trimmed = output.Trim();
            if (trimmed.Length == 0) return DiagnosticStepResult.Skip("No output from powercfg /lastwake.");

            var lines = trimmed.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            bool unknown = trimmed.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
            string summary = unknown
                ? "The last wake source couldn't be identified by Windows itself."
                : "Last wake source recorded - see evidence.";
            return unknown ? DiagnosticStepResult.Warn(summary, lines) : DiagnosticStepResult.Pass(summary, lines);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run powercfg /lastwake: {ex.Message}");
        }
    }

    /// <summary>`powercfg /sleepstudy /output &lt;file&gt;.html` - Modern Standby's own diagnostic
    /// report. The report is a large, version-varying, mostly JS-rendered document; this strips
    /// tags and looks for a recognizable "process/driver name near a percentage" shape rather than
    /// attempting a full structural parse. When nothing recognizable is found, this degrades to
    /// pointing at the generated file rather than guessing an offender.</summary>
    public static async Task<DiagnosticStepResult> CheckSleepStudyAsync()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tmp-sleepstudy-{Guid.NewGuid():N}.html");
        try
        {
            var (_, exitCode) = await RunCapturedAsync("powercfg.exe", $"/sleepstudy /output \"{tempFile}\"", timeoutMs: 30000);
            if (exitCode != 0 || !File.Exists(tempFile))
                return DiagnosticStepResult.Skip("Sleep study report unavailable - this system may not support Modern Standby (sleepstudy only applies there).");

            string html = await File.ReadAllTextAsync(tempFile);
            string text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));

            var offenders = Regex.Matches(text, @"([A-Za-z0-9_.\-]{3,60}\.(?:exe|sys|dll))\D{0,40}?(\d{1,3}(?:\.\d+)?)\s*%")
                .Select(m => $"{m.Groups[1].Value}: {m.Groups[2].Value}%")
                .Distinct()
                .Take(8)
                .ToList();

            if (offenders.Count == 0)
                return DiagnosticStepResult.Warn(
                    "Sleep study report generated but no specific offender could be confidently extracted from it - open the file directly for the full report.",
                    new[] { tempFile });

            offenders.Add($"Full report: {tempFile}");
            return DiagnosticStepResult.Warn(
                "Sleep study report generated - possible DRIPS offenders / modern-standby exit sources found. This is a best-effort text extraction; open the file for the full report.",
                offenders);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run/parse powercfg /sleepstudy: {ex.Message}");
        }
    }

    #endregion

    #region 905 - "No internet" (layered, stop at first failure)

    private static string? FindGatewayForDisplay()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var gw = ni.GetIPProperties().GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw is not null) return gw.Address.ToString();
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>Layer 1: does any adapter actually report a Connected link state
    /// (Win32_NetworkAdapter.NetConnectionStatus == 2)?</summary>
    public static DiagnosticStepResult CheckAdapterLinkState()
    {
        try
        {
            var evidence = new List<string>();
            bool anyConnected = false;
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionStatus IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                int status = mo["NetConnectionStatus"] is { } raw ? Convert.ToInt32(raw) : -1;
                string name = (mo["Name"] as string ?? "Unknown adapter").Trim();
                evidence.Add($"{name}: {DescribeNetConnectionStatus(status)}");
                if (status == 2) anyConnected = true;
            }

            if (!anyConnected)
                return DiagnosticStepResult.Fail("No network adapter reports a Connected state. Re-check cables/Wi-Fi, or run: Get-NetAdapter", evidence);
            return DiagnosticStepResult.Pass("At least one network adapter is connected.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read adapter link state: {ex.Message}");
        }
    }

    private static string DescribeNetConnectionStatus(int status) => status switch
    {
        0 => "Disconnected",
        1 => "Connecting",
        2 => "Connected",
        3 => "Disconnecting",
        4 => "Hardware not present",
        5 => "Hardware disabled",
        6 => "Hardware malfunction",
        7 => "Media disconnected",
        8 => "Authenticating",
        9 => "Authentication succeeded",
        10 => "Authentication failed",
        11 => "Invalid address",
        12 => "Credentials required",
        _ => $"Unknown ({status})",
    };

    /// <summary>Layer 2: does the adapter have a real DHCP-assigned address, or only an APIPA
    /// (169.254.x.x) self-assigned fallback address (the classic "DHCP didn't answer" signature)?</summary>
    public static DiagnosticStepResult CheckApipaAddress()
    {
        try
        {
            var addresses = new List<string>();
            bool anyApipa = false, anyRealAddress = false;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    addresses.Add($"{ni.Name}: {ip}");
                    if (ip.StartsWith("169.254.")) anyApipa = true;
                    else anyRealAddress = true;
                }
            }

            if (addresses.Count == 0)
                return DiagnosticStepResult.Fail("No active adapter has an IPv4 address at all. Run: ipconfig /all");
            if (anyApipa && !anyRealAddress)
                return DiagnosticStepResult.Fail("Adapter has an APIPA (169.254.x.x) address - DHCP didn't hand out a real address. Run: ipconfig /all", addresses);
            return DiagnosticStepResult.Pass("A real (non-APIPA) IPv4 address is assigned.", addresses);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read adapter IP addresses: {ex.Message}");
        }
    }

    public sealed class NetworkLayerContext
    {
        public ConnectivityResult? Connectivity { get; set; }
    }

    /// <summary>Layer 3: default gateway ping, via the existing NetworkDiagnosticsService check
    /// (reused so this branch's later DNS step doesn't have to ping a second time).</summary>
    public static async Task<DiagnosticStepResult> CheckGatewayAsync(NetworkLayerContext ctx)
    {
        try
        {
            ctx.Connectivity ??= await new NetworkDiagnosticsService().CheckAsync();
            var c = ctx.Connectivity;
            string? gateway = FindGatewayForDisplay();
            string gatewayLabel = gateway is null ? string.Empty : $" ({gateway})";

            if (c.GatewayReachable == true)
                return DiagnosticStepResult.Pass($"Gateway{gatewayLabel} responded to ping in {c.GatewayRoundtripMs} ms.");
            if (c.GatewayReachable is null)
                return DiagnosticStepResult.Skip("No default gateway is configured on this system - nothing to ping.");
            return DiagnosticStepResult.Fail($"Gateway{gatewayLabel} didn't respond to ping. Run: ping {gateway ?? "<gateway>"}");
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check gateway connectivity: {ex.Message}");
        }
    }

    /// <summary>Layer 4: does an actual hostname resolve? (distinct from the ICMP ping to a
    /// resolver IP the gateway/DNS-reachability check above uses - this exercises real DNS.)</summary>
    public static async Task<DiagnosticStepResult> CheckDnsAsync(NetworkLayerContext ctx)
    {
        try
        {
            ctx.Connectivity ??= await new NetworkDiagnosticsService().CheckAsync();
            var c = ctx.Connectivity;
            if (c.DnsLookupMs is { } ms)
                return DiagnosticStepResult.Pass($"DNS resolved www.msftconnecttest.com in {ms} ms.");
            return DiagnosticStepResult.Fail("DNS resolution failed or timed out. Run: nslookup www.msftconnecttest.com");
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check DNS resolution: {ex.Message}");
        }
    }

    /// <summary>Layer 5: an actual outbound TCP connect (distinct from an ICMP ping, which many
    /// networks block outright even when TCP is fine).</summary>
    public static async Task<DiagnosticStepResult> CheckOutboundTcpAsync(CancellationToken ct)
    {
        const string host = "1.1.1.1";
        const int port = 443;
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var winner = await Task.WhenAny(connectTask, Task.Delay(4000, ct));
            if (winner != connectTask || !client.Connected)
                return DiagnosticStepResult.Fail($"Couldn't open an outbound TCP connection to {host}:{port}. Run: Test-NetConnection {host} -Port {port}");
            return DiagnosticStepResult.Pass($"Outbound TCP connection to {host}:{port} succeeded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Fail($"Outbound TCP connect failed: {ex.Message}. Run: Test-NetConnection {host} -Port {port}");
        }
    }

    #endregion

    #region 906 - "Fans are loud / it runs hot"

    /// <summary>Correlates fan RPM / temperature sensors against current CPU load - "hot because
    /// busy" (load and heat both up) vs. "hot without load" (heat/fans up while load is low, a
    /// dust/airflow/fan-curve/pump smell instead). Opens its own SensorMonitorService instance
    /// (on-demand, disposed immediately after) rather than sharing EnergyThermalsViewModel's -
    /// TroubleshootViewModel only takes Performance/Processes references per its constructor.</summary>
    public static DiagnosticStepResult CheckFanTempCorrelation(double cpuLoadPercent)
    {
        SensorMonitorService? sensors = null;
        try
        {
            sensors = new SensorMonitorService();
            if (!sensors.IsAvailable)
                return DiagnosticStepResult.Skip("Sensor readings aren't available on this system.");

            var readings = sensors.Sample();
            var fans = readings.Where(r => r.Type == LibreHardwareMonitor.Hardware.SensorType.Fan && r.Value.HasValue).ToList();
            var temps = readings.Where(r => r.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature && r.Value.HasValue).ToList();

            if (fans.Count == 0 && temps.Count == 0)
                return DiagnosticStepResult.Skip("No fan or temperature sensors were found to read.");

            var evidence = new List<string> { $"CPU load: {cpuLoadPercent:0.#}%" };
            evidence.AddRange(fans.Select(f => $"{f.HardwareName} / {f.SensorName}: {f.Value ?? 0:0} RPM"));
            evidence.AddRange(temps.Take(6).Select(t => $"{t.HardwareName} / {t.SensorName}: {t.Value ?? 0:0.#} °C"));

            double maxFanRpm = fans.Count == 0 ? 0 : fans.Max(f => f.Value ?? 0);
            double maxTemp = temps.Count == 0 ? 0 : temps.Max(t => t.Value ?? 0);
            bool fansOrTempHigh = maxFanRpm >= 2500 || maxTemp >= 80;
            bool loadHigh = cpuLoadPercent >= 60;

            if (!fansOrTempHigh)
                return DiagnosticStepResult.Pass("Fan speeds and temperatures look normal right now.", evidence);

            string summary = loadHigh
                ? "Fans/temperatures are elevated, and CPU load is also high right now - reads as \"hot because busy\"."
                : "Fans/temperatures are elevated while CPU load is currently low - reads as \"hot without load\" (worth checking dust/airflow, a stuck fan curve, or a process not reflected in this instant's load).";
            return DiagnosticStepResult.Warn(summary, evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read sensors: {ex.Message}");
        }
        finally
        {
            sensors?.Dispose();
        }
    }

    /// <summary>`powercfg /query` for the active plan's minimum processor state - a plan stuck with
    /// a high minimum keeps the CPU (and fans) from ever idling down.</summary>
    public static async Task<DiagnosticStepResult> CheckMinProcessorStateAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN");
            if (exitCode is null) return DiagnosticStepResult.Skip("powercfg /query timed out.");

            var acMatch = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            var dcMatch = Regex.Match(output, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            int? ac = acMatch.Success ? Convert.ToInt32(acMatch.Groups[1].Value, 16) : null;
            int? dc = dcMatch.Success ? Convert.ToInt32(dcMatch.Groups[1].Value, 16) : null;

            if (ac is null && dc is null)
                return DiagnosticStepResult.Skip("Couldn't determine the minimum processor state from powercfg /query.");

            var evidence = new List<string>
            {
                ac is { } a ? $"Plugged in (AC): {a}%" : "Plugged in (AC): unknown",
                dc is { } d ? $"On battery (DC): {d}%" : "On battery (DC): unknown",
            };

            bool stuckHigh = (ac ?? 0) >= 80 || (dc ?? 0) >= 80;
            return stuckHigh
                ? DiagnosticStepResult.Warn("Minimum processor state is set high - the CPU may be prevented from idling down, keeping it (and the fans) running hotter than necessary.", evidence)
                : DiagnosticStepResult.Pass("Minimum processor state looks normal.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the minimum processor state: {ex.Message}");
        }
    }

    /// <summary>A CPU whose clock speed is still running well above base while reported load is
    /// low is a "stuck at a high performance state" signature, distinct from genuinely being busy.</summary>
    public static DiagnosticStepResult CheckStuckHighPerformanceState(double cpuLoadPercent, double cpuVsBasePercent)
    {
        try
        {
            var evidence = new List<string>
            {
                $"CPU load: {cpuLoadPercent:0.#}%",
                $"Current clock vs. base clock: {cpuVsBasePercent:+0.#;-0.#;0}%",
            };

            bool looksIdle = cpuLoadPercent < 15;
            bool clockStuckHigh = cpuVsBasePercent >= 25;

            if (looksIdle && clockStuckHigh)
                return DiagnosticStepResult.Warn("CPU load is low but the clock speed is still running well above base - it may be stuck at a high performance state instead of idling down.", evidence);
            return DiagnosticStepResult.Pass("CPU clock speed tracks load normally.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read CPU clock data: {ex.Message}");
        }
    }

    /// <summary>A single process using close to a full logical core's worth of sustained CPU is a
    /// plausible "pinning a core" contributor to fan noise/heat, even when total system CPU% looks
    /// moderate on a many-core machine.</summary>
    public static DiagnosticStepResult CheckCorePinningProcess(IReadOnlyList<ProcessRow> processes)
    {
        try
        {
            int logical = Math.Max(1, Environment.ProcessorCount);
            double singleCoreThreshold = 100.0 / logical * 0.85;

            var candidate = processes
                .Where(p => p.CpuPercent10sAvg >= singleCoreThreshold)
                .OrderByDescending(p => p.CpuPercent10sAvg)
                .FirstOrDefault();

            if (candidate is null)
                return DiagnosticStepResult.Pass("No single process looks like it's pinning a core right now.");

            var evidence = new List<string>
            {
                $"{candidate.Name} (PID {candidate.Pid}): {candidate.CpuPercent10sAvg:0.#}% CPU (10s avg) - roughly one core's worth on this {logical}-logical-processor system.",
            };
            return DiagnosticStepResult.Warn($"{candidate.Name} (PID {candidate.Pid}) is using close to a full core sustained - a likely contributor to fan noise/heat.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the process list: {ex.Message}");
        }
    }

    #endregion

    #region 907/908 - boot & shutdown degradation (shared Diagnostics-Performance parsing)

    private const string DiagnosticsPerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public sealed record BootCulprit(string Name, double Milliseconds, DateTime TimeCreated);

    public sealed class BootShutdownContext
    {
        public List<RawEvent> BootEvents { get; set; } = new();
        public List<BootCulprit> BootCulprits { get; set; } = new();
        public List<RawEvent> ShutdownEvents { get; set; } = new();
        public List<BootCulprit> ShutdownCulprits { get; set; } = new();
    }

    /// <summary>
    /// Best-effort "named culprit + measured delay" extraction from a Diagnostics-Performance
    /// event's own (localized) message text - not a documented, versioned property layout (the
    /// same caveat EventLogService.ExtractBugcheckCode already carries for a different event), so
    /// a message that doesn't match this shape simply contributes no named culprit; the raw
    /// message is still shown as evidence either way.
    /// </summary>
    private static readonly Regex CulpritPattern = new(
        @"(?<name>[A-Za-z0-9_.\\ \-]{3,80}?)\s+(?:took|caused a delay of|consumed|delayed (?:the )?(?:boot|logon|startup|shutdown) by)\s+(?<ms>[\d,]+)\s*m(?:illi)?s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<BootCulprit> ExtractCulprits(List<RawEvent> events)
    {
        var culprits = new List<BootCulprit>();
        foreach (var ev in events)
        {
            var m = CulpritPattern.Match(ev.Message);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups["ms"].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms)) continue;
            culprits.Add(new BootCulprit(m.Groups["name"].Value.Trim(), ms, ev.TimeCreated));
        }
        return culprits;
    }

    #endregion

    #region 907 - "It boots or signs in slowly"

    /// <summary>Microsoft-Windows-Diagnostics-Performance/Operational events 100-110 - boot
    /// degradation, slow services, slow startup apps, slow group policy, slow profile load.</summary>
    public static DiagnosticStepResult CheckBootDegradationEvents(BootShutdownContext ctx)
    {
        var events = ReadProviderEvents(DiagnosticsPerformanceLog, null, Enumerable.Range(100, 11).ToArray(), BootShutdownLookbackDays, maxEvents: 50);
        ctx.BootEvents = events;
        ctx.BootCulprits = ExtractCulprits(events);

        if (events.Count == 0)
            return DiagnosticStepResult.Pass($"No boot-performance degradation events (100-110) found in the last {BootShutdownLookbackDays} days, or this channel isn't enabled on this Windows build.");

        var evidence = events.Take(10).Select(e => $"{e.TimeCreated:g} - event {e.EventId}: {Truncate(e.Message, 160)}").ToList();
        string summary = ctx.BootCulprits.Count > 0
            ? $"{events.Count} boot-degradation event(s) found - {ctx.BootCulprits.Count} named item(s) with a measured delay (see next step)."
            : $"{events.Count} boot-degradation event(s) found, but no specific named delay could be confidently extracted from the event text.";
        return DiagnosticStepResult.Warn(summary, evidence);
    }

    /// <summary>Joins the named culprits from the previous step to the Startup tab's items and the
    /// Services tab's rows by a simple substring match on name, and reports the total measured
    /// delay from the events' own millisecond figures - "these N items cost you M ms at last boot",
    /// not an estimate.</summary>
    public static async Task<DiagnosticStepResult> CheckBootCulpritJoinAsync(BootShutdownContext ctx)
    {
        try
        {
            if (ctx.BootCulprits.Count == 0)
                return DiagnosticStepResult.Skip("No named culprits with a measured delay were extracted from the boot-degradation events.");

            var startupItems = await Task.Run(() => new StartupManagerService().Sample());
            var services = await Task.Run(() => new ServiceControlService().Sample());

            var ranked = ctx.BootCulprits.OrderByDescending(c => c.Milliseconds).Take(3).ToList();
            double totalMs = ranked.Sum(c => c.Milliseconds);

            var evidence = new List<string>();
            foreach (var c in ranked)
            {
                var startupMatch = startupItems.FirstOrDefault(s => NamesCorrelate(c.Name, s.Name));
                var serviceMatch = services.FirstOrDefault(s => NamesCorrelate(c.Name, s.DisplayName) || NamesCorrelate(c.Name, s.ServiceName));
                string joinNote = startupMatch is not null ? $" - matches Startup item \"{startupMatch.Name}\""
                    : serviceMatch is not null ? $" - matches Services item \"{serviceMatch.DisplayName}\""
                    : string.Empty;
                evidence.Add($"{c.Name}: {c.Milliseconds:0} ms ({c.TimeCreated:g}){joinNote}");
            }

            return DiagnosticStepResult.Warn($"These {ranked.Count} item(s) cost {totalMs:0} ms at last boot, per the event log's own reported figures.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't join boot culprits to Startup/Services: {ex.Message}");
        }
    }

    private static bool NamesCorrelate(string culpritName, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(culpritName) || string.IsNullOrWhiteSpace(candidateName)) return false;
        return culpritName.Contains(candidateName, StringComparison.OrdinalIgnoreCase) ||
               candidateName.Contains(culpritName, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 908 - "It takes forever to shut down"

    /// <summary>Diagnostics-Performance shutdown-degradation events 200-208.</summary>
    public static DiagnosticStepResult CheckShutdownDegradationEvents(BootShutdownContext ctx)
    {
        var events = ReadProviderEvents(DiagnosticsPerformanceLog, null, Enumerable.Range(200, 9).ToArray(), BootShutdownLookbackDays, maxEvents: 50);
        ctx.ShutdownEvents = events;
        ctx.ShutdownCulprits = ExtractCulprits(events);

        if (events.Count == 0)
            return DiagnosticStepResult.Pass($"No shutdown-degradation events (200-208) found in the last {BootShutdownLookbackDays} days.");

        var evidence = events.Take(10).Select(e => $"{e.TimeCreated:g} - event {e.EventId}: {Truncate(e.Message, 160)}").ToList();
        return DiagnosticStepResult.Warn($"{events.Count} shutdown-degradation event(s) found.", evidence);
    }

    /// <summary>User Profile Service 1530 ("a user is logged on with a temporary profile"-adjacent
    /// registry-handle-leak warning) / 1534 events - a process holding registry handles open at
    /// logoff, a known cause of a slow sign-out/shutdown. Filtered by event ID only on the
    /// Application log (the provider's display name varies slightly by Windows build - "User
    /// Profile Service" vs. "Microsoft-Windows-User Profiles Service" - so this doesn't risk
    /// missing events over a provider-name mismatch).</summary>
    public static DiagnosticStepResult CheckProfileUnloadEvents()
    {
        var events = ReadProviderEvents("Application", null, new[] { 1530, 1534 }, BootShutdownLookbackDays, maxEvents: 30);
        if (events.Count == 0)
            return DiagnosticStepResult.Pass($"No User Profile Service events (1530/1534) found in the last {BootShutdownLookbackDays} days.");

        var evidence = events.Take(10).Select(e => $"{e.TimeCreated:g} - event {e.EventId}: {Truncate(e.Message, 160)}").ToList();
        return DiagnosticStepResult.Warn($"{events.Count} profile-unload event(s) found - a process left registry handles open at logoff, which can delay sign-out/shutdown.", evidence);
    }

    public static DiagnosticStepResult CheckWaitToKillServiceTimeout()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            var raw = key?.GetValue("WaitToKillServiceTimeout");
            if (raw is null)
                return DiagnosticStepResult.Pass("WaitToKillServiceTimeout isn't set in the registry - Windows' built-in default applies.");

            string text = raw.ToString() ?? string.Empty;
            return DiagnosticStepResult.Pass(
                $"WaitToKillServiceTimeout is set to {text} ms - how long Windows waits for a hung service before forcing it closed at shutdown.",
                new[] { $"HKLM\\SYSTEM\\CurrentControlSet\\Control\\WaitToKillServiceTimeout = {text}" });
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read WaitToKillServiceTimeout: {ex.Message}");
        }
    }

    /// <summary>Reports which named item blocked shutdown the longest, from the same
    /// shutdown-degradation events the first step of this branch already parsed.</summary>
    public static DiagnosticStepResult SummarizeShutdownCulprits(BootShutdownContext ctx)
    {
        if (ctx.ShutdownCulprits.Count == 0)
            return DiagnosticStepResult.Skip("No named culprits with a measured duration were extracted from the shutdown-degradation events.");

        var ranked = ctx.ShutdownCulprits.OrderByDescending(c => c.Milliseconds).Take(3).ToList();
        var evidence = ranked.Select(c => $"{c.Name}: {c.Milliseconds:0} ms ({c.TimeCreated:g})").ToList();
        var top = ranked[0];
        return DiagnosticStepResult.Warn($"\"{top.Name}\" blocked shutdown the longest at last measurement: {top.Milliseconds:0} ms.", evidence);
    }

    #endregion

    #region 909 - "Games or video stutter"

    /// <summary>Processor Information\% DPC Time / % Interrupt Time (the "_Total" instance) plus
    /// System\Context Switches/sec, sampled fresh over ~1 second the same "prime, then read" way
    /// CheckMemoryThrashingAsync already does for a rate counter. A driver monopolizing interrupt
    /// time is exactly the "smooth everywhere except stutters/audio crackles" signature - CPU%
    /// alone can look totally normal while this is happening, which is why this doesn't just reuse
    /// the shared performance sampler's CPU history.</summary>
    public static async Task<DiagnosticStepResult> CheckDpcInterruptTimeAsync(CancellationToken ct)
    {
        PerformanceCounter? dpcCounter = null, interruptCounter = null, contextSwitchCounter = null;
        try
        {
            try { dpcCounter = new PerformanceCounter("Processor Information", "% DPC Time", "_Total", readOnly: true); }
            catch { /* category/instance not present on this system - reflected in evidence below */ }
            try { interruptCounter = new PerformanceCounter("Processor Information", "% Interrupt Time", "_Total", readOnly: true); }
            catch { /* as above */ }
            try { contextSwitchCounter = new PerformanceCounter("System", "Context Switches/sec", readOnly: true); }
            catch { /* as above */ }

            if (dpcCounter is null && interruptCounter is null && contextSwitchCounter is null)
                return DiagnosticStepResult.Skip("None of the DPC/interrupt/context-switch performance counters are available on this system.");

            dpcCounter?.NextValue();
            interruptCounter?.NextValue();
            contextSwitchCounter?.NextValue();
            await Task.Delay(1000, ct);
            float dpc = dpcCounter?.NextValue() ?? 0;
            float interrupt = interruptCounter?.NextValue() ?? 0;
            float contextSwitches = contextSwitchCounter?.NextValue() ?? 0;

            var evidence = new List<string>
            {
                dpcCounter is not null ? $"% DPC Time (all cores): {dpc:0.##}%" : "% DPC Time: unavailable",
                interruptCounter is not null ? $"% Interrupt Time (all cores): {interrupt:0.##}%" : "% Interrupt Time: unavailable",
                contextSwitchCounter is not null ? $"Context Switches/sec: {contextSwitches:0}" : "Context Switches/sec: unavailable",
            };

            // Rough rules of thumb, not documented Microsoft thresholds - outside a heavily
            // loaded system, DPC/ISR time is normally well under 1%, so a sustained few percent
            // is already a strong "a driver is doing too much work in interrupt context" signal.
            bool dpcHigh = dpc >= 3;
            bool interruptHigh = interrupt >= 2;
            bool contextSwitchesHigh = contextSwitches >= 15000;

            if (dpcHigh || interruptHigh)
                return DiagnosticStepResult.Warn(
                    "DPC/interrupt time is elevated - a driver may be monopolizing interrupt time, which shows up as stutter/audio crackle even while overall CPU% looks normal. Use the capture below to narrow down which driver.",
                    evidence);
            if (contextSwitchesHigh)
                return DiagnosticStepResult.Warn("Context switch rate is unusually high - worth investigating alongside the DPC/interrupt capture below.", evidence);
            return DiagnosticStepResult.Pass("DPC/interrupt time and context-switch rate look normal over this sample window.", evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read DPC/interrupt counters: {ex.Message}");
        }
        finally
        {
            dpcCounter?.Dispose();
            interruptCounter?.Dispose();
            contextSwitchCounter?.Dispose();
        }
    }

    /// <summary>#909: the heavy, opt-in half of this branch - a short `wpr -start DiagEasy` /
    /// `wpr -stop` capture, then a best-effort `tracerpt` summary. This is deliberately not part of
    /// the automatic step sequence (see <see cref="DiagnosticStep.IsManual"/>) - even a short
    /// capture is a real system-wide ETW trace, and wpr only allows one active trace session at a
    /// time, so it's opt-in via its own button rather than something every "Games stutter" run
    /// kicks off unasked.
    ///
    /// Caveat, stated plainly in the result text: `tracerpt` alone (no symbols, no Windows
    /// Performance Analyzer) can't reliably resolve a raw DPC/ISR routine address back to a driver
    /// module name - that resolution is what WPA's own analysis views do. What this *can* do
    /// without WPA is summarize which kernel/interrupt-related ETW providers logged the most events
    /// during the capture window, via tracerpt's own -summary report - a useful "where to look
    /// next" pointer, not a definitive per-driver DPC-time ranking.</summary>
    public static async Task<DiagnosticStepResult> CaptureDpcOffenderAsync(CancellationToken ct)
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string wpr = Path.Combine(system32, "wpr.exe");
        string tracerpt = Path.Combine(system32, "tracerpt.exe");
        if (!File.Exists(wpr) || !File.Exists(tracerpt))
            return DiagnosticStepResult.Skip("wpr.exe and/or tracerpt.exe aren't available on this system - can't run the capture.");

        string etlPath = Path.Combine(Path.GetTempPath(), $"tmp-dpc-capture-{Guid.NewGuid():N}.etl");
        string summaryPath = Path.Combine(Path.GetTempPath(), $"tmp-dpc-summary-{Guid.NewGuid():N}.txt");
        string csvPath = Path.Combine(Path.GetTempPath(), $"tmp-dpc-events-{Guid.NewGuid():N}.csv");
        try
        {
            var (startOutput, startExit) = await RunCapturedAsync(wpr, "-start DiagEasy -filemode", timeoutMs: 20000);
            if (startExit != 0)
                return DiagnosticStepResult.Skip($"Couldn't start the wpr capture: {Truncate(startOutput.Trim(), 300)}");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
            catch (OperationCanceledException)
            {
                // Don't leave an active trace session behind for a canceled/timed-out capture -
                // wpr only allows one at a time, and an abandoned session would block every
                // future capture until manually stopped.
                try { await RunCapturedAsync(wpr, "-cancel", timeoutMs: 15000); } catch { /* best-effort */ }
                throw;
            }

            var (stopOutput, stopExit) = await RunCapturedAsync(wpr, $"-stop \"{etlPath}\"", timeoutMs: 60000);
            if (stopExit != 0 || !File.Exists(etlPath))
                return DiagnosticStepResult.Skip($"Couldn't stop/save the wpr capture: {Truncate(stopOutput.Trim(), 300)}");

            var (_, tracerptExit) = await RunCapturedAsync(tracerpt,
                $"\"{etlPath}\" -o \"{csvPath}\" -summary \"{summaryPath}\" -quiet", timeoutMs: 60000);
            if (tracerptExit != 0 || !File.Exists(summaryPath))
                return DiagnosticStepResult.Warn("Capture saved, but tracerpt couldn't summarize it.", new[] { $"Trace file: {etlPath}" });

            string summary = await File.ReadAllTextAsync(summaryPath, ct);
            var providerLines = Regex.Matches(summary, @"^\s*(Microsoft-Windows-[\w-]+|PerfInfo|[\w.\\-]+)\s*:\s*(\d+)\s*$", RegexOptions.Multiline)
                .Select(m => (Name: m.Groups[1].Value.Trim(), Count: int.Parse(m.Groups[2].Value)))
                .Where(p => p.Count > 0)
                .OrderByDescending(p => p.Count)
                .Take(8)
                .ToList();

            var evidence = new List<string> { $"Trace file: {etlPath}", $"Summary report: {summaryPath}" };
            if (providerLines.Count == 0)
            {
                return DiagnosticStepResult.Warn(
                    "Capture completed, but no specific provider/module could be confidently extracted from tracerpt's summary. tracerpt alone can't resolve DPC routines to driver names without symbols - open the trace in Windows Performance Analyzer for a real per-driver DPC breakdown.",
                    evidence);
            }

            evidence.InsertRange(0, providerLines.Select(p => $"{p.Name}: {p.Count} events during the capture"));
            var top = providerLines[0];
            return DiagnosticStepResult.Warn(
                $"Best-effort lead, not a confirmed per-driver ranking: \"{top.Name}\" logged the most events during the capture window ({top.Count}). tracerpt can't resolve DPC/ISR routines to a driver module name without symbols - open the trace in Windows Performance Analyzer for the definitive per-driver DPC time breakdown.",
                evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"DPC capture failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(csvPath)) File.Delete(csvPath); } catch { /* best-effort cleanup */ }
        }
    }

    #endregion

    #region 910 - "Wi-Fi keeps dropping"

    public sealed class WifiContext
    {
        public bool HasWirelessAdapter { get; set; }
    }

    /// <summary>Whether this machine has a wireless network adapter at all, via
    /// `netsh wlan show interfaces` (the same tool the rest of this branch already shells out to) -
    /// its own "There is no wireless interface on the system." text is the simplest reliable
    /// signal, rather than guessing from WMI adapter names/types.</summary>
    public static async Task<DiagnosticStepResult> CheckWirelessAdapterPresenceAsync(WifiContext ctx)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("netsh.exe", "wlan show interfaces");
            if (exitCode is null) return DiagnosticStepResult.Skip("netsh wlan show interfaces timed out.");

            bool none = output.Contains("no wireless interface", StringComparison.OrdinalIgnoreCase);
            ctx.HasWirelessAdapter = !none && output.Contains("Name", StringComparison.OrdinalIgnoreCase);

            if (!ctx.HasWirelessAdapter)
                return DiagnosticStepResult.Skip("No wireless adapter found on this system - the rest of this check doesn't apply.");
            return DiagnosticStepResult.Pass("A wireless adapter was found.", new[] { output.Trim() });
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check for a wireless adapter: {ex.Message}");
        }
    }

    /// <summary>`netsh wlan show wlanreport` generates an HTML report at a fixed well-known path
    /// (%ProgramData%\Microsoft\Windows\WlanReport\wlan-report-latest.html) - this reads that file
    /// and best-effort extracts disconnect events and their reason codes, the same "strip tags,
    /// look for a recognizable shape" approach CheckSleepStudyAsync already uses for another
    /// version-varying, mostly-tabular Windows diagnostic report.</summary>
    public static async Task<DiagnosticStepResult> CheckWlanReportAsync(WifiContext ctx)
    {
        try
        {
            var (_, exitCode) = await RunCapturedAsync("netsh.exe", "wlan show wlanreport", timeoutMs: 20000);
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "WlanReport", "wlan-report-latest.html");

            if (exitCode != 0 || !File.Exists(reportPath))
                return DiagnosticStepResult.Skip("Couldn't generate/find the WLAN report (netsh wlan show wlanreport).");

            string html = await File.ReadAllTextAsync(reportPath);
            string text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));

            var disconnects = Regex.Matches(text, @"[Dd]isconnect(?:ed)?\D{0,60}?[Rr]eason(?:\s*[Cc]ode)?\D{0,10}(\d{1,5})")
                .Select(m => m.Groups[1].Value)
                .Where(v => v.Length > 0)
                .ToList();

            var evidence = new List<string> { $"Full report: {reportPath}" };
            if (disconnects.Count == 0)
                return DiagnosticStepResult.Warn(
                    "WLAN report generated, but no specific disconnect-reason codes could be confidently extracted from it - open the report directly for the full disconnect timeline.",
                    evidence);

            var byReason = disconnects.GroupBy(r => r).OrderByDescending(g => g.Count()).ToList();
            evidence.InsertRange(0, byReason.Select(g => $"Reason code {g.Key}: {g.Count()} occurrence(s)"));
            var top = byReason[0];
            return DiagnosticStepResult.Warn(
                $"{disconnects.Count} disconnect event(s) found in the WLAN report. Most common reason code: {top.Key} ({top.Count()} occurrence(s)).",
                evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the WLAN report: {ex.Message}");
        }
    }

    /// <summary>WLAN-AutoConfig event IDs 8000-8003 (association/disconnect/roam) and the
    /// 11000-series (adapter/radio state changes), last 7 days.</summary>
    public static DiagnosticStepResult CheckWlanAutoConfigEvents()
    {
        var ids = new List<int> { 8000, 8001, 8002, 8003 };
        ids.AddRange(Enumerable.Range(11000, 15));
        var events = ReadProviderEvents("Microsoft-Windows-WLAN-AutoConfig/Operational", null, ids, 7, maxEvents: 100);

        if (events.Count == 0)
            return DiagnosticStepResult.Pass("No WLAN-AutoConfig disconnect/roam events (8000-8003/11000-series) found in the last 7 days.");

        var byId = events.GroupBy(e => e.EventId).OrderByDescending(g => g.Count()).ToList();
        var evidence = events.Take(15).Select(e => $"{e.TimeCreated:g} - event {e.EventId}: {Truncate(e.Message, 140)}").ToList();
        var top = byId[0];
        return DiagnosticStepResult.Warn($"{events.Count} WLAN-AutoConfig event(s) in the last 7 days - most common is event {top.Key} ({top.Count()} occurrence(s)).", evidence);
    }

    /// <summary>`powercfg /query` for the active plan's wireless adapter power-saving setting - an
    /// aggressive setting is a common, easy-to-fix cause of a Wi-Fi adapter dropping out on idle.</summary>
    public static async Task<DiagnosticStepResult> CheckWirelessPowerSavingAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/query SCHEME_CURRENT SUB_WIRELESS");
            if (exitCode is null) return DiagnosticStepResult.Skip("powercfg /query timed out.");
            if (string.IsNullOrWhiteSpace(output) || output.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                return DiagnosticStepResult.Skip("Couldn't read the wireless adapter power-saving setting (this system may not expose SUB_WIRELESS).");

            var acMatch = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            var dcMatch = Regex.Match(output, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            int? ac = acMatch.Success ? Convert.ToInt32(acMatch.Groups[1].Value, 16) : null;
            int? dc = dcMatch.Success ? Convert.ToInt32(dcMatch.Groups[1].Value, 16) : null;

            if (ac is null && dc is null)
                return DiagnosticStepResult.Skip("Couldn't determine the wireless adapter's power-saving setting from powercfg /query.");

            var evidence = new List<string>
            {
                ac is { } a ? $"Plugged in (AC): {DescribeWirelessPowerSavingLevel(a)}" : "Plugged in (AC): unknown",
                dc is { } d ? $"On battery (DC): {DescribeWirelessPowerSavingLevel(d)}" : "On battery (DC): unknown",
            };

            bool aggressive = (ac ?? 0) >= 2 || (dc ?? 0) >= 2;
            return aggressive
                ? DiagnosticStepResult.Warn("An aggressive wireless power-saving setting is active - a common, easy-to-fix cause of the adapter dropping out on idle.", evidence)
                : DiagnosticStepResult.Pass("Wireless adapter power-saving setting looks reasonable.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the wireless power-saving setting: {ex.Message}");
        }
    }

    private static string DescribeWirelessPowerSavingLevel(int index) => index switch
    {
        0 => "Maximum Performance (no power saving)",
        1 => "Low Power Saving",
        2 => "Medium Power Saving",
        3 => "Maximum Power Saving",
        _ => $"Unknown ({index})",
    };

    #endregion

    #region 911 - "My disk sits at 100%"

    public sealed class DiskBottleneckContext
    {
        public string MediaType { get; set; } = "Unknown";
        public string DriveLetter { get; set; } = "C";
    }

    private static PerformanceCounter? TryCreateTotalDiskCounter(string counter)
    {
        try { return new PerformanceCounter("LogicalDisk", counter, "_Total", readOnly: true); }
        catch { return null; }
    }

    /// <summary>LogicalDisk\Avg. Disk sec/Transfer (latency) vs. Disk Bytes/sec (throughput) for
    /// the system drive, sampled fresh over ~1 second - high latency with low throughput points at
    /// the disk itself being slow (fragmentation/aging/failing hardware); high throughput points at
    /// genuinely heavy I/O from something (a process or background service, checked next).</summary>
    public static async Task<DiagnosticStepResult> CheckDiskLatencyVsThroughputAsync(CancellationToken ct)
    {
        string driveInstance = Environment.SystemDirectory[..1] + ":";
        PerformanceCounter? latencyCounter = null, throughputCounter = null;
        try
        {
            try { latencyCounter = new PerformanceCounter("LogicalDisk", "Avg. Disk sec/Transfer", driveInstance, readOnly: true); }
            catch { latencyCounter = TryCreateTotalDiskCounter("Avg. Disk sec/Transfer"); }
            try { throughputCounter = new PerformanceCounter("LogicalDisk", "Disk Bytes/sec", driveInstance, readOnly: true); }
            catch { throughputCounter = TryCreateTotalDiskCounter("Disk Bytes/sec"); }

            if (latencyCounter is null && throughputCounter is null)
                return DiagnosticStepResult.Skip("Couldn't read LogicalDisk latency/throughput counters.");

            latencyCounter?.NextValue();
            throughputCounter?.NextValue();
            await Task.Delay(1000, ct);
            float latencySec = latencyCounter?.NextValue() ?? 0;
            float bytesPerSec = throughputCounter?.NextValue() ?? 0;

            double latencyMs = latencySec * 1000;
            var evidence = new List<string>
            {
                $"Avg. Disk sec/Transfer: {latencyMs:0.#} ms",
                $"Disk Bytes/sec: {Formatting.FormatByteRate(bytesPerSec)}",
            };

            bool latencyHigh = latencyMs >= 25;
            bool throughputHigh = bytesPerSec >= 50 * 1024 * 1024; // ~50 MB/s sustained reads as genuinely busy

            string summary = (latencyHigh, throughputHigh) switch
            {
                (true, false) => "High latency with low throughput - reads as the disk itself being the bottleneck (aging/fragmented/failing), not a specific app driving heavy I/O.",
                (true, true) => "High latency and high throughput together - reads as genuinely I/O-heavy right now (see the top process below).",
                (false, true) => "High throughput with normal latency - reads as genuinely I/O-heavy right now, not a slow disk (see the top process below).",
                _ => "Disk latency and throughput both look normal right now.",
            };
            return latencyHigh || throughputHigh
                ? DiagnosticStepResult.Warn(summary, evidence)
                : DiagnosticStepResult.Pass(summary, evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read disk latency/throughput counters: {ex.Message}");
        }
        finally
        {
            latencyCounter?.Dispose();
            throughputCounter?.Dispose();
        }
    }

    /// <summary>Top per-process disk I/O from the already-sampled Processes list (ProcessRow.
    /// DiskBytesPerSec, computed by ProcessMonitorService.Sample() - reused rather than re-read),
    /// the same "reuse the shared sampler's already-computed figures" shape CheckTopOffenders
    /// already uses for CPU/memory.</summary>
    public static DiagnosticStepResult CheckTopDiskIoProcess(IReadOnlyList<ProcessRow> processes)
    {
        try
        {
            var top = processes.Where(p => p.DiskBytesPerSec > 0).OrderByDescending(p => p.DiskBytesPerSec).Take(5).ToList();
            if (top.Count == 0)
                return DiagnosticStepResult.Pass("No process is showing meaningful disk I/O right now.");

            var evidence = top.Select(p => $"{p.Name} (PID {p.Pid}): {Formatting.FormatByteRate(p.DiskBytesPerSec)}").ToList();
            var leader = top[0];
            bool dominant = top.Count == 1 || leader.DiskBytesPerSec >= top[1].DiskBytesPerSec * 2;

            if (dominant && leader.DiskBytesPerSec >= 5 * 1024 * 1024)
                return DiagnosticStepResult.Warn($"{leader.Name} (PID {leader.Pid}) is driving most of the current disk I/O at {Formatting.FormatByteRate(leader.DiskBytesPerSec)}.", evidence);
            return DiagnosticStepResult.Pass("No single process is clearly dominating disk I/O right now.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the process list: {ex.Message}");
        }
    }

    /// <summary>Media type (HDD/SSD/...) of the system drive via DiskFragmentationService's
    /// existing MSFT_Volume associator chain - reused, not re-implemented. Feeds the fragmentation
    /// step's own short-circuit below (an SSD doesn't fragment in any way that matters, so there's
    /// no point running a full-volume analyze pass on one).</summary>
    public static DiagnosticStepResult CheckSystemDriveMediaType(DiskBottleneckContext ctx)
    {
        try
        {
            string driveLetter = Environment.SystemDirectory[..1];
            ctx.DriveLetter = driveLetter;
            ctx.MediaType = DiskFragmentationService.GetMediaType(driveLetter);

            var evidence = new List<string> { $"Drive {driveLetter}: media type {ctx.MediaType}" };
            if (ctx.MediaType == "HDD")
                return DiagnosticStepResult.Warn("System drive is a spinning disk (HDD) - fragmentation is worth checking (manual step below).", evidence);
            return DiagnosticStepResult.Pass($"System drive media type: {ctx.MediaType}{(ctx.MediaType == "SSD" ? " - fragmentation doesn't apply." : ".")}", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't determine the system drive's media type: {ex.Message}");
        }
    }

    /// <summary>#911: the fragmentation check is manual (<see cref="DiagnosticStep.IsManual"/>)
    /// rather than part of the automatic sequence - DiskFragmentationService.Analyze is already
    /// documented as "on-demand only... even an analyze-only pass walks the whole volume's MFT and
    /// can take a while", so this branch respects that rather than firing it on every "disk sits at
    /// 100%" run. Short-circuits to a Pass without running defrag.exe at all when the drive isn't a
    /// spinning disk.</summary>
    public static async Task<DiagnosticStepResult> CheckFragmentationManualAsync(DiskBottleneckContext ctx)
    {
        try
        {
            string mediaType = ctx.MediaType.Length > 0 && ctx.MediaType != "Unknown" ? ctx.MediaType : DiskFragmentationService.GetMediaType(ctx.DriveLetter);
            if (mediaType != "HDD")
                return DiagnosticStepResult.Pass($"Drive {ctx.DriveLetter}: media type {mediaType} - fragmentation doesn't apply, skipping the analyze pass.");

            // #354 added MftSizeBytes to FragmentationAnalysis's field list after this call was
            // written, which also grows its generated Deconstruct - naming the fields actually used
            // instead of positionally deconstructing all seven avoids needing to touch this call
            // site again the next time that record gains a field.
            var analysis = await DiskFragmentationService.Analyze(ctx.DriveLetter);
            if (!analysis.Success)
                return DiagnosticStepResult.Skip(analysis.Message);

            var evidence = new List<string> { $"Drive {ctx.DriveLetter}: {analysis.Message}" };
            if (analysis.FragmentedPercent is { } p && p >= 10)
                return DiagnosticStepResult.Warn($"Drive {ctx.DriveLetter} is {p}% fragmented - a plausible contributor to sustained high disk activity on an HDD.", evidence);
            return DiagnosticStepResult.Pass($"Drive {ctx.DriveLetter} fragmentation: {analysis.Message}.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't analyze fragmentation: {ex.Message}");
        }
    }

    /// <summary>Whether Windows Search (a standalone process, so directly attributable) or the
    /// SysMain/Superfetch service (hosted inside a shared svchost.exe, so its own I/O can't be
    /// cleanly isolated per-process the way SearchIndexer's can - flagged by running state only,
    /// with that limitation stated explicitly) look like the top writer right now.</summary>
    public static DiagnosticStepResult CheckBackgroundWriter(IReadOnlyList<ProcessRow> processes)
    {
        try
        {
            var evidence = new List<string>();
            var searchIndexer = processes.FirstOrDefault(p => p.Name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase));
            bool searchHigh = searchIndexer is not null && searchIndexer.DiskBytesPerSec >= 5 * 1024 * 1024;
            if (searchIndexer is not null)
                evidence.Add($"SearchIndexer.exe (PID {searchIndexer.Pid}): {Formatting.FormatByteRate(searchIndexer.DiskBytesPerSec)}");

            bool sysMainRunning = false;
            try
            {
                var services = new ServiceControlService().Sample();
                var sysMain = services.FirstOrDefault(s => s.ServiceName.Equals("SysMain", StringComparison.OrdinalIgnoreCase));
                sysMainRunning = sysMain?.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                if (sysMain is not null)
                    evidence.Add($"SysMain (Superfetch) service: {sysMain.Status} - runs inside a shared svchost.exe, so its own I/O can't be isolated per-process here.");
            }
            catch { /* service list unavailable - evidence just won't mention it */ }

            if (searchHigh)
                return DiagnosticStepResult.Warn("Windows Search indexing is currently writing heavily to disk.", evidence);
            if (sysMainRunning)
                return DiagnosticStepResult.Warn("SysMain (Superfetch) is running - a plausible (but not directly measurable per-process) contributor to background disk activity, especially just after boot or an app update.", evidence);
            return DiagnosticStepResult.Pass("Windows Search and SysMain don't look like the current source of disk activity.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check background indexing/prefetch services: {ex.Message}");
        }
    }

    #endregion

    #region 912 - "Battery dies too fast"

    public sealed class BatteryContext
    {
        public bool HasBattery { get; set; }
    }

    /// <summary>Win32_Battery presence check - gates the rest of this branch via each later step's
    /// ShouldRun predicate (#914), since running powercfg /batteryreport on a desktop with no
    /// battery at all just produces its own "This machine has no batteries" error text rather than
    /// anything worth showing as a separate failed step.</summary>
    public static DiagnosticStepResult CheckBatteryPresence(BatteryContext ctx)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, EstimatedChargeRemaining FROM Win32_Battery");
            var batteries = searcher.Get().Cast<ManagementObject>().ToList();
            ctx.HasBattery = batteries.Count > 0;

            if (!ctx.HasBattery)
                return DiagnosticStepResult.Skip("No battery detected - this looks like a desktop, so the rest of this check doesn't apply.");

            var evidence = batteries.Select(b => $"{b["DeviceID"]}: {b["EstimatedChargeRemaining"]}% estimated charge remaining").ToList();
            return DiagnosticStepResult.Pass($"{batteries.Count} battery(ies) detected.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't check for a battery: {ex.Message}");
        }
    }

    /// <summary>`powercfg /batteryreport` - design vs. full-charge capacity (-&gt; a health%
    /// headline) and the report's own recent-usage detail, best-effort extracted from the generated
    /// HTML the same "strip tags, look for a recognizable shape" way CheckSleepStudyAsync already
    /// does for a different powercfg report.</summary>
    public static async Task<DiagnosticStepResult> CheckBatteryReportAsync()
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"tmp-batteryreport-{Guid.NewGuid():N}.html");
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", $"/batteryreport /output \"{reportPath}\"", timeoutMs: 20000);
            if (exitCode != 0 || !File.Exists(reportPath))
                return DiagnosticStepResult.Skip($"Couldn't generate the battery report: {Truncate(output.Trim(), 200)}");

            string html = await File.ReadAllTextAsync(reportPath);
            string text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));

            var designMatch = Regex.Match(text, @"DESIGN CAPACITY\s*([\d,]+)\s*mWh", RegexOptions.IgnoreCase);
            var fullMatch = Regex.Match(text, @"FULL CHARGE CAPACITY\s*([\d,]+)\s*mWh", RegexOptions.IgnoreCase);

            var evidence = new List<string> { $"Full report: {reportPath}" };
            if (!designMatch.Success || !fullMatch.Success ||
                !double.TryParse(designMatch.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var design) ||
                !double.TryParse(fullMatch.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var full) || design <= 0)
            {
                return DiagnosticStepResult.Warn("Battery report generated, but design/full-charge capacity couldn't be confidently extracted - open the report directly for the full detail.", evidence);
            }

            double healthPercent = Math.Round(full / design * 100, 0);
            evidence.Insert(0, $"Design capacity: {design:0} mWh");
            evidence.Insert(1, $"Full charge capacity: {full:0} mWh");

            string summary = $"Battery health: {healthPercent:0}% of design capacity ({full:0} mWh of {design:0} mWh).";
            return healthPercent < 80
                ? DiagnosticStepResult.Warn(summary + " Meaningfully degraded - this alone can explain shorter battery life.", evidence)
                : DiagnosticStepResult.Pass(summary, evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run/parse powercfg /batteryreport: {ex.Message}");
        }
    }

    /// <summary>`powercfg /srumutil /output &lt;file&gt; /xml` - SRUM-based per-app energy
    /// estimates. Less universally present than /batteryreport (added later, and can be missing or
    /// policy-disabled on some builds), so this degrades to Skip on any failure rather than treating
    /// its absence as a check failure - the summary explicitly says so per this round's spec.</summary>
    public static async Task<DiagnosticStepResult> CheckSrumEnergyAsync()
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"tmp-srum-{Guid.NewGuid():N}.xml");
        try
        {
            var (_, exitCode) = await RunCapturedAsync("powercfg.exe", $"/srumutil /output \"{xmlPath}\" /xml", timeoutMs: 25000);
            if (exitCode != 0 || !File.Exists(xmlPath))
                return DiagnosticStepResult.Skip("powercfg /srumutil isn't available on this Windows build (or produced no output) - it's less universally present than /batteryreport.");

            string xml = await File.ReadAllTextAsync(xmlPath);
            var apps = Regex.Matches(xml, @"(?:ApplicationName|APPID|Name)=""([^""]+\.exe)""[^>]*?(?:EnergyEstimate|Energy)=""(\d+(?:\.\d+)?)""", RegexOptions.IgnoreCase)
                .Select(m => (Name: m.Groups[1].Value, Energy: double.Parse(m.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture)))
                .Where(a => a.Energy > 0)
                .GroupBy(a => a.Name).Select(g => (g.Key, Energy: g.Sum(a => a.Energy)))
                .OrderByDescending(a => a.Energy)
                .Take(8)
                .ToList();

            var evidence = new List<string> { $"Full SRUM export: {xmlPath}" };
            if (apps.Count == 0)
                return DiagnosticStepResult.Warn("SRUM energy data exported, but no specific per-app energy figures could be confidently extracted - open the XML export directly.", evidence);

            evidence.InsertRange(0, apps.Select(a => $"{a.Key}: {a.Energy:0} (relative energy estimate)"));
            return DiagnosticStepResult.Warn($"Top energy-consuming app: {apps[0].Key}. Figures are SRUM's own relative energy estimates, not a calibrated mWh figure.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't run/parse powercfg /srumutil: {ex.Message}");
        }
    }

    /// <summary>Active power plan name, reused from PowerPlanService rather than re-parsing
    /// `powercfg /list` here.</summary>
    public static async Task<DiagnosticStepResult> CheckActivePowerPlanForBatteryAsync()
    {
        try
        {
            var plans = await PowerPlanService.ListPowerPlansAsync();
            var active = plans.FirstOrDefault(p => p.IsActive);
            if (active is null)
                return DiagnosticStepResult.Skip("Couldn't determine the active power plan.");

            var evidence = new List<string> { $"Active plan: {active.Name}" };
            bool highPerf = active.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase) ||
                            active.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase);
            return highPerf
                ? DiagnosticStepResult.Warn($"Active power plan is \"{active.Name}\" - a high-performance plan trades battery life for performance.", evidence)
                : DiagnosticStepResult.Pass($"Active power plan: {active.Name}.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read the active power plan: {ex.Message}");
        }
    }

    #endregion

    #region 913 - "A device keeps disconnecting or isn't recognized"

    public sealed class PnpProblemContext
    {
        public string? WorstDeviceName { get; set; }
        public int WorstErrorCode { get; set; }
    }

    /// <summary>Common ConfigManagerErrorCode values translated to plain English - not exhaustive
    /// (Microsoft documents roughly 50 of these), just the ones a user is actually likely to hit for
    /// a flaky/unrecognized device. An unlisted code still shows as "Code N" rather than nothing.</summary>
    private static readonly Dictionary<int, string> ConfigManagerErrorDescriptions = new()
    {
        [1] = "Device is not configured correctly",
        [3] = "Driver may be corrupted, or the system is low on memory",
        [10] = "Device cannot start",
        [12] = "Device cannot find enough free resources to use",
        [14] = "Device requires a restart to work correctly",
        [16] = "Windows cannot identify all the resources this device uses",
        [18] = "Reinstall the drivers for this device",
        [19] = "Windows cannot start this device - the registry may be corrupted",
        [21] = "Windows is removing this device",
        [22] = "Device is disabled",
        [24] = "Device is not present, not working properly, or does not have all its drivers installed",
        [28] = "Drivers for this device are not installed",
        [29] = "Device is disabled because the firmware didn't give it the required resources",
        [31] = "Device is not working properly - Windows cannot load the required drivers",
        [32] = "Driver for this device has been disabled (blocked as an intentional policy)",
        [33] = "Windows cannot determine which resources this device needs",
        [34] = "Windows cannot determine the settings for this device",
        [35] = "This computer's system firmware does not include enough information to properly configure this device",
        [37] = "Windows cannot initialize the device driver for this hardware",
        [38] = "Driver for this device was previously started and is still in memory",
        [39] = "Windows cannot load the device driver - it may be corrupted or missing",
        [40] = "Windows cannot access this hardware because its service key information is missing or recorded incorrectly",
        [41] = "Windows successfully loaded the device driver but cannot find the hardware device",
        [42] = "Windows cannot load the device driver because there is a duplicate device already running",
        [43] = "Windows has stopped this device because it has reported problems",
        [44] = "An application or service has shut down this hardware device",
        [45] = "Currently, this hardware device is not connected to the computer",
        [47] = "Windows cannot use this hardware device because it has been prepared for safe removal",
        [48] = "The software for this device has been blocked from starting because it is known to have problems with Windows",
        [52] = "Windows cannot verify the digital signature for the drivers required for this device",
    };

    private static string DescribeConfigManagerErrorCode(int code) =>
        ConfigManagerErrorDescriptions.TryGetValue(code, out var desc) ? $"Code {code}: {desc}" : $"Code {code} (no plain-English description available)";

    /// <summary>Win32_PnPEntity entries with a nonzero ConfigManagerErrorCode - Device Manager's own
    /// "yellow bang / missing driver / disabled" state, queried directly via WMI rather than
    /// shelling out to a tool, since Win32_PnPEntity already exposes this cleanly.</summary>
    public static DiagnosticStepResult CheckPnpProblemDevices(PnpProblemContext ctx)
    {
        try
        {
            var problems = new List<(string Name, int Code)>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "(unnamed device)").Trim();
                int code = mo["ConfigManagerErrorCode"] is { } raw ? Convert.ToInt32(raw) : 0;
                if (code == 0) continue;
                problems.Add((name, code));
            }

            if (problems.Count == 0)
                return DiagnosticStepResult.Pass("No devices currently report a problem (ConfigManagerErrorCode) in Device Manager.");

            var evidence = problems.Select(p => $"{p.Name} - {DescribeConfigManagerErrorCode(p.Code)}").ToList();
            // Prefer a "not currently connected" (45) device as the likely disconnect culprit when
            // one is present - the code that most directly matches this symptom's wording - and
            // otherwise just take the first problem device found.
            var worst = problems.OrderByDescending(p => p.Code == 45 ? 1 : 0).First();
            ctx.WorstDeviceName = worst.Name;
            ctx.WorstErrorCode = worst.Code;

            return DiagnosticStepResult.Warn($"{problems.Count} device(s) currently report a problem. Most relevant: {worst.Name} - {DescribeConfigManagerErrorCode(worst.Code)}.", evidence);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't query Win32_PnPEntity: {ex.Message}");
        }
    }

    /// <summary>Kernel-PnP and USBHUB3 event-log entries from the last 7 days, joined to the
    /// problem device found above by a name substring match (the same correlate-by-name join
    /// #907's boot-culprit step already uses) - counts how many times that device (or "a device",
    /// if none matched by name) shows up in that window.</summary>
    public static DiagnosticStepResult CheckDeviceDisconnectEvents(PnpProblemContext ctx)
    {
        var kernelPnp = ReadProviderEvents("System", "Microsoft-Windows-Kernel-PnP", new[] { 400, 410, 411, 420 }, 7, maxEvents: 100);
        var usbEvents = ReadAllProviderEvents("Microsoft-Windows-USB-USBHUB3-Diagnostic/Operational", 7, maxEvents: 100);

        var all = kernelPnp.Concat(usbEvents).OrderByDescending(e => e.TimeCreated).ToList();
        if (all.Count == 0)
            return DiagnosticStepResult.Pass("No Kernel-PnP or USBHUB3 disconnect-related events found in the last 7 days.");

        List<RawEvent> matching = all;
        if (!string.IsNullOrEmpty(ctx.WorstDeviceName))
        {
            var m = all.Where(e => e.Message.Contains(ctx.WorstDeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Count > 0) matching = m;
        }

        var evidence = matching.Take(15).Select(e => $"{e.TimeCreated:g} - {e.ProviderName} event {e.EventId}: {Truncate(e.Message, 140)}").ToList();
        string deviceLabel = ctx.WorstDeviceName ?? "a device";
        return DiagnosticStepResult.Warn($"{matching.Count} disconnect-related event(s) found for {deviceLabel} in the last 7 days ({all.Count} total Kernel-PnP/USBHUB3 events).", evidence);
    }

    /// <summary>Whether any USB root hub has "allow the computer to turn off this device to save
    /// power" enabled - the registry surface behind that Device Manager checkbox
    /// (Device Parameters\SelectiveSuspendEnabled under each root hub's instance key). A root hub
    /// with this on can power down (and drop) everything attached to it, a classic cause of USB
    /// devices dropping out under light/idle load.</summary>
    public static DiagnosticStepResult CheckUsbRootHubPowerSetting()
    {
        try
        {
            var enabledOn = new List<string>();
            var checkedHubs = new List<string>();
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey is null)
                return DiagnosticStepResult.Skip("Couldn't read HKLM\\SYSTEM\\CurrentControlSet\\Enum\\USB.");

            foreach (var vidPidName in usbKey.GetSubKeyNames().Where(n => n.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)))
            {
                using var vidPidKey = usbKey.OpenSubKey(vidPidName);
                if (vidPidKey is null) continue;
                foreach (var instanceName in vidPidKey.GetSubKeyNames())
                {
                    using var deviceParams = vidPidKey.OpenSubKey($@"{instanceName}\Device Parameters");
                    var raw = deviceParams?.GetValue("SelectiveSuspendEnabled") ?? deviceParams?.GetValue("EnhancedPowerManagementEnabled");
                    if (raw is null) continue;
                    string hubLabel = $"{vidPidName}\\{instanceName}";
                    checkedHubs.Add(hubLabel);
                    if (Convert.ToInt32(raw) != 0)
                        enabledOn.Add(hubLabel);
                }
            }

            if (checkedHubs.Count == 0)
                return DiagnosticStepResult.Skip("No USB root hub power-management registry values were found to check.");

            if (enabledOn.Count > 0)
                return DiagnosticStepResult.Warn(
                    $"{enabledOn.Count} of {checkedHubs.Count} USB root hub(s) allow Windows to power them down to save power - a plausible cause of devices dropping out under light/idle load.",
                    enabledOn);
            return DiagnosticStepResult.Pass($"None of the {checkedHubs.Count} checked USB root hub(s) have selective suspend enabled.", checkedHubs);
        }
        catch (Exception ex)
        {
            return DiagnosticStepResult.Skip($"Couldn't read USB root hub power settings: {ex.Message}");
        }
    }

    #endregion
}
