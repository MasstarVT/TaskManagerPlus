using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #279 (ETW deep mode): per-process, per-faulting-file hard-fault attribution via
/// Microsoft-Windows-Kernel-Memory's hard-fault events - the same "known tool, shelled out to
/// logman+tracerpt, output tolerantly parsed" shape DpcLatencyService already establishes for this
/// tab's DPC/ISR measurement session (#201-214), followed closely here rather than re-deriving a
/// second ETW-capture pattern.
///
/// Unlike DpcLatencyService's classic MOF "Windows Kernel Trace" provider (a reserved singleton
/// that can only run under the fixed name "NT Kernel Logger"), Kernel-Memory is a modern
/// manifest-based provider, so this session uses an ordinary name of its own. The capture enables
/// the provider with a keyword mask of 0 - per documented ETW semantics, MatchAnyKeyword=0 means no
/// keyword filtering at all, i.e. every event the provider emits (hard faults included), rather
/// than gambling on a specific keyword bitmask this app has no way to fully verify is stable across
/// Windows builds/SKUs. The XML tracerpt renders is filtered down to hard-fault-shaped events the
/// same tolerant way DpcLatencyService.ParseAndIngest filters DPC/ISR events: by an element/task/
/// opcode name containing "HardFault", then a best-effort field-name search for the process id and
/// faulting file name (which the event schema does carry, unlike DPC/ISR's routine address). A
/// Windows build where none of that matches just parses zero events - shown as an honest "no
/// recognizable hard-fault events" message, per this app's "degrade to hidden, never fabricate"
/// rule, never a fabricated row.
///
/// Start/Stop-gated (never runs on its own) per CLAUDE.md's on-demand rule for anything ETW-based -
/// the always-on #278/#279-fallback figures above cover the cheap always-available case.
/// </summary>
public sealed class HardFaultEtwService
{
    private const string SessionName = "TMPlus-HardFaultTrace";
    private const string ProviderName = "Microsoft-Windows-Kernel-Memory";

    private static readonly string LogmanPath = Path.Combine(Environment.SystemDirectory, "logman.exe");
    private static readonly string TracerptPath = Path.Combine(Environment.SystemDirectory, "tracerpt.exe");

    public bool ToolsAvailable { get; } = File.Exists(LogmanPath) && File.Exists(TracerptPath);

    private sealed class Accum { public int Count; }
    private readonly Dictionary<(int Pid, string File), Accum> _byPidFile = new();
    private readonly Dictionary<int, string> _pidNames = new();

    /// <summary>Clears all accumulated state for a new session - called from
    /// ResponsivenessViewModel.StartHardFaultEtwAsync, same shape as DpcLatencyService.ResetSession.</summary>
    public void ResetSession()
    {
        _byPidFile.Clear();
        _pidNames.Clear();
    }

    public List<HardFaultEtwRow> BuildRows() =>
        _byPidFile.Select(kv => new HardFaultEtwRow
        {
            Pid = kv.Key.Pid,
            ProcessName = _pidNames.TryGetValue(kv.Key.Pid, out var n) ? n : $"pid {kv.Key.Pid}",
            FileName = kv.Key.File,
            Count = kv.Value.Count,
        }).OrderByDescending(r => r.Count).ToList();

    /// <summary>Runs one capture-convert-parse cycle. Never throws except on cancellation - every
    /// other failure comes back as Ok=false/EventsParsed=0 with a plain-English Message, same
    /// contract as DpcLatencyService.SampleOnceAsync.</summary>
    public async Task<(bool Ok, string Message, int EventsParsed)> SampleOnceAsync(TimeSpan window, CancellationToken ct)
    {
        if (!ToolsAvailable)
            return (false, "logman.exe/tracerpt.exe weren't found on this system - hard-fault ETW capture isn't available.", 0);

        string dir = Path.Combine(AppPaths.SettingsDirectory, "ResponsivenessTraces");
        try { Directory.CreateDirectory(dir); }
        catch { return (false, "Couldn't create a temp folder for the trace.", 0); }

        string etl = Path.Combine(dir, $"hardfault_{Guid.NewGuid():N}.etl");
        string xml = Path.Combine(dir, $"hardfault_{Guid.NewGuid():N}.xml");

        try
        {
            // Best-effort cleanup of a stale session from a previous crashed/killed run.
            await RunProcessAsync(LogmanPath, $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(15), CancellationToken.None, ignoreExitCode: true);

            var (startOk, startErr) = await RunProcessAsync(LogmanPath,
                $"create trace \"{SessionName}\" -p \"{ProviderName}\" 0x0 0x0 -o \"{etl}\" -ets",
                TimeSpan.FromSeconds(20), ct);
            if (!startOk)
                return (false, DescribeLogmanError(startErr), 0);

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
                ? "Trace captured, but no recognizable hard-fault events could be parsed from it on this Windows build."
                : $"{parsed} hard-fault events parsed from the last {window.TotalSeconds:0}s sample.";
            return (true, message, parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Hard-fault capture failed: {ex.Message}", 0);
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
            return "Access denied starting the trace - this needs administrator rights (the app should already be elevated; if this appears, hard-fault ETW capture can't run).";
        if (raw.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "A trace session was already running and couldn't be replaced.";
        return string.IsNullOrWhiteSpace(raw) ? "Couldn't start the trace." : $"Couldn't start the trace: {raw}";
    }

    private int ParseAndIngest(string xmlPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(xmlPath); }
        catch { return 0; }

        int parsed = 0;
        foreach (var fields in ExtractEventFields(doc))
        {
            string kindHint = fields.TryGetValue("__ElementName", out var en) ? en
                : fields.TryGetValue("EventName", out var evn) ? evn
                : fields.TryGetValue("Task", out var tk) ? tk
                : fields.TryGetValue("Opcode", out var op) ? op
                : string.Empty;
            if (!kindHint.Contains("HardFault", StringComparison.OrdinalIgnoreCase)) continue;

            int pid = 0;
            foreach (var key in new[] { "ProcessId", "PID", "ProcessID" })
            {
                if (fields.TryGetValue(key, out var v) && int.TryParse(v, out pid) && pid > 0) break;
                pid = 0;
            }
            if (pid <= 0) continue;

            string fileName = string.Empty;
            foreach (var kv in fields)
            {
                if (kv.Key.Contains("FileName", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("File", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = kv.Value;
                    break;
                }
            }

            var key2 = (pid, fileName);
            if (!_byPidFile.TryGetValue(key2, out var acc)) { acc = new Accum(); _byPidFile[key2] = acc; }
            acc.Count++;

            if (!_pidNames.ContainsKey(pid))
            {
                try { using var p = Process.GetProcessById(pid); _pidNames[pid] = p.ProcessName; }
                catch { _pidNames[pid] = $"pid {pid}"; }
            }

            parsed++;
        }
        return parsed;
    }

    private static IEnumerable<Dictionary<string, string>> ExtractEventFields(XDocument doc)
    {
        var eventElements = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Event", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (eventElements.Count == 0)
        {
            eventElements = doc.Descendants()
                .Where(e => e.Name.LocalName.Contains("HardFault", StringComparison.OrdinalIgnoreCase))
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
