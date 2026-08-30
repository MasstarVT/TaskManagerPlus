using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #493: parses `fltmc filters` (name/altitude/frame/instance count) and `fltmc instances`
/// (per-volume attachment) into one merged MinifilterEntry list - the file-system minifilter stack,
/// a different mechanism from #467's class UpperFilters/LowerFilters (those are older
/// SYSTEM\CurrentControlSet\Control\Class device-stack filters; minifilters register through
/// FltRegisterFilter and attach to volumes, not device nodes). #494 layers an altitude-range
/// category on top so an anti-virus-class minifilter stacked heavily on the boot volume can be
/// flagged as a likely (not certain) cause of slow file operations.
///
/// Both fltmc subcommands print a fixed-width table with a "----" separator row marking each
/// column's span - ParseTable below locates column boundaries from that row rather than splitting
/// on whitespace, since a filter's own name can occasionally contain spaces.
/// </summary>
public static class MinifilterService
{
    public static Task<List<MinifilterEntry>> ScanAsync() => Task.Run(async () =>
    {
        var filtersOutput = await RunCapturedAsync("fltmc.exe", "filters");
        var instancesOutput = await RunCapturedAsync("fltmc.exe", "instances");

        var filterRows = ParseTable(filtersOutput);
        var instanceRows = ParseTable(instancesOutput);

        // fltmc instances lists one row per (filter, volume) pair - group volumes by filter name.
        var volumesByFilter = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in instanceRows)
        {
            string? name = row.GetValueOrDefault("Filter Name")?.Trim();
            string? volume = row.GetValueOrDefault("Volume Name")?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(volume)) continue;
            if (!volumesByFilter.TryGetValue(name, out var list)) volumesByFilter[name] = list = new List<string>();
            if (!list.Contains(volume, StringComparer.OrdinalIgnoreCase)) list.Add(volume);
        }

        var results = new List<MinifilterEntry>();
        foreach (var row in filterRows)
        {
            string name = row.GetValueOrDefault("Filter Name")?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;

            string altitudeText = row.GetValueOrDefault("Altitude")?.Trim() ?? string.Empty;
            double? altitude = double.TryParse(altitudeText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : null;
            int instanceCount = int.TryParse(row.GetValueOrDefault("Num Instances")?.Trim(), out var n) ? n : 0;
            string frame = row.GetValueOrDefault("Frame")?.Trim() ?? string.Empty;

            volumesByFilter.TryGetValue(name, out var volumes);

            results.Add(new MinifilterEntry
            {
                Name = name,
                AltitudeText = altitudeText,
                AltitudeValue = altitude,
                Frame = frame,
                InstanceCount = instanceCount,
                AttachedVolumes = volumes ?? new List<string>(),
                Category = ClassifyAltitude(altitude),
            });
        }

        return results.OrderByDescending(r => r.AltitudeValue ?? 0).ToList();
    });

    /// <summary>#494: Microsoft's documented minifilter altitude ranges (see "Load Order Groups and
    /// Altitudes for Minifilter Drivers") - a convention vendors are expected to follow when
    /// requesting an altitude from Microsoft, not something Windows enforces. Quick flag, not a
    /// verdict: a filter in the anti-virus range is *likely* anti-virus/security software, not
    /// guaranteed to be.</summary>
    internal static MinifilterCategory ClassifyAltitude(double? altitude)
    {
        if (altitude is not { } a) return MinifilterCategory.Other;
        if (a is >= 320000 and <= 329999) return MinifilterCategory.AntiVirus;
        if (a is >= 360000 and <= 389999) return MinifilterCategory.ActivityMonitor;
        if (a is >= 140000 and <= 159999) return MinifilterCategory.Encryption;
        return MinifilterCategory.Other;
    }

    /// <summary>Locates column boundaries from the "----" separator row fltmc prints under its
    /// header, then slices every subsequent data row at those same character positions - more
    /// robust than splitting on whitespace, since a filter name can itself contain a space.</summary>
    private static List<Dictionary<string, string>> ParseTable(string output)
    {
        var results = new List<Dictionary<string, string>>();
        var lines = output.Replace("\r\n", "\n").Split('\n');

        int sepIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (t.Length > 0 && t.All(c => c == '-' || c == ' ')) { sepIndex = i; break; }
        }
        if (sepIndex < 1) return results;

        string headerLine = lines[sepIndex - 1];
        string sepLine = lines[sepIndex];

        // Column spans: each contiguous run of '-' in the separator line marks one column.
        var spans = new List<(int Start, int End)>();
        int pos = 0;
        while (pos < sepLine.Length)
        {
            if (sepLine[pos] != '-') { pos++; continue; }
            int start = pos;
            while (pos < sepLine.Length && sepLine[pos] == '-') pos++;
            spans.Add((start, pos));
        }
        if (spans.Count == 0) return results;

        var headers = spans.Select(s => Slice(headerLine, s.Start, s.End).Trim()).ToList();

        for (int i = sepIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart().StartsWith('-')) continue; // a later section's own separator row

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < spans.Count; c++)
            {
                // The last column's data can run past its header's own span width - extend it to
                // the end of the line so a long instance name isn't truncated.
                int end = c == spans.Count - 1 ? line.Length : spans[c].End;
                row[headers[c]] = Slice(line, spans[c].Start, end).Trim();
            }
            if (row.Values.Any(v => v.Length > 0)) results.Add(row);
        }
        return results;
    }

    private static string Slice(string s, int start, int end)
    {
        if (start >= s.Length) return string.Empty;
        end = Math.Min(end, s.Length);
        return end > start ? s[start..end] : string.Empty;
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical output-only shape (empty string
    /// for a timed-out run).</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 20000)
        => (await ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: string.Empty)).Output;
}
