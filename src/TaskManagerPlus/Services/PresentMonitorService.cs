using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #250/#251/#252/#258/#259: a real-time-ish "PresentMon-lite" - periodic short ETW captures of
/// Microsoft-Windows-DxgKrnl (the GPU kernel's Present/Flip/VSync/queue-packet events) and
/// Microsoft-Windows-Dwm-Core, correlated per process to produce actual frame times for whatever
/// is rendering (#250), from which #251's percentiles/stutter numbers and #259's long-packet/
/// preemption rows are derived. #258 pairs this data with InputLatencyService's own captured
/// timestamps (see EstimateInputToPresentMs) - kept as a query method here rather than owning any
/// input-side state, since ResponsivenessViewModel already holds both services and can pass the
/// one piece of data (the last raw-input arrival time) this needs.
///
/// Follows DpcLatencyService's exact shape/tradeoffs (read that file's remarks first) - the same
/// "known tool, shelled out, output parsed" choice this app makes everywhere over an ETW reader
/// library: logman starts/stops the trace, tracerpt converts the .etl to XML, and the XML is
/// parsed tolerantly. Two differences from DpcLatencyService's classic-MOF capture:
///
///  1. DxgKrnl/Dwm-Core are modern manifest-based providers (not the classic "NT Kernel Logger"
///     singleton), so the session uses an arbitrary name and providers are added with `logman
///     update trace -p <provider> -ets` after an initial empty `create trace`, rather than the
///     one-shot `create trace -p "Windows Kernel Trace" "(dpc,isr)"` DpcLatencyService uses.
///  2. A manifest-based event's XML carries a documented, SDK-stable System/Execution/@ProcessID
///     and System/TimeCreated/@SystemTime - unlike the classic MOF schema's undocumented field
///     names, these two are reliable to extract by name. Everything else (which field, if any,
///     encodes present-mode/duration) is exactly as undocumented/version-dependent as the classic
///     schema, so that part is parsed exactly as tolerantly as DpcLatencyService parses DPC/ISR
///     duration fields - see ClassifyPresentMode's remarks for what was found/not found and why
///     "Unknown" is a legitimate, expected outcome for #252, not a parsing bug.
/// </summary>
public sealed class PresentMonitorService
{
    // Not a reserved singleton name like DpcLatencyService's "NT Kernel Logger" - any name works
    // for a modern (non-classic) trace session, kept distinct so it can't collide with the DPC
    // measurement session if both happen to run at once.
    private const string SessionName = "TMPlus Present Monitor";
    private const string DxgKrnlProvider = "Microsoft-Windows-DxgKrnl";
    private const string DwmCoreProvider = "Microsoft-Windows-Dwm-Core";

    private static readonly string LogmanPath = Path.Combine(Environment.SystemDirectory, "logman.exe");
    private static readonly string TracerptPath = Path.Combine(Environment.SystemDirectory, "tracerpt.exe");
    private static readonly string[] DurationFieldCandidates = { "Duration", "ElapsedTime", "TimeElapsed", "RunningTime", "dwDuration" };

    // #259: only surface a GPU packet/preemption this far past a 60Hz frame budget - well short of
    // a TDR's own multi-second default timeout, but already a "quick flag" worth showing.
    private const double LongPacketThresholdUs = 2000;

    public bool ToolsAvailable { get; } = File.Exists(LogmanPath) && File.Exists(TracerptPath);

    private readonly Dictionary<int, string> _pidToName = new();
    private readonly Dictionary<int, List<DateTime>> _frameTimesUtcByPid = new();
    private readonly Dictionary<int, string> _presentModeByPid = new();
    private readonly List<GpuStallRow> _gpuStalls = new();

    public IReadOnlyList<GpuStallRow> GpuStalls => _gpuStalls;

    /// <summary>Clears all accumulated per-session state - call once from
    /// ResponsivenessViewModel.StartPresentMonitorAsync, mirroring DpcLatencyService.ResetSession.</summary>
    public void ResetSession()
    {
        _pidToName.Clear();
        _frameTimesUtcByPid.Clear();
        _presentModeByPid.Clear();
        _gpuStalls.Clear();
    }

    /// <summary>#258: pairs a raw-input arrival timestamp (from InputLatencyService.LastEventUtc)
    /// against the next captured present/frame timestamp for the given pid (falling back to
    /// whichever process has produced the most frames this session, if no pid is given) - an
    /// estimate of click-to-photon delay, excluding display panel latency. Null when there's no
    /// frame after the input yet, or nothing captured at all.</summary>
    public double? EstimateInputToPresentMs(DateTime inputUtc, int? pid)
    {
        List<DateTime>? times = null;
        if (pid is { } p) _frameTimesUtcByPid.TryGetValue(p, out times);
        times ??= _frameTimesUtcByPid.Values.OrderByDescending(l => l.Count).FirstOrDefault();
        if (times is null || times.Count == 0) return null;

        DateTime? next = null;
        foreach (var t in times)
        {
            if (t <= inputUtc) continue;
            if (next is null || t < next.Value) next = t;
        }
        if (next is not { } n) return null;

        double ms = (n - inputUtc).TotalMilliseconds;
        return ms is >= 0 and < 5000 ? ms : null;
    }

    /// <summary>#251/#258: the raw frame-to-frame gaps (ms) captured for one pid this session, most
    /// recent last, capped to maxCount - backs the optional frame-time-vs-index scatter chart.</summary>
    public List<double> GetFrameTimesMs(int pid, int maxCount)
    {
        if (!_frameTimesUtcByPid.TryGetValue(pid, out var times) || times.Count < 2) return new List<double>();
        var sorted = times.OrderBy(t => t).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < sorted.Count; i++)
        {
            double ms = (sorted[i] - sorted[i - 1]).TotalMilliseconds;
            if (ms is > 0.1 and < 2000) gaps.Add(ms);
        }
        return gaps.Count <= maxCount ? gaps : gaps.Skip(gaps.Count - maxCount).ToList();
    }

    /// <summary>#250/#251/#252: one row per process that produced at least two usable frame
    /// timestamps this session, busiest (most frames) first - see BuildOneRow.</summary>
    public List<PresentAppRow> BuildAppRows()
    {
        var rows = new List<PresentAppRow>();
        foreach (var (pid, times) in _frameTimesUtcByPid)
        {
            var row = BuildOneRow(pid, times);
            if (row is not null) rows.Add(row);
        }
        return rows.OrderByDescending(r => r.FrameCount).ToList();
    }

    private PresentAppRow? BuildOneRow(int pid, List<DateTime> times)
    {
        if (times.Count < 3) return null;
        var sorted = times.OrderBy(t => t).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < sorted.Count; i++)
        {
            double ms = (sorted[i] - sorted[i - 1]).TotalMilliseconds;
            if (ms is > 0.1 and < 2000) gaps.Add(ms); // drop implausible/negative/huge gaps (session boundaries)
        }
        if (gaps.Count < 2) return null;

        double avgMs = gaps.Average();
        double median = Percentile(gaps, 0.5);
        int hitches = gaps.Count(g => g > median * 2);
        double variance = gaps.Average(g => (g - avgMs) * (g - avgMs));
        double stdDev = Math.Sqrt(variance);

        // #251: 1%-low / 0.1%-low FPS - the average FPS of the worst (largest) 1% / 0.1% of frame
        // times, the standard "stutter-sensitive" definition, unlike a single-number average FPS.
        var worstDesc = gaps.OrderByDescending(g => g).ToList();
        double low1Fps = FpsFromWorst(worstDesc, 0.01);
        double low01Fps = FpsFromWorst(worstDesc, 0.001);

        string mode = _presentModeByPid.TryGetValue(pid, out var m) ? m : "Unknown";
        return new PresentAppRow
        {
            Pid = pid,
            ProcessName = _pidToName.TryGetValue(pid, out var name) ? name : $"pid {pid}",
            FrameCount = sorted.Count,
            AvgFrameTimeMs = avgMs,
            AvgFps = avgMs > 0 ? 1000.0 / avgMs : 0,
            HitchCount = hitches,
            FrameTimeStdDevMs = stdDev,
            Low1PercentFps = low1Fps,
            Low01PercentFps = low01Fps,
            PresentModeText = mode,
            PresentModeNote = PresentModeLookup.Describe(mode),
        };
    }

    private static double FpsFromWorst(List<double> sortedDesc, double fraction)
    {
        if (sortedDesc.Count == 0) return 0;
        int count = Math.Max(1, (int)Math.Ceiling(sortedDesc.Count * fraction));
        double avgWorst = sortedDesc.Take(count).Average();
        return avgWorst > 0 ? 1000.0 / avgWorst : 0;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int index = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>Runs one capture-convert-parse cycle. Never throws except on cancellation - every
    /// other failure (tool missing, access denied, malformed trace, unrecognized event schema)
    /// comes back as Ok=false/EventsParsed=0 with a plain-English Message, matching
    /// DpcLatencyService.SampleOnceAsync.</summary>
    public async Task<(bool Ok, string Message, int EventsParsed)> SampleOnceAsync(TimeSpan window, CancellationToken ct)
    {
        if (!ToolsAvailable)
            return (false, "logman.exe/tracerpt.exe weren't found on this system - present/frame-time capture isn't available.", 0);

        string dir = Path.Combine(AppPaths.SettingsDirectory, "ResponsivenessTraces");
        try { Directory.CreateDirectory(dir); }
        catch { return (false, "Couldn't create a temp folder for the trace.", 0); }

        string etl = Path.Combine(dir, $"present_{Guid.NewGuid():N}.etl");
        string xml = Path.Combine(dir, $"present_{Guid.NewGuid():N}.xml");

        try
        {
            // Best-effort cleanup of a stale session left over from a previous crashed/killed run.
            await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);

            var (createOk, createErr) = await RunProcessAsync(LogmanPath, $"create trace \"{SessionName}\" -o \"{etl}\" -ets", TimeSpan.FromSeconds(20), ct);
            if (!createOk)
                return (false, DescribeLogmanError(createErr), 0);

            var (dxgOk, dxgErr) = await RunProcessAsync(LogmanPath, $"update trace \"{SessionName}\" -p \"{DxgKrnlProvider}\" -ets", TimeSpan.FromSeconds(15), ct);
            // Dwm-Core is best-effort - some Windows builds don't expose it as a standalone
            // provider name; DxgKrnl alone is still enough for #250's frame-time capture, so a
            // failure here doesn't abort the whole sample.
            await RunProcessAsync(LogmanPath, $"update trace \"{SessionName}\" -p \"{DwmCoreProvider}\" -ets", TimeSpan.FromSeconds(15), ct, ignoreExitCode: true);

            if (!dxgOk)
            {
                await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);
                return (false, DescribeLogmanError(dxgErr), 0);
            }

            try
            {
                await Task.Delay(window, ct);
            }
            finally
            {
                await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);
            }

            if (!File.Exists(etl))
                return (false, "The trace didn't produce an output file.", 0);

            var (convOk, convErr) = await RunProcessAsync(TracerptPath, $"\"{etl}\" -o \"{xml}\" -of XML -y", TimeSpan.FromSeconds(60), ct);
            if (!convOk || !File.Exists(xml))
                return (false, $"tracerpt couldn't convert the trace: {convErr}", 0);

            int parsed = ParseAndIngest(xml);
            string message = parsed == 0
                ? "Trace captured, but no recognizable present/frame events could be parsed from it - nothing may have been actively rendering, or this Windows build's DxgKrnl schema doesn't match what's parsed for."
                : $"{parsed} present/GPU-packet event(s) parsed from the last {window.TotalSeconds:0}s sample.";
            return (true, message, parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Present-monitor capture failed: {ex.Message}", 0);
        }
        finally
        {
            try { if (File.Exists(etl)) File.Delete(etl); } catch { /* best-effort cleanup */ }
            try { if (File.Exists(xml)) File.Delete(xml); } catch { /* best-effort cleanup */ }
        }
    }

    private static string DescribeLogmanError(string raw)
    {
        if (raw.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return "Access denied starting the trace - this needs administrator rights (the app should already be elevated; if this appears, present monitoring can't run).";
        if (raw.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "A trace session was already running and couldn't be replaced.";
        if (raw.Contains("not found", StringComparison.OrdinalIgnoreCase) || raw.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return $"Microsoft-Windows-DxgKrnl isn't available as an ETW provider on this system: {raw}";
        return string.IsNullOrWhiteSpace(raw) ? "Couldn't start the present-monitor trace." : $"Couldn't start the present-monitor trace: {raw}";
    }

    private int ParseAndIngest(string xmlPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(xmlPath); }
        catch { return 0; }

        int parsed = 0;
        foreach (var ev in ExtractEvents(doc))
        {
            if (ev.Pid <= 4) continue; // System/Idle pid noise, never a real render process

            bool isPresentLike = ev.TaskHint.Contains("Present", StringComparison.OrdinalIgnoreCase) ||
                                  ev.TaskHint.Contains("Flip", StringComparison.OrdinalIgnoreCase) ||
                                  ev.TaskHint.Contains("VSync", StringComparison.OrdinalIgnoreCase) ||
                                  ev.TaskHint.Contains("Blit", StringComparison.OrdinalIgnoreCase);
            bool isQueuePacket = ev.TaskHint.Contains("QueuePacket", StringComparison.OrdinalIgnoreCase) ||
                                  ev.TaskHint.Contains("DmaPacket", StringComparison.OrdinalIgnoreCase) ||
                                  ev.TaskHint.Contains("Packet", StringComparison.OrdinalIgnoreCase);
            bool isPreempt = ev.TaskHint.Contains("Preempt", StringComparison.OrdinalIgnoreCase);

            if (!isPresentLike && !isQueuePacket && !isPreempt) continue;

            if (!_pidToName.ContainsKey(ev.Pid))
                _pidToName[ev.Pid] = ProcessNameLookup.TryGetProcessName(ev.Pid) ?? $"pid {ev.Pid}";

            if (isPresentLike)
            {
                IngestPresent(ev);
                parsed++;
            }
            else
            {
                IngestGpuPacket(ev, isPreempt);
                parsed++;
            }
        }
        return parsed;
    }

    private void IngestPresent(RawPresentEvent ev)
    {
        if (ev.TimeUtc is { } t)
        {
            if (!_frameTimesUtcByPid.TryGetValue(ev.Pid, out var list))
            {
                list = new List<DateTime>();
                _frameTimesUtcByPid[ev.Pid] = list;
            }
            list.Add(t);
        }

        // #252: best-effort present-mode classification - see ClassifyPresentMode's remarks.
        string? mode = ClassifyPresentMode(ev.Fields);
        if (mode is not null) _presentModeByPid[ev.Pid] = mode;
    }

    /// <summary>#252: classifies Hardware Independent Flip / Hardware Composed Flip / Composed
    /// Flip / Composed Blit from whatever field this Windows build's DxgKrnl schema happens to
    /// expose. This is a genuinely undocumented part of the schema (unlike System/Execution/
    /// ProcessID or System/TimeCreated, which are stable/documented) - the classic Present-tracking
    /// tools (PresentMon and friends) resolve this from internal, version-pinned struct offsets
    /// they update per Windows release, which this app deliberately doesn't take on (see the class
    /// remarks on why raw ETW parsing here stays tolerant/best-effort). This scans every captured
    /// field's name for "Mode"/"Flags"/"Model" and pattern-matches known substrings in its value;
    /// if nothing matches on this Windows build, the row legitimately reports "Unknown" per
    /// CLAUDE.md's "degrade to Unknown, never guess" rule - that's an expected, documented partial
    /// outcome for this item, not a parsing bug.</summary>
    private static string? ClassifyPresentMode(Dictionary<string, string> fields)
    {
        foreach (var (key, value) in fields)
        {
            if (!key.Contains("Mode", StringComparison.OrdinalIgnoreCase) &&
                !key.Contains("Flags", StringComparison.OrdinalIgnoreCase) &&
                !key.Contains("Model", StringComparison.OrdinalIgnoreCase))
                continue;

            bool flip = value.Contains("FLIP", StringComparison.OrdinalIgnoreCase);
            bool composed = value.Contains("COMPOS", StringComparison.OrdinalIgnoreCase);
            bool independent = value.Contains("INDEPENDENT", StringComparison.OrdinalIgnoreCase);
            bool hw = value.Contains("HW", StringComparison.OrdinalIgnoreCase) || value.Contains("HARDWARE", StringComparison.OrdinalIgnoreCase);
            bool blit = value.Contains("BLT", StringComparison.OrdinalIgnoreCase) || value.Contains("BLIT", StringComparison.OrdinalIgnoreCase);

            if (flip && independent) return "Hardware Independent Flip";
            if (flip && composed && hw) return "Hardware Composed Flip";
            if (flip && composed) return "Composed Flip";
            if (blit) return "Composed Blit";
        }
        return null;
    }

    private void IngestGpuPacket(RawPresentEvent ev, bool isPreempt)
    {
        double? durationUs = null;
        foreach (var key in DurationFieldCandidates)
        {
            if (ev.Fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var raw))
            {
                durationUs = NormalizeToMicroseconds(raw);
                break;
            }
        }

        // #259: only surface genuinely long packets, or any preemption event regardless of
        // duration (preemption is inherently notable - it means the scheduler bumped this packet
        // for something else).
        if (!isPreempt && durationUs is not (> LongPacketThresholdUs and < 10_000_000)) return;

        string name = _pidToName.TryGetValue(ev.Pid, out var n) ? n : $"pid {ev.Pid}";
        _gpuStalls.Insert(0, new GpuStallRow
        {
            Timestamp = DateTime.Now,
            ProcessName = name,
            Kind = isPreempt ? "Preemption" : "Long GPU packet",
            DurationUs = durationUs,
        });
        while (_gpuStalls.Count > 50) _gpuStalls.RemoveAt(_gpuStalls.Count - 1);
    }

    /// <summary>See DpcLatencyService.NormalizeToMicroseconds's remarks - same unit-normalization
    /// heuristic (the field's actual unit isn't documented), duplicated here rather than shared
    /// since the two services' plausible-range constants differ slightly.</summary>
    private static double NormalizeToMicroseconds(double raw)
    {
        if (raw is > 0 and < 10_000_000) return raw;
        double asHundredNs = raw / 10.0;
        return asHundredNs is > 0 and < 10_000_000 ? asHundredNs : raw;
    }

    private sealed record RawPresentEvent(int Pid, DateTime? TimeUtc, string TaskHint, Dictionary<string, string> Fields);

    /// <summary>Flattens each &lt;Event&gt; into pid/timestamp/task-hint plus a name/value field
    /// map, the same tolerant approach DpcLatencyService.ExtractEventFields uses - except this also
    /// pulls the documented System/Execution/@ProcessID and System/TimeCreated/@SystemTime
    /// attributes tracerpt's XML renders for any manifest-based provider (unlike the classic MOF
    /// schema DpcLatencyService parses, these two are a stable, public contract - see the class
    /// remarks).</summary>
    private static IEnumerable<RawPresentEvent> ExtractEvents(XDocument doc)
    {
        var eventElements = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "Event", StringComparison.OrdinalIgnoreCase));

        foreach (var ev in eventElements)
        {
            var system = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "System");

            int pid = 0;
            DateTime? timeUtc = null;
            if (system is not null)
            {
                var exec = system.Elements().FirstOrDefault(e => e.Name.LocalName == "Execution");
                var pidAttr = exec?.Attribute("ProcessID") ?? exec?.Attribute("ProcessId");
                if (pidAttr is not null && int.TryParse(pidAttr.Value, out var p)) pid = p;

                var timeCreated = system.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated");
                var sysTimeAttr = timeCreated?.Attribute("SystemTime");
                if (sysTimeAttr is not null &&
                    DateTime.TryParse(sysTimeAttr.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    timeUtc = t;
            }

            // tracerpt resolves the Task/Opcode into a human-readable name in RenderingInfo when it
            // has message-table strings to do so; fall back to the raw numeric System/Task/Opcode
            // when it doesn't (still useful for the plain substring matching this class does).
            var rendering = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "RenderingInfo");
            string taskText = rendering?.Elements().FirstOrDefault(e => e.Name.LocalName == "Task")?.Value
                ?? system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Task")?.Value
                ?? string.Empty;
            string opcodeText = rendering?.Elements().FirstOrDefault(e => e.Name.LocalName == "Opcode")?.Value
                ?? system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Opcode")?.Value
                ?? string.Empty;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var eventData = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData");
            if (eventData is not null)
            {
                foreach (var data in eventData.Elements().Where(e => e.Name.LocalName == "Data"))
                {
                    var name = data.Attribute("Name")?.Value;
                    if (!string.IsNullOrEmpty(name)) fields.TryAdd(name, data.Value);
                }
            }

            yield return new RawPresentEvent(pid, timeUtc, $"{taskText} {opcodeText}".Trim(), fields);
        }
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/> - notably, the local copy
    /// this replaces never killed a timed-out child at all; the shared runner kills the whole
    /// process tree. External cancellation still rethrows; a plain timeout reads (false, "timed
    /// out"), and ignoreExitCode stays for the tools whose exit codes can't be trusted.</summary>
    private static async Task<(bool Ok, string Output)> RunProcessAsync(string exe, string args, TimeSpan timeout, CancellationToken ct, bool ignoreExitCode = false)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, args, (int)timeout.TotalMilliseconds, ct);
            if (exitCode is null)
            {
                ct.ThrowIfCancellationRequested();
                return (false, "timed out");
            }
            return (ignoreExitCode || exitCode == 0, output.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>#252: a short "what latency does this present mode add" lookup, the same register as
/// KnownOffenderDriverLookup - informational only, never a diagnosis.</summary>
public static class PresentModeLookup
{
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hardware Independent Flip"] = "Lowest-latency path - the app's own buffer flips directly to the display with no compositor copy.",
        ["Hardware Composed Flip"] = "DWM composes via hardware overlay planes - still a low added-latency path, close to independent flip.",
        ["Composed Flip"] = "DWM copies the app's buffer into the desktop composition each frame - typically adds roughly one frame of latency.",
        ["Composed Blit"] = "Legacy GDI-style copy path through the desktop - the highest added-latency present mode, common for older or exclusive-fullscreen-incompatible apps.",
    };

    public static string Describe(string mode) =>
        Notes.TryGetValue(mode, out var note) ? note : "Not enough detail was captured in this trace to classify the present mode on this Windows build.";
}
