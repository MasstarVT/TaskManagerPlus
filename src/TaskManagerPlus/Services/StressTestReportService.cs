using System.Globalization;
using System.Text;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #700: routes each stress-test run's report through the app's existing CSV/HTML/Markdown
/// reporting system rather than a new, unrelated writer - the CSV trace export below instantiates
/// LoggingService directly (the same class backing the footer's Start/Stop Logging CSV writer,
/// header rotation and all), and the Markdown/HTML summary reports share DiagnosticReportFormatting
/// with SummaryViewModel's #73/#97 diagnostic report (identical look - same dark inline &lt;style&gt;
/// block, same Esc/Sparkline helpers). Run-to-run comparison text ("same test, 12°C hotter and
/// 400 MHz lower than three months ago") is built once here so the Markdown report, the HTML
/// report, and StressTestViewModel's live status line all read identically.
/// </summary>
public static class StressTestReportService
{
    /// <summary>#700: exports the run's full sampled trace as CSV, via LoggingService directly -
    /// the exact class the footer's manual/rolling-buffer logging already writes CSV through, not
    /// a second implementation.</summary>
    public static void ExportTraceCsv(StressTestRunResult result, string path)
    {
        using var logging = new LoggingService();
        logging.Start(path, new List<string>
        {
            "Timestamp", "Temp (C)", "Clock (GHz)", "Package Power (W)", "Throttle (%)",
            "Fan (RPM)", "Rail Voltage (V)", "GPU Utilization (%)", "WHEA events since start", "TDR events since start",
        });

        foreach (var s in result.Trace)
        {
            logging.WriteRow(new List<string>
            {
                s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Num(s.TempC), Num(s.ClockGhz), Num(s.PackagePowerW), Num(s.ThrottlePercent), Num(s.FanRpm), Num(s.RailVoltage), Num(s.GpuUtilizationPercent),
                s.WheaEventsSinceStart.ToString(CultureInfo.InvariantCulture),
                s.TdrEventsSinceStart.ToString(CultureInfo.InvariantCulture),
            });
        }

        logging.Stop();
    }

    public static string BuildRunMarkdown(StressTestRunResult result, StressTestHistoryEntry? previous)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line($"# Task Manager Plus stress test report - {DescribeType(result.TestType)}");
        Line($"Run started {result.StartedAt:F} — duration {FormatDuration(result.ActualDuration)} (requested {FormatDuration(result.RequestedDuration)}).");
        Line();
        Line($"**Result: {(result.Passed ? "PASS" : "FAIL")}**" + (result.Criteria.Aborted ? $" — aborted: {result.Criteria.AbortReason}" : string.Empty));
        Line();

        Line("## Pass/fail criteria");
        Line("| Criterion | Result |");
        Line("|---|---|");
        Line($"| Computation correct (no checksum mismatch) | {CriterionText(result.Criteria.ComputationChecked, result.Criteria.ComputationOk)} |");
        Line($"| No WHEA corrected-error delta | {(result.Criteria.NoWheaDelta ? "Pass" : "Fail")} |");
        Line($"| No display-driver reset (TDR) | {(result.Criteria.NoTdr ? "Pass" : "Fail")} |");
        Line($"| Sustained clock at/above rated base | {CriterionText(result.Criteria.ClockChecked, result.Criteria.SustainedClockAtOrAboveBase)} |");
        Line($"| Peak temperature below throttle point | {(result.Criteria.PeakTempBelowThrottlePoint ? "Pass" : "Fail")} |");
        Line();

        Line("## Summary");
        Line($"- Threads: {result.ThreadCount}");
        Line($"- Peak temperature: {Fmt(result.PeakTempC, "0.#", "°C")}");
        Line($"- Average clock: {Fmt(result.AvgClockGhz, "0.00", " GHz")}");
        Line($"- Peak package power: {Fmt(result.PeakPowerW, "0.#", " W")}");
        Line($"- Peak fan speed: {Fmt(result.PeakFanRpm, "0", " RPM")}");
        Line($"- Safety-abort temperature ceiling used for this run: {result.EffectiveTempCeilingC:0.#}°C" +
             (result.ThrottlePointReferenceC is { } tp ? $" (throttle-point reference: {tp:0.#}°C)" : string.Empty));
        Line();

        if (result.CpuResult is { } cpu)
        {
            Line("## CPU torture test (#695)");
            Line($"- {cpu.ThreadResults.Count} thread(s), {cpu.TotalIterations:N0} total iterations, {(cpu.FaultMessage is null ? "no hard fault" : $"hard fault: {cpu.FaultMessage}")}.");
            Line(cpu.AllThreadsPassed ? "- All threads passed checksum verification." : "- **One or more threads FAILED checksum verification.**");
            var failed = cpu.ThreadResults.Where(t => !t.Passed).ToList();
            if (failed.Count > 0)
            {
                Line();
                Line("| Thread | Expected (hex) | Actual (hex) |");
                Line("|---|---|---|");
                foreach (var t in failed) Line($"| {t.ThreadIndex} | {t.Expected:X16} | {t.Actual:X16} |");
            }
            Line();
        }

        if (result.MemoryResult is { } mem)
        {
            Line("## Memory pattern-verify test (#696)");
            if (mem.Skipped)
            {
                Line($"- Skipped: {mem.SkipReason}");
            }
            else
            {
                Line($"- {mem.BytesTested / 1073741824.0:0.##} GB tested across walking-ones, walking-zeros, and pseudorandom passes.");
                Line(mem.Mismatches.Count == 0 ? "- No mismatches found." : $"- **{mem.Mismatches.Count} mismatch(es) found** (list capped at 50):");
                if (mem.Mismatches.Count > 0)
                {
                    Line();
                    Line("| Pattern | Byte offset | Expected (hex) | Actual (hex) |");
                    Line("|---|---|---|---|");
                    foreach (var m in mem.Mismatches) Line($"| {m.PatternName} | 0x{m.ByteOffset:X} | {m.Expected:X16} | {m.Actual:X16} |");
                }
            }
            Line();
        }

        if (previous is not null)
        {
            Line("## Compared to the previous run of this test");
            Line(BuildComparisonText(result, previous));
        }

        return sb.ToString();
    }

    public static string BuildRunHtml(StressTestRunResult result, StressTestHistoryEntry? previous)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');
        string Esc(string s) => DiagnosticReportFormatting.HtmlEscape(s);

        Line(DiagnosticReportFormatting.HtmlDocumentOpen($"Stress test report - {DescribeType(result.TestType)} - {result.StartedAt:F}"));
        Line($"<h1>Task Manager Plus stress test report — {Esc(DescribeType(result.TestType))}</h1>");
        Line($"<p class=\"muted\">Run started {Esc(result.StartedAt.ToString("F"))} — duration {Esc(FormatDuration(result.ActualDuration))} (requested {Esc(FormatDuration(result.RequestedDuration))})</p>");
        Line($"<p class=\"{(result.Passed ? "ok" : "crit")}\"><b>Result: {(result.Passed ? "PASS" : "FAIL")}</b>" +
             (result.Criteria.Aborted ? $" — aborted: {Esc(result.Criteria.AbortReason ?? string.Empty)}" : string.Empty) + "</p>");

        Line("<h2>Pass/fail criteria</h2><table>");
        Line($"<tr><td>Computation correct</td><td>{CriterionText(result.Criteria.ComputationChecked, result.Criteria.ComputationOk)}</td></tr>");
        Line($"<tr><td>No WHEA corrected-error delta</td><td>{(result.Criteria.NoWheaDelta ? "Pass" : "Fail")}</td></tr>");
        Line($"<tr><td>No display-driver reset (TDR)</td><td>{(result.Criteria.NoTdr ? "Pass" : "Fail")}</td></tr>");
        Line($"<tr><td>Sustained clock at/above base</td><td>{CriterionText(result.Criteria.ClockChecked, result.Criteria.SustainedClockAtOrAboveBase)}</td></tr>");
        Line($"<tr><td>Peak temperature below throttle point</td><td>{(result.Criteria.PeakTempBelowThrottlePoint ? "Pass" : "Fail")}</td></tr>");
        Line("</table>");

        Line("<h2>Trace</h2>");
        var temps = result.Trace.Where(t => t.TempC.HasValue).Select(t => t.TempC!.Value).ToList();
        if (temps.Count >= 2)
        {
            Line($"<p>Temperature (°C), {temps.Min():0.#}–{temps.Max():0.#}</p>");
            Line(DiagnosticReportFormatting.Sparkline(temps, "#f26d6d", temps.Min() - 1, temps.Max() + 1));
        }
        var clocks = result.Trace.Where(t => t.ClockGhz.HasValue).Select(t => t.ClockGhz!.Value).ToList();
        if (clocks.Count >= 2)
        {
            Line($"<p>Clock (GHz), {clocks.Min():0.00}–{clocks.Max():0.00}</p>");
            Line(DiagnosticReportFormatting.Sparkline(clocks, "#3C9EE8", clocks.Min() - 0.05, clocks.Max() + 0.05));
        }

        Line("<h2>Summary</h2><table>");
        Line($"<tr><td>Threads</td><td>{result.ThreadCount}</td></tr>");
        Line($"<tr><td>Peak temperature</td><td>{Esc(Fmt(result.PeakTempC, "0.#", "°C"))}</td></tr>");
        Line($"<tr><td>Average clock</td><td>{Esc(Fmt(result.AvgClockGhz, "0.00", " GHz"))}</td></tr>");
        Line($"<tr><td>Peak package power</td><td>{Esc(Fmt(result.PeakPowerW, "0.#", " W"))}</td></tr>");
        Line($"<tr><td>Peak fan speed</td><td>{Esc(Fmt(result.PeakFanRpm, "0", " RPM"))}</td></tr>");
        Line($"<tr><td>Safety-abort ceiling used</td><td>{result.EffectiveTempCeilingC:0.#}°C</td></tr>");
        Line("</table>");

        if (result.CpuResult is { } cpu)
        {
            Line("<h2>CPU torture test</h2>");
            Line($"<p>{cpu.ThreadResults.Count} thread(s), {cpu.TotalIterations:N0} total iterations.</p>");
            Line($"<p class=\"{(cpu.AllThreadsPassed ? "ok" : "crit")}\">{(cpu.AllThreadsPassed ? "All threads passed checksum verification." : "One or more threads FAILED checksum verification.")}</p>");
        }

        if (result.MemoryResult is { } mem)
        {
            Line("<h2>Memory pattern-verify test</h2>");
            Line(mem.Skipped
                ? $"<p class=\"warn\">Skipped: {Esc(mem.SkipReason ?? string.Empty)}</p>"
                : $"<p class=\"{(mem.Mismatches.Count == 0 ? "ok" : "crit")}\">{mem.BytesTested / 1073741824.0:0.##} GB tested — {(mem.Mismatches.Count == 0 ? "no mismatches found." : $"{mem.Mismatches.Count} mismatch(es) found.")}</p>");
        }

        if (previous is not null)
        {
            Line("<h2>Compared to the previous run of this test</h2>");
            Line($"<p>{Esc(BuildComparisonText(result, previous))}</p>");
        }

        Line("</body></html>");
        return sb.ToString();
    }

    /// <summary>"Same test, 12°C hotter and 400 MHz lower than three months ago" - the single most
    /// useful artifact #700 asks this whole domain to produce.</summary>
    public static string BuildComparisonText(StressTestRunResult result, StressTestHistoryEntry previous)
    {
        var parts = new List<string>();

        if (result.PeakTempC is { } t && previous.PeakTempC is { } pt && Math.Abs(t - pt) >= 0.5)
            parts.Add($"{Math.Abs(t - pt):0.#}°C {(t > pt ? "hotter" : "cooler")}");

        if (result.AvgClockGhz is { } c && previous.AvgClockGhz is { } pc)
        {
            double deltaMhz = (c - pc) * 1000.0;
            if (Math.Abs(deltaMhz) >= 20) parts.Add($"{Math.Abs(deltaMhz):0} MHz {(deltaMhz > 0 ? "higher" : "lower")}");
        }

        if (result.PeakPowerW is { } p && previous.PeakPowerW is { } pp && Math.Abs(p - pp) >= 1)
            parts.Add($"{Math.Abs(p - pp):0.#} W {(p > pp ? "higher" : "lower")} peak power");

        string when = FormatRelativeAge(result.StartedAt - previous.Timestamp);
        string passChange = result.Passed != previous.Passed
            ? $" ({(result.Passed ? "now passing" : "now FAILING")}, was {(previous.Passed ? "passing" : "failing")})"
            : string.Empty;

        return parts.Count == 0
            ? $"Same test, essentially unchanged from the run {when} ({previous.Timestamp:g}){passChange}."
            : $"Same test, {string.Join(" and ", parts)} than the run {when} ({previous.Timestamp:g}){passChange}.";
    }

    private static string FormatRelativeAge(TimeSpan age)
    {
        if (age.TotalDays >= 60) return $"{age.TotalDays / 30.0:0} months ago";
        if (age.TotalDays >= 1.5) return $"{age.TotalDays:0} days ago";
        if (age.TotalDays >= 1) return "a day ago";
        return "earlier today";
    }

    private static string CriterionText(bool checked_, bool ok) => checked_ ? (ok ? "Pass" : "Fail") : "N/A (not applicable to this test type)";

    private static string Fmt(double? v, string format, string unit) => v is { } val ? val.ToString(format) + unit : "Unknown";

    private static string Num(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Formatting.FormatSpanMinutes (#1086) above one minute; below it this keeps its
    /// original rounded second count ("43s" for 42.7s, where the shared ladder floors to "42s").</summary>
    private static string FormatDuration(TimeSpan d) => d.TotalMinutes >= 1 ? Formatting.FormatSpanMinutes(d) : $"{d.TotalSeconds:0}s";

    public static string DescribeType(StressTestType type) => type switch
    {
        StressTestType.CpuTorture => "CPU torture test",
        StressTestType.MemoryVerify => "Memory pattern-verify test",
        StressTestType.GpuLoad => "GPU load test",
        StressTestType.CombinedSoak => "Combined-load soak test",
        _ => type.ToString(),
    };
}
