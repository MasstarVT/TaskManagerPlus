using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
    private static readonly Regex ColumnSplit = new(@"\s{2,}", RegexOptions.Compiled);

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

    /// <summary>Parses `fltmc filters`' 4-column table (Filter Name / Num Instances / Altitude /
    /// Frame). fltmc's column widths aren't fixed-width-aligned in a way that's safe to slice by
    /// character position across Windows versions, so this splits on runs of 2+ spaces instead
    /// (the same tolerant approach VolumeDiagnosticsService's vssadmin/fsutil parsers use) and
    /// silently skips any line that doesn't cleanly split into 4 columns - the header row, the
    /// "----" separator row, and any blank line all fail that check harmlessly.</summary>
    private static List<MinifilterDriverInfo> ParseFilters(string output)
    {
        var result = new List<MinifilterDriverInfo>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || IsSeparatorLine(line)) continue;

            var cols = ColumnSplit.Split(line);
            if (cols.Length != 4) continue;
            if (string.Equals(cols[0], "Filter Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instanceCount)) continue;

            result.Add(new MinifilterDriverInfo { Name = cols[0], InstanceCount = instanceCount, Altitude = cols[2] });
        }
        return result;
    }

    /// <summary>Parses `fltmc instances`' 4-column table (Filter Name / Volume Name / Altitude /
    /// Instance Name) - same tolerant column-split approach as ParseFilters above.</summary>
    private static List<MinifilterInstanceInfo> ParseInstances(string output)
    {
        var result = new List<MinifilterInstanceInfo>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || IsSeparatorLine(line)) continue;

            var cols = ColumnSplit.Split(line);
            if (cols.Length != 4) continue;
            if (string.Equals(cols[0], "Filter Name", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new MinifilterInstanceInfo { FilterName = cols[0], VolumeName = cols[1], Altitude = cols[2], InstanceName = cols[3] });
        }
        return result;
    }

    private static bool IsSeparatorLine(string line) => line.Length > 0 && line.All(c => c == '-' || c == ' ');

    /// <summary>Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
    /// pattern VolumeDiagnosticsService's fsutil/vssadmin shell-outs use.</summary>
    private static async Task<string?> RunFltmcAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("fltmc.exe", args)
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
                try { proc.Kill(); } catch { /* best-effort */ }
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
