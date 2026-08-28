using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #201/#202/#203/#207/#208/#209/#213/#214: the Responsiveness tab's core DPC/ISR-by-driver
/// engine - the single feature this whole domain hangs off.
///
/// Implementation choice: this project deliberately doesn't take a dependency on the
/// Microsoft.Diagnostics.Tracing.TraceEvent NuGet package for a real-time ETW reader, matching
/// CLAUDE.md's "prefer a known Windows tool over raw interop/an extra dependency" rule one level
/// further - every other ETW-adjacent feature in this app (#210's offline capture) already shells
/// out to an in-box tool rather than linking a tracing library, and TraceEvent pulls in a fairly
/// large dependency graph for what's used here. Instead, a "measurement session" (Start/Stop, see
/// ResponsivenessViewModel) repeatedly takes short (a few seconds) snapshot samples: start the
/// classic "Windows Kernel Trace" MOF provider via logman with the DPC+ISR keywords, wait, stop it,
/// convert the .etl to XML via tracerpt, and parse the DPC/ISR events out of that XML - the same
/// "known tool, shelled out, output parsed" tradeoff schtasks.exe/sc.exe/vssadmin.exe/defrag.exe
/// already take elsewhere in this app. This is periodic sampling, not a continuous streaming
/// reader - acceptable per this feature's own scope note, and it keeps the whole thing working
/// without touching NuGet.
///
/// The classic "Windows Kernel Trace" provider is a reserved singleton session and can *only* be
/// created under the exact name "NT Kernel Logger" (confirmed against a real logman invocation
/// during development: any other session name is rejected outright with "The session name
/// provided is invalid", before logman even gets to an access check) - this is a genuine Windows
/// quirk, not a typo.
///
/// The classic MOF-based DPC/ISR event schema tracerpt renders to XML isn't publicly documented
/// the way a manifest-based provider's fields are (there's no stable field-name contract to code
/// against), so parsing here is deliberately tolerant: every element/attribute in an event is
/// flattened into a name/value map, a routine address is taken from whichever field's name
/// contains "routine" (falling back to the first hex-pointer-shaped value in the event), and a
/// duration is taken from a short list of candidate field names with a unit-normalization
/// heuristic (see NormalizeToMicroseconds). If a Windows build's actual field names don't match
/// any of these candidates, samples legitimately parse zero events - per this app's "degrade to
/// Unknown/hidden, never fabricate" rule, that shows up as an empty grid with an explanatory
/// status message, not invented numbers.
/// </summary>
public sealed class DpcLatencyService
{
    // The classic kernel logger's one fixed, reserved session name - see the class remarks.
    private const string SessionName = "NT Kernel Logger";

    private static readonly string LogmanPath = Path.Combine(Environment.SystemDirectory, "logman.exe");
    private static readonly string TracerptPath = Path.Combine(Environment.SystemDirectory, "tracerpt.exe");

    private static readonly Regex HexPointerRegex = new(@"^0x[0-9A-Fa-f]{6,16}$", RegexOptions.Compiled);
    private static readonly string[] DurationFieldCandidates = { "Duration", "ElapsedTime", "TimeElapsed", "DpcTime", "IsrTime", "RunningTime" };

    public bool ToolsAvailable { get; } = File.Exists(LogmanPath) && File.Exists(TracerptPath);

    /// <summary>#214's audio-glitch cutoff, reused as #209's spike threshold too (one knob for
    /// both, kept simple for v1) - default 1000us, roughly a dropout's worth of buffer at 48kHz.</summary>
    public double AudioGlitchThresholdUs { get; set; } = 1000;

    private sealed class DriverAccum
    {
        public int Count;
        public double TotalUs;
        public double MaxUs;
        public int TimerCount;
        public int DeviceCount;
        public readonly List<double> Samples = new();
    }

    private readonly Dictionary<string, DriverAccum> _dpcByDriver = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DriverAccum> _isrByDriver = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DpcSpikeEvent> _spikes = new();
    private readonly List<double> _allDpcSamplesUs = new();

    private DateTime _sessionStart;
    private double _sessionTotalDpcUs;

    public double HighestDpcUs { get; private set; }
    public string HighestDpcDriver { get; private set; } = string.Empty;
    public int AudioGlitchCount { get; private set; }
    public double RollingAvgUs => _allDpcSamplesUs.Count == 0 ? 0 : _allDpcSamplesUs.Average();
    public double RollingP99Us => Percentile(_allDpcSamplesUs, 0.99);
    public IReadOnlyList<DpcSpikeEvent> RecentSpikes => _spikes;

    /// <summary>#213: clears all accumulated state and marks a new session start - called once
    /// from ResponsivenessViewModel.StartMeasurementAsync.</summary>
    public void ResetSession()
    {
        _dpcByDriver.Clear();
        _isrByDriver.Clear();
        _spikes.Clear();
        _allDpcSamplesUs.Clear();
        _sessionStart = DateTime.Now;
        _sessionTotalDpcUs = 0;
        HighestDpcUs = 0;
        HighestDpcDriver = string.Empty;
        AudioGlitchCount = 0;
    }

    public List<DriverDpcRow> BuildDriverDpcRows(Func<string, (string? Hint, DriverIdentityInfo? Identity)> enrich)
    {
        var rows = new List<DriverDpcRow>();
        foreach (var (name, a) in _dpcByDriver)
        {
            var (hint, identity) = enrich(name);
            rows.Add(new DriverDpcRow
            {
                DriverName = name,
                EventCount = a.Count,
                TotalTimeUs = a.TotalUs,
                MaxTimeUs = a.MaxUs,
                TimerDpcCount = a.TimerCount,
                DeviceDpcCount = a.DeviceCount,
                KnownOffenderHint = hint,
                Version = identity?.Version ?? string.Empty,
                DriverDate = identity?.DriverDate ?? string.Empty,
                Provider = identity?.Provider ?? string.Empty,
                Signer = identity?.Signer ?? string.Empty,
                IsOutdated = identity?.IsOutdated ?? false,
                IdentityText = identity is not null && identity.Provider.Length > 0
                    ? $"{name} — {identity.Provider}{(identity.Version.Length > 0 ? $" v{identity.Version}" : "")}"
                    : name,
            });
        }
        return rows.OrderByDescending(r => r.TotalTimeUs).ToList();
    }

    public List<DriverIsrRow> BuildDriverIsrRows() =>
        _isrByDriver.Select(kv => new DriverIsrRow
        {
            DriverName = kv.Key,
            Count = kv.Value.Count,
            TotalTimeUs = kv.Value.TotalUs,
            MaxTimeUs = kv.Value.MaxUs,
        }).OrderByDescending(r => r.TotalTimeUs).ToList();

    /// <summary>#213: builds the final min/avg/max/p99-per-driver + total-DPC-time-as-%-of-wall-
    /// clock summary for the just-stopped measurement session.</summary>
    public MeasurementSessionSummary BuildSummary()
    {
        var wall = DateTime.Now - _sessionStart;
        var perDriver = _dpcByDriver.Select(kv => new DriverSessionStat
        {
            DriverName = kv.Key,
            MinUs = kv.Value.Samples.Count == 0 ? 0 : kv.Value.Samples.Min(),
            AvgUs = kv.Value.Samples.Count == 0 ? 0 : kv.Value.Samples.Average(),
            MaxUs = kv.Value.MaxUs,
            P99Us = Percentile(kv.Value.Samples, 0.99),
        }).OrderByDescending(d => d.MaxUs).ToList();

        double wallUs = wall.TotalMicroseconds;
        return new MeasurementSessionSummary
        {
            StartedAt = _sessionStart,
            Duration = wall,
            TotalDpcTimeUs = _sessionTotalDpcUs,
            DpcTimePercentOfWallClock = wallUs > 0 ? _sessionTotalDpcUs / wallUs * 100.0 : 0,
            PerDriver = perDriver,
        };
    }

    /// <summary>Runs one capture-convert-parse cycle. Never throws except on cancellation - every
    /// other failure (tool missing, access denied, malformed trace, unrecognized event schema)
    /// comes back as Ok=false/EventsParsed=0 with a plain-English Message.</summary>
    public async Task<(bool Ok, string Message, int EventsParsed)> SampleOnceAsync(TimeSpan window, CancellationToken ct)
    {
        if (!ToolsAvailable)
            return (false, "logman.exe/tracerpt.exe weren't found on this system - DPC/ISR capture isn't available.", 0);

        string dir = Path.Combine(AppPaths.SettingsDirectory, "ResponsivenessTraces");
        try { Directory.CreateDirectory(dir); }
        catch { return (false, "Couldn't create a temp folder for the trace.", 0); }

        string etl = Path.Combine(dir, $"sample_{Guid.NewGuid():N}.etl");
        string xml = Path.Combine(dir, $"sample_{Guid.NewGuid():N}.xml");

        try
        {
            // Best-effort cleanup of a stale session left over from a previous crashed/killed run -
            // errors ignored, and deliberately not tied to the caller's token so it always runs.
            await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);

            var (startOk, startErr) = await RunProcessAsync(LogmanPath,
                $"create trace \"{SessionName}\" -p \"Windows Kernel Trace\" \"(dpc,isr)\" -o \"{etl}\" -ets",
                TimeSpan.FromSeconds(20), ct);
            if (!startOk)
                return (false, DescribeLogmanError(startErr), 0);

            try
            {
                await Task.Delay(window, ct);
            }
            finally
            {
                // Always stop the session we just started, even on cancel - CancellationToken.None
                // so a cancelled measurement session still cleanly tears down the kernel trace
                // rather than leaving "NT Kernel Logger" running for the next sample to trip over.
                await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);
            }

            if (!File.Exists(etl))
                return (false, "The trace didn't produce an output file.", 0);

            var (convOk, convErr) = await RunProcessAsync(TracerptPath, $"\"{etl}\" -o \"{xml}\" -of XML -y", TimeSpan.FromSeconds(60), ct);
            if (!convOk || !File.Exists(xml))
                return (false, $"tracerpt couldn't convert the trace: {convErr}", 0);

            int parsed = ParseAndIngest(xml);
            string message = parsed == 0
                ? "Trace captured, but no recognizable DPC/ISR events could be parsed from it on this Windows build."
                : $"{parsed} DPC/ISR events parsed from the last {window.TotalSeconds:0}s sample.";
            return (true, message, parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"DPC capture failed: {ex.Message}", 0);
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
            return "Access denied starting the kernel trace - this needs administrator rights (the app should already be elevated; if this appears, DPC/ISR capture can't run).";
        if (raw.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "A kernel trace session was already running and couldn't be replaced.";
        return string.IsNullOrWhiteSpace(raw) ? "Couldn't start the kernel trace." : $"Couldn't start the kernel trace: {raw}";
    }

    private int ParseAndIngest(string xmlPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(xmlPath); }
        catch { return 0; }

        var moduleMap = DpcModuleMapService.GetModuleMap();
        int parsed = 0;

        foreach (var fields in ExtractEventFields(doc))
        {
            string kindHint = fields.TryGetValue("__ElementName", out var en) ? en
                : fields.TryGetValue("EventName", out var evn) ? evn
                : fields.TryGetValue("Task", out var tk) ? tk
                : fields.TryGetValue("Opcode", out var op) ? op
                : string.Empty;

            bool isDpc = kindHint.Contains("DPC", StringComparison.OrdinalIgnoreCase);
            bool isIsr = !isDpc && kindHint.Contains("ISR", StringComparison.OrdinalIgnoreCase);
            if (!isDpc && !isIsr) continue;

            string? routineHex = fields.FirstOrDefault(kv => kv.Key.Contains("routine", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrEmpty(routineHex))
                routineHex = fields.Values.FirstOrDefault(v => HexPointerRegex.IsMatch(v));
            if (string.IsNullOrEmpty(routineHex) || !TryParseHex(routineHex, out ulong addr)) continue;

            double? durationUs = null;
            foreach (var key in DurationFieldCandidates)
            {
                if (fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var raw))
                {
                    durationUs = NormalizeToMicroseconds(raw);
                    break;
                }
            }
            if (durationUs is not (> 0 and < 1_000_000)) continue; // implausible or missing - skip rather than guess

            string driverName = DpcModuleMapService.ResolveDriverName(moduleMap, addr) ?? $"0x{addr:X} (unresolved)";
            bool isTimer = fields.TryGetValue("Type", out var typeVal) && typeVal.Contains("Timer", StringComparison.OrdinalIgnoreCase);

            IngestOne(isDpc, driverName, durationUs.Value, isTimer);
            parsed++;
        }

        return parsed;
    }

    private void IngestOne(bool isDpc, string driverName, double durationUs, bool isTimer)
    {
        var dict = isDpc ? _dpcByDriver : _isrByDriver;
        if (!dict.TryGetValue(driverName, out var acc))
        {
            acc = new DriverAccum();
            dict[driverName] = acc;
        }
        acc.Count++;
        acc.TotalUs += durationUs;
        acc.MaxUs = Math.Max(acc.MaxUs, durationUs);
        if (acc.Samples.Count < 500) acc.Samples.Add(durationUs); // bounded so p99 stays cheap on a busy driver

        if (!isDpc) return;

        if (isTimer) acc.TimerCount++; else acc.DeviceCount++;

        _sessionTotalDpcUs += durationUs;
        if (_allDpcSamplesUs.Count < 2000) _allDpcSamplesUs.Add(durationUs);
        if (durationUs > HighestDpcUs) { HighestDpcUs = durationUs; HighestDpcDriver = driverName; }
        if (durationUs >= AudioGlitchThresholdUs) AudioGlitchCount++;

        // #209: only for genuinely notable spikes, not every DPC - keeps the list short and the
        // foreground-window lookup (one syscall per spike) cheap.
        if (durationUs >= AudioGlitchThresholdUs)
        {
            var (procName, title) = ForegroundContextService.GetForegroundContext();
            _spikes.Insert(0, new DpcSpikeEvent
            {
                Timestamp = DateTime.Now,
                DriverName = driverName,
                DurationUs = durationUs,
                Kind = "DPC",
                ForegroundContext = string.IsNullOrEmpty(procName) ? "Unknown" : $"{procName} — {title}",
            });
            while (_spikes.Count > 30) _spikes.RemoveAt(_spikes.Count - 1);
        }
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int index = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static bool TryParseHex(string s, out ulong value)
    {
        string h = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return ulong.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>See the class remarks - the captured field's unit isn't documented, so a plausible-
    /// as-microseconds value is used as-is, and an implausibly large one is assumed to be raw
    /// 100ns ticks and divided down. A value that fits neither is left as-is for the caller's own
    /// plausibility check (which then rejects it) - never silently forced into range.</summary>
    private static double NormalizeToMicroseconds(double raw)
    {
        if (raw is > 0 and < 1_000_000) return raw;
        double asHundredNs = raw / 10.0;
        return asHundredNs is > 0 and < 1_000_000 ? asHundredNs : raw;
    }

    private static IEnumerable<Dictionary<string, string>> ExtractEventFields(XDocument doc)
    {
        var eventElements = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Event", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (eventElements.Count == 0)
        {
            // Fallback for a schema that names each event element after its own MOF class instead
            // of a generic "Event" wrapper - treat any DPC/ISR-named element as its own record.
            eventElements = doc.Descendants()
                .Where(e => e.Name.LocalName.Contains("DPC", StringComparison.OrdinalIgnoreCase) ||
                            e.Name.LocalName.Contains("ISR", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var ev in eventElements)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in ev.DescendantsAndSelf().SelectMany(e => e.Attributes()))
                fields.TryAdd(attr.Name.LocalName, attr.Value);
            foreach (var data in ev.Descendants().Where(e => string.Equals(e.Name.LocalName, "Data", StringComparison.OrdinalIgnoreCase)))
            {
                var name = data.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name)) fields.TryAdd(name, data.Value);
            }
            fields["__ElementName"] = ev.Name.LocalName;
            yield return fields;
        }
    }

    private static async Task<(bool Ok, string Output)> RunProcessAsync(string exe, string args, TimeSpan timeout, CancellationToken ct, bool ignoreExitCode = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't start process");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token);

            string combined = (await outTask) + (await errTask);
            bool ok = ignoreExitCode || proc.ExitCode == 0;
            return (ok, combined.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, "timed out");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
