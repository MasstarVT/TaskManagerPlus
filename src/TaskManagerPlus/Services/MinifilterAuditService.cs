using System.Diagnostics;
using System.Globalization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #369: minifilter (filesystem filter driver) stack audit via `fltmc filters` /
/// `fltmc instances` - the documented, known-tool way to enumerate these (the same "shell out to a
/// known Windows tool rather than raw interop" tradeoff defrag.exe/schtasks.exe/sc.exe already take
/// elsewhere in this app; there's no simpler managed API for Filter Manager's own registry).
///
/// "Quick flag, not a verdict": this only counts how many filter instances are attached to each
/// volume and flags ones above a (user-adjustable) count as worth a manual look - it does NOT
/// attempt to classify individual drivers as "an antivirus" / "a backup agent" / "a sync client" by
/// name, since there's no reliable way to do that from the driver name alone without fabricating
/// vendor knowledge (filter-driver naming isn't standardized, the same reason SensorMonitorService
/// can't do an exact sensor-name lookup - see CLAUDE.md). A high instance count is a plausible
/// latency contributor, not proof of one - a clean modern Windows install already carries several
/// in-box filters (Windows Defender, the cloud-files filter OneDrive uses, Storage QoS, ...), so
/// the "normal" baseline varies by Windows version/edition and is left for the user to judge rather
/// than hardcoded here.
/// </summary>
public static class MinifilterAuditService
{
    public static async Task<MinifilterAuditResult> RunAsync()
    {
        try
        {
            var filtersOutput = await RunFltmcAsync("filters");
            if (filtersOutput is null)
                return MinifilterAuditResult.Unavailable(
                    "fltmc.exe (filters) did not respond - it may not be available on this Windows edition, or the process couldn't be started.");

            var instancesOutput = await RunFltmcAsync("instances");
            if (instancesOutput is null)
                return MinifilterAuditResult.Unavailable(
                    "fltmc.exe (instances) did not respond - it may not be available on this Windows edition, or the process couldn't be started.");

            var filters = ParseFilters(filtersOutput);
            var instances = ParseInstances(instancesOutput);

            // Every Windows install has in-box minifilters (FileInfo, WdFilter, ...), so an empty
            // table is a failed read (e.g. "Filter listing failed with error: 0x80070005"), not "none".
            if (filters.Count == 0)
            {
                string reason = filtersOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no output";
                return MinifilterAuditResult.Unavailable($"fltmc.exe (filters) returned no filter table: {reason}");
            }

            var volumesByFilter = instances
                .GroupBy(i => i.FilterName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(i => i.VolumeName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var filtersWithVolumes = filters
                .Select(f => new MinifilterDriverInfo
                {
                    Name = f.Name,
                    Altitude = f.Altitude,
                    InstanceCount = f.InstanceCount,
                    AttachedVolumes = volumesByFilter.TryGetValue(f.Name, out var vols) ? vols : new List<string>(),
                })
                .OrderByDescending(f => ParseAltitude(f.Altitude))
                .ToList();

            var volumes = instances
                .GroupBy(i => i.VolumeName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MinifilterVolumeInfo
                {
                    VolumeName = g.Key,
                    Instances = g.OrderByDescending(i => ParseAltitude(i.Altitude)).ToList(),
                })
                .OrderBy(v => v.VolumeName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MinifilterAuditResult
            {
                Available = true,
                Filters = filtersWithVolumes,
                Volumes = volumes,
            };
        }
        catch (Exception ex)
        {
            return MinifilterAuditResult.Unavailable($"Failed: {ex.Message}");
        }
    }

    private static double ParseAltitude(string altitude)
        => double.TryParse(altitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Parses `fltmc filters`' table (Filter Name / Num Instances / Altitude / Frame),
    /// skipping any row whose instance count isn't a number.</summary>
    private static List<MinifilterDriverInfo> ParseFilters(string output)
    {
        var result = new List<MinifilterDriverInfo>();
        foreach (var cols in ParseFltmcTable(output))
        {
            if (cols.Length < 3 || cols[0].Length == 0) continue;
            if (!int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instanceCount)) continue;
            result.Add(new MinifilterDriverInfo { Name = cols[0], InstanceCount = instanceCount, Altitude = cols[2] });
        }
        return result;
    }

    /// <summary>Label for a `fltmc instances` row whose Volume Name column is blank (fltmc couldn't
    /// name that volume) - kept as a row so per-volume counts still add up to Num Instances.</summary>
    internal const string UnnamedVolumeLabel = "(unnamed volume)";

    /// <summary>Parses `fltmc instances`' table - Filter / Volume Name / Altitude / Instance Name,
    /// followed on current builds by Frame / SprtFtrs / VlStatus (older builds print fewer trailing
    /// columns, and title the first one "Filter Name"). Only the first four are used.</summary>
    private static List<MinifilterInstanceInfo> ParseInstances(string output)
    {
        var result = new List<MinifilterInstanceInfo>();
        foreach (var cols in ParseFltmcTable(output))
        {
            if (cols.Length < 4 || cols[0].Length == 0) continue;
            result.Add(new MinifilterInstanceInfo
            {
                FilterName = cols[0],
                VolumeName = cols[1].Length > 0 ? cols[1] : UnnamedVolumeLabel,
                Altitude = cols[2],
                InstanceName = cols[3],
            });
        }
        return result;
    }

    /// <summary>Slices one of fltmc's fixed-width tables at the column spans marked by its "----"
    /// separator row: one string[] per data row, columns in order (positional, so a localized
    /// header doesn't matter). Splitting on runs of spaces instead shifted every column left on
    /// `fltmc instances` rows whose Volume Name is blank, turning altitudes into volume names.
    /// Each column runs up to the next one's start, so a value that spills into the gap between
    /// columns isn't cut; the last runs to the end of the line.</summary>
    internal static List<string[]> ParseFltmcTable(string output)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(output)) return rows;
        var lines = output.Replace("\r\n", "\n").Split('\n');

        int sep = Array.FindIndex(lines, IsSeparatorLine);
        if (sep < 0) return rows;

        var starts = new List<int>();
        string sepLine = lines[sep];
        for (int pos = 0; pos < sepLine.Length; pos++)
            if (sepLine[pos] == '-' && (pos == 0 || sepLine[pos - 1] != '-')) starts.Add(pos);

        for (int i = sep + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0 || IsSeparatorLine(line)) continue;

            var cols = new string[starts.Count];
            for (int c = 0; c < starts.Count; c++)
            {
                int start = starts[c];
                int end = c + 1 < starts.Count ? Math.Min(starts[c + 1], line.Length) : line.Length;
                cols[c] = start < end ? line[start..end].Trim() : string.Empty;
            }
            rows.Add(cols);
        }
        return rows;
    }

    private static bool IsSeparatorLine(string line) => line.Trim().Length > 0 && line.All(c => c == '-' || c == ' ' || c == '\r');

    /// <summary>Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
    /// pattern VolumeDiagnosticsService's fsutil/vssadmin shell-outs use.</summary>
    private static async Task<string?> RunFltmcAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(ToolRunner.ResolveTool("fltmc.exe"), args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(10000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }

            return (await outputTask) + (await errorTask);
        }
        catch
        {
            return null;
        }
    }
}
