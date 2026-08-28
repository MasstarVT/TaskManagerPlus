using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #299: on a #297 trigger (or a manual "Export incident" click), writes a timestamped folder
/// containing the #296 ring-buffer CSV, a JSON snapshot of system state at the trigger moment, any
/// #298 .etl capture, and a Markdown summary.
///
/// No standalone Markdown/HTML report-writer service exists elsewhere in this app to reuse -
/// SummaryViewModel's own "Markdown report"/"HTML report" buttons (#73/#97) build their output via
/// a private inline `Line()`-StringBuilder helper local to SummaryViewModel.GenerateReport, tied to
/// Summary-tab-specific sections (Health Check, specs, stability history) - not something this
/// class can call into without extracting it into a shared service first, which is out of scope
/// for this final chunk. BuildMarkdown below is a straightforward hand-written template instead,
/// following that same lightweight `Line()`-StringBuilder idiom so its *style* still matches the
/// rest of this app's generated reports, per the item's own "a straightforward hand-written
/// template is a fine, honest fallback - document which you did" allowance.
/// </summary>
public static class IncidentBundleService
{
    /// <summary>Everything ResponsivenessViewModel already knows at the moment a trigger fires,
    /// snapshotted into plain values on the UI thread before handing off to this class's async
    /// file-writing (never read live ObservableCollections from a background thread).</summary>
    public sealed record HungWindowSnapshot(string ProcessName, int Pid, string WindowTitle, double? ResponseMs, double? HungForSeconds);
    public sealed record DpcDriverSnapshot(string DriverName, double MaxTimeUs, double AvgTimeUs, int EventCount);
    public sealed record ScheduledTaskSnapshot(string Name, string Status);

    public sealed record SystemStateSnapshot(
        DateTime TimestampUtc,
        string Reason,
        string ForegroundProcessName,
        string ForegroundWindowTitle,
        List<ScheduledTaskSnapshot> RunningScheduledTasks,
        List<HungWindowSnapshot> HungWindows,
        List<DpcDriverSnapshot> TopDpcDrivers);

    public static async Task<(bool Ok, string Message, string? FolderPath)> ExportAsync(
        SystemStateSnapshot state, IReadOnlyList<FlightRecorderSample> ringBuffer, string? etlPath, string baseFolder)
    {
        return await Task.Run(() =>
        {
            try
            {
                string folder = Path.Combine(baseFolder, $"Incident_{state.TimestampUtc.ToLocalTime():yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(folder);

                File.WriteAllText(Path.Combine(folder, "ring-buffer.csv"), FlightRecorderService.ToCsv(ringBuffer));

                var stateDto = new
                {
                    state.TimestampUtc,
                    state.Reason,
                    ForegroundProcess = state.ForegroundProcessName,
                    ForegroundWindowTitle = state.ForegroundWindowTitle,
                    RunningScheduledTasks = state.RunningScheduledTasks,
                    HungWindows = state.HungWindows,
                    TopDpcDrivers = state.TopDpcDrivers,
                };
                File.WriteAllText(Path.Combine(folder, "system-state.json"), JsonSerializer.Serialize(stateDto, new JsonSerializerOptions { WriteIndented = true }));

                bool hasEtl = false;
                if (!string.IsNullOrEmpty(etlPath) && File.Exists(etlPath))
                {
                    try
                    {
                        File.Copy(etlPath, Path.Combine(folder, Path.GetFileName(etlPath)), overwrite: true);
                        hasEtl = true;
                    }
                    catch { /* best-effort - the rest of the bundle is still useful without it */ }
                }

                File.WriteAllText(Path.Combine(folder, "summary.md"), BuildMarkdown(state, ringBuffer, hasEtl));

                return (true, $"Incident bundle exported to {folder}", (string?)folder);
            }
            catch (Exception ex)
            {
                return (false, $"Export failed: {ex.Message}", (string?)null);
            }
        });
    }

    /// <summary>Matches WprCaptureService.OpenInDefaultApp's own shell-out-to-open pattern -
    /// `explorer.exe /select,<path>` highlights the item rather than just opening the folder.</summary>
    public static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    private static string BuildMarkdown(SystemStateSnapshot state, IReadOnlyList<FlightRecorderSample> ring, bool hasEtl)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.AppendLine(s);

        Line($"# Stutter incident — {state.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Line();
        Line($"**Trigger reason:** {state.Reason}");
        Line();
        Line("This is a composite-heuristic incident snapshot (see the Responsiveness tab's Score/" +
             "flight-recorder cards), not a confirmed diagnosis.");
        Line();

        Line("## Foreground app at trigger time");
        Line($"- {(string.IsNullOrEmpty(state.ForegroundProcessName) ? "Unknown" : state.ForegroundProcessName)} — \"{state.ForegroundWindowTitle}\"");
        Line();

        Line("## Ring buffer");
        if (ring.Count == 0)
        {
            Line("- No samples buffered (the flight recorder wasn't armed long enough before this trigger).");
        }
        else
        {
            double coveredSeconds = (ring[^1].TimestampUtc - ring[0].TimestampUtc).TotalSeconds;
            Line($"- {ring.Count} sample(s) at ~{FlightRecorderService.SampleHz}Hz, covering the last {coveredSeconds:0.#}s - see ring-buffer.csv.");
        }
        Line();

        Line("## Hung windows at trigger time");
        if (state.HungWindows.Count == 0)
        {
            Line("- None.");
        }
        else
        {
            foreach (var w in state.HungWindows)
            {
                string resp = w.ResponseMs.HasValue ? $"{w.ResponseMs.Value:0} ms round-trip" : "no response measured yet";
                string hungFor = w.HungForSeconds.HasValue ? $", hung {w.HungForSeconds.Value:0}s" : string.Empty;
                Line($"- {w.ProcessName} (pid {w.Pid}) — \"{w.WindowTitle}\" — {resp}{hungFor}");
            }
        }
        Line();

        Line("## Top DPC drivers (this session)");
        if (state.TopDpcDrivers.Count == 0)
        {
            Line("- No DPC/ISR measurement session was running (see the Responsiveness tab's Start button).");
        }
        else
        {
            foreach (var d in state.TopDpcDrivers.Take(10))
                Line($"- {d.DriverName}: max {d.MaxTimeUs:0.#} µs, avg {d.AvgTimeUs:0.#} µs, {d.EventCount} event(s)");
        }
        Line();

        Line("## Running scheduled tasks");
        if (state.RunningScheduledTasks.Count == 0)
        {
            Line("- None.");
        }
        else
        {
            foreach (var t in state.RunningScheduledTasks) Line($"- {t.Name} ({t.Status})");
        }
        Line();

        Line("## ETW trace");
        Line(hasEtl
            ? "- A circular ETW capture (see WprCaptureService) was saved alongside this bundle - open the .etl in Windows Performance Analyzer for the full trace."
            : "- No ETW circular capture was running when this incident was captured.");

        return sb.ToString();
    }
}
