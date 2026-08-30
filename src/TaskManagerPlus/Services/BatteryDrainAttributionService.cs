using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #644: per-app battery-drain attribution over the last several days, via `powercfg /srumutil`
/// (dumps the raw SRUM - System Resource Usage Monitor - database Windows already maintains for
/// its own per-app energy-estimation engine). This answers "what killed my battery overnight" in
/// a way the live drain-rate readout on the Battery card fundamentally can't, since that only
/// ever shows the instantaneous total.
///
/// `/srumutil`'s dump format is considerably more obscure and undocumented than
/// `/batteryreport`'s - Microsoft ships it as a raw diagnostic table dump with no published
/// schema at all, and the exact table/column layout is known to vary by Windows build. Rather
/// than hardcode field names this app could easily get wrong (and risk presenting a fabricated or
/// mis-attributed energy figure - the one thing this project's conventions explicitly rule out),
/// this scans the XML output *adaptively*: any repeating element that looks like one table row is
/// inspected for a child field whose name suggests an app identifier and a sibling field whose
/// name suggests an energy value, the same "look for the shape, not a hardcoded exact name"
/// approach BootPerformanceService's own boot-time-field scan already takes for a different
/// undocumented event schema. When no such shape is found at all (a Windows build whose dump this
/// parser doesn't recognize), this returns an empty list plus an explanatory status message -
/// never a fabricated row.
/// </summary>
public static class BatteryDrainAttributionService
{
    private const int DefaultLookbackDays = 5;
    private const int MaxRows = 50;

    public static async Task<(List<BatteryDrainAttributionRow> Rows, string StatusText)> ReadRecentDrainAsync(
        int lookbackDays = DefaultLookbackDays)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus");
        string tempFile = Path.Combine(tempDir, $"srum-{Guid.NewGuid():N}.xml");
        try
        {
            Directory.CreateDirectory(tempDir);
            // /srumutil can take a while on a machine with a large SRUM database - a longer
            // timeout than the other powercfg shell-outs in this app, same reasoning
            // TracerouteService gives its own longer-than-default bound.
            var (output, exitCode) = await RunProcessAsync("powercfg.exe", $"/srumutil /output \"{tempFile}\" /xml", 60000);

            if (!File.Exists(tempFile))
            {
                return (new List<BatteryDrainAttributionRow>(),
                    exitCode is null
                        ? "powercfg /srumutil timed out."
                        : $"powercfg /srumutil didn't produce output: {output.Trim()}");
            }

            string xml = await File.ReadAllTextAsync(tempFile);
            var rows = ParseSrumXml(xml, lookbackDays);
            return rows.Count > 0
                ? (rows, string.Empty)
                : (rows, "powercfg /srumutil ran, but this app couldn't recognize any per-app energy rows in its output " +
                          "(this dump's exact layout is undocumented by Microsoft and can vary by Windows build/edition).");
        }
        catch (Exception ex)
        {
            return (new List<BatteryDrainAttributionRow>(), $"Couldn't read SRUM energy data: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private static List<BatteryDrainAttributionRow> ParseSrumXml(string xml, int lookbackDays)
    {
        var result = new List<BatteryDrainAttributionRow>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return result; }

        var cutoff = DateTime.Now.AddDays(-lookbackDays);
        var agg = new Dictionary<string, (double Energy, int Samples, DateTime? Last)>(StringComparer.OrdinalIgnoreCase);

        // Candidate "row" elements: any element whose direct children are all leaves (no further
        // nesting) - the shape of one flattened table record, regardless of what this particular
        // dump happens to call it.
        var candidateRows = doc.Descendants().Where(e => e.HasElements && e.Elements().All(c => !c.HasElements));

        foreach (var row in candidateRows)
        {
            string? appName = null;
            double? energy = null;
            DateTime? timestamp = null;

            foreach (var field in row.Elements())
            {
                string name = field.Name.LocalName;
                string value = field.Value.Trim();
                if (value.Length == 0) continue;

                if (appName is null && LooksLikeAppField(name) && LooksLikeAppValue(value))
                    appName = Path.GetFileName(value.TrimEnd('\\'));

                if (energy is null && name.Contains("Energy", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e) && e > 0)
                    energy = e;

                if (timestamp is null &&
                    (name.Contains("Time", StringComparison.OrdinalIgnoreCase) || name.Contains("Date", StringComparison.OrdinalIgnoreCase)) &&
                    BatteryReportService.TryParseFlexibleDate(value, out var parsed))
                    timestamp = parsed;
            }

            if (appName is null || appName.Length == 0 || energy is null) continue;
            if (timestamp is { } ts && ts < cutoff) continue;

            if (agg.TryGetValue(appName, out var existing))
            {
                var last = timestamp is { } t && (existing.Last is null || t > existing.Last) ? t : existing.Last;
                agg[appName] = (existing.Energy + energy.Value, existing.Samples + 1, last);
            }
            else
            {
                agg[appName] = (energy.Value, 1, timestamp);
            }
        }

        foreach (var (name, v) in agg.OrderByDescending(kv => kv.Value.Energy).Take(MaxRows))
        {
            result.Add(new BatteryDrainAttributionRow
            {
                AppName = name,
                EnergyEstimate = v.Energy,
                SampleCount = v.Samples,
                LastSeen = v.Last,
            });
        }

        return result;
    }

    private static bool LooksLikeAppField(string localName) =>
        localName.Contains("App", StringComparison.OrdinalIgnoreCase) ||
        localName.Contains("Exe", StringComparison.OrdinalIgnoreCase) ||
        localName.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
        localName.Contains("Package", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAppValue(string value) =>
        value.Contains('\\') || value.Contains(".exe", StringComparison.OrdinalIgnoreCase) || value.Contains('_');

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism.</summary>
    private static Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
