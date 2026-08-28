using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #649/#650/#651: `powercfg /sleepstudy` (alias `/systempowerreport`) report ingestion, plus
/// cross-session offender ranking. Both Modern Standby and legacy-S3 machines are routed here -
/// `/systemsleepdiagnostics`, the command #651 was originally written against, has been deprecated
/// on every currently-supported Windows build and simply redirects to `/systempowerreport` (the
/// same report `/sleepstudy` produces) with no data of its own; this was confirmed live via
/// `powercfg /systemsleepdiagnostics` on a real (legacy-S3, non-Modern-Standby) dev machine, which
/// printed only "The system sleep diagnostics report has been deprecated and replaced with the
/// system power report. Please use the command 'powercfg /systempowerreport' instead." and exited
/// with no report at all. #651's routing is therefore: try the legacy-named command first on a
/// non-Modern-Standby system (so a Windows build old enough to still honor it separately gets that
/// report), detect the deprecation message, and fall back to `/sleepstudy /xml` either way - the
/// status text still tells the two machine types apart so the panel doesn't look identical between
/// them for no reason.
///
/// Unlike `/batteryreport`'s XML (undocumented but stable field names, per BatteryReportService's
/// own remarks), the sleepstudy/system-power-report XML schema has no public documentation at all.
/// Parsing here is therefore deliberately loose - elements are matched by local-name *substring*
/// (e.g. anything containing "Session"), not an exact path - and any session or field this scan
/// can't confidently recognize is left out rather than guessed. A report that doesn't yield any
/// recognizable session data degrades to an empty list plus a clear status note, never a fabricated
/// row.
/// </summary>
public static class SleepStudyService
{
    public static async Task<(List<SleepStudySession> Sessions, List<SleepStudyOffender> RankedOffenders, string StatusText)> RunAsync(bool isModernStandby)
    {
        string legacyNote = string.Empty;
        if (!isModernStandby)
        {
            try
            {
                var (legacyOutput, _) = await RunProcessAsync("powercfg.exe", "/systemsleepdiagnostics", 5000);
                if (legacyOutput.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                    legacyNote = "powercfg /systemsleepdiagnostics is deprecated on this Windows build (its own message points to /systempowerreport) - ";
            }
            catch { /* best-effort probe only - falls straight through to /sleepstudy below either way */ }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus");
        string tempFile = Path.Combine(tempDir, $"sleepstudy-{Guid.NewGuid():N}.xml");
        try
        {
            Directory.CreateDirectory(tempDir);
            var (output, _) = await RunProcessAsync("powercfg.exe", $"/sleepstudy /xml /output \"{tempFile}\"", 30000);
            if (!File.Exists(tempFile))
            {
                string reason = output.Contains("administrator", StringComparison.OrdinalIgnoreCase)
                    ? "powercfg /sleepstudy needs administrator privileges (this app should already be elevated - try relaunching it)."
                    : "powercfg /sleepstudy didn't produce a report on this system (it needs at least one sleep/resume cycle in the last few days to have anything to report on).";
                return (new List<SleepStudySession>(), new List<SleepStudyOffender>(), reason);
            }

            string xml = await File.ReadAllTextAsync(tempFile);
            var sessions = ParseSessions(xml);
            var ranked = RankOffenders(sessions);

            string reportLabel = isModernStandby ? "Modern Standby diagnostic report" : $"{legacyNote}system power report";
            string status = sessions.Count == 0
                ? $"{reportLabel} loaded, but no recognizable session data was found in it (its XML schema isn't publicly documented by Microsoft, so this parser is best-effort)."
                : $"{reportLabel}: {sessions.Count} session(s) parsed.";
            return (sessions, ranked, status);
        }
        catch (Exception ex)
        {
            return (new List<SleepStudySession>(), new List<SleepStudyOffender>(), $"Couldn't read the sleep report: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private static List<SleepStudySession> ParseSessions(string xml)
    {
        var result = new List<SleepStudySession>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return result; }

        var sessionElements = doc.Descendants()
            .Where(e => e.Name.LocalName.Contains("Session", StringComparison.OrdinalIgnoreCase) && e.HasElements)
            .ToList();

        foreach (var el in sessionElements)
        {
            var start = FindDate(el, "Start", "PeriodStart", "SessionStart");
            var duration = ParseDurationOrSeconds(FindValue(el, "Duration"));
            double? lowPowerPercent = FindPercent(el, "ConnectedStandby", "LowPower", "Idle", "Efficiency");
            double? drainPercent = FindPercent(el, "Drain", "Discharge", "EnergyChange", "BatteryDrop", "Battery");

            var offenders = new List<SleepStudyOffender>();
            foreach (var offEl in el.Descendants().Where(d =>
                         d.Name.LocalName.Contains("Activator", StringComparison.OrdinalIgnoreCase) ||
                         d.Name.LocalName.Contains("Offender", StringComparison.OrdinalIgnoreCase) ||
                         d.Name.LocalName.Contains("Contributor", StringComparison.OrdinalIgnoreCase)))
            {
                string? name = FindValue(offEl, "Name");
                if (string.IsNullOrWhiteSpace(name) && !offEl.HasElements) name = offEl.Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                string category = FindValue(offEl, "Type") ?? FindValue(offEl, "Category") ?? string.Empty;
                offenders.Add(new SleepStudyOffender { Name = Truncate(name!, 120), Category = category });
            }

            if (start is null && duration is null && offenders.Count == 0) continue; // nothing usable in this element

            result.Add(new SleepStudySession
            {
                Start = start,
                Duration = duration,
                LowPowerPercent = lowPowerPercent,
                DrainPercent = drainPercent,
                TopOffenders = offenders,
            });
        }

        // Session-like elements can legitimately nest (e.g. a report-level wrapper whose own name
        // also contains "Session"), which would otherwise show as duplicate rows for the same
        // underlying session - de-duplicate by (Start, Duration) rather than trying to guess the
        // exact containment shape of an undocumented schema.
        return result
            .GroupBy(s => (s.Start, s.Duration))
            .Select(g => g.OrderByDescending(s => s.TopOffenders.Count).First())
            .OrderByDescending(s => s.Start ?? DateTime.MinValue)
            .ToList();
    }

    private static List<SleepStudyOffender> RankOffenders(List<SleepStudySession> sessions)
    {
        var byName = new Dictionary<string, SleepStudyOffender>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            foreach (var offender in session.TopOffenders.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (!byName.TryGetValue(offender.Name, out var agg))
                    byName[offender.Name] = agg = new SleepStudyOffender { Name = offender.Name, Category = offender.Category };
                agg.SessionCount++;
            }
        }
        return byName.Values.OrderByDescending(o => o.SessionCount).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindValue(XElement parent, string nameContains)
    {
        var el = parent.Descendants().FirstOrDefault(d => d.Name.LocalName.Contains(nameContains, StringComparison.OrdinalIgnoreCase) && !d.HasElements);
        var v = el?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static DateTime? FindDate(XElement parent, params string[] nameHints)
    {
        foreach (var hint in nameHints)
        {
            var raw = FindValue(parent, hint);
            if (raw is not null && BatteryReportService.TryParseFlexibleDate(raw, out var dt)) return dt;
        }
        return null;
    }

    private static double? FindPercent(XElement parent, params string[] nameHints)
    {
        foreach (var hint in nameHints)
        {
            var raw = FindValue(parent, hint);
            if (raw is not null && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100)
                return v;
        }
        return null;
    }

    private static readonly Regex Iso8601DurationRegex = new(@"^PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static TimeSpan? ParseDurationOrSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.StartsWith("P", StringComparison.OrdinalIgnoreCase))
        {
            var match = Iso8601DurationRegex.Match(raw);
            if (!match.Success) return null;
            double hours = match.Groups[1].Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            double minutes = match.Groups[2].Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            double seconds = match.Groups[3].Success ? double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var num) && num >= 0)
        {
            // A bare number's unit isn't specified by the (undocumented) schema - milliseconds is
            // the common convention for Windows diagnostic-report durations, so anything only
            // plausible as milliseconds (a raw-seconds session over ~1.1 days would be implausibly
            // long) is treated as such.
            return num > 100_000 ? TimeSpan.FromMilliseconds(num) : TimeSpan.FromSeconds(num);
        }
        return null;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern PowerPlanService.RunCapturedAsync
    /// established (see its remarks). Duplicated here rather than shared, matching this app's
    /// existing convention of each shelled-out-tool service owning its own small copy.</summary>
    private static async Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
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
}
