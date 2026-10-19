using System.Globalization;
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
/// column's span - MinifilterAuditService.ParseFltmcTable slices rows at those spans (not on
/// whitespace, and by position rather than by the localized header text).
/// </summary>
public static class MinifilterService
{
    /// <summary>Throws when either table couldn't be read (fltmc failed, timed out, or printed no
    /// table) - every Windows install has in-box minifilters attached to the system volume
    /// (WdFilter, FileInfo, ...), so an empty table is a failed read, never "none". The VM's catch
    /// shows that as a failure rather than as "no minifilters" / "no AV filters on C:".</summary>
    public static Task<List<MinifilterEntry>> ScanAsync() => Task.Run(async () =>
    {
        var (filtersOutput, filtersExit) = await RunCapturedAsync("fltmc.exe", "filters");
        var (instancesOutput, instancesExit) = await RunCapturedAsync("fltmc.exe", "instances");

        // Columns are read by position, not by header text - fltmc's headers come from its .mui
        // and are translated on a non-English install. filters: Filter Name / Num Instances /
        // Altitude / Frame. instances: Filter / Volume Name / Altitude / Instance Name / ...
        var filterRows = MinifilterAuditService.ParseFltmcTable(filtersOutput)
            .Where(c => c.Length >= 3 && c[0].Length > 0 &&
                        int.TryParse(c[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .ToList();
        if (filterRows.Count == 0)
            throw new InvalidOperationException($"fltmc filters returned no filter table ({DescribeFailure(filtersOutput, filtersExit)}).");

        var instanceRows = MinifilterAuditService.ParseFltmcTable(instancesOutput)
            .Where(c => c.Length >= 2 && c[0].Length > 0)
            .ToList();
        if (instanceRows.Count == 0 && filterRows.Any(c => c[1] != "0"))
            throw new InvalidOperationException($"fltmc instances returned no instance table ({DescribeFailure(instancesOutput, instancesExit)}).");

        // fltmc instances lists one row per (filter, volume) pair - group volumes by filter name.
        var volumesByFilter = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cols in instanceRows)
        {
            string name = cols[0];
            string volume = cols[1];
            if (volume.Length == 0) continue;
            if (!volumesByFilter.TryGetValue(name, out var list)) volumesByFilter[name] = list = new List<string>();
            if (!list.Contains(volume, StringComparer.OrdinalIgnoreCase)) list.Add(volume);
        }

        var results = new List<MinifilterEntry>();
        foreach (var cols in filterRows)
        {
            string name = cols[0];
            string altitudeText = cols[2];
            double? altitude = double.TryParse(altitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
            int instanceCount = int.Parse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            string frame = cols.Length > 3 ? cols[3] : string.Empty;

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

    private static string DescribeFailure(string output, int? exitCode)
    {
        if (exitCode is null) return "timed out";
        string first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return first.Length > 0 ? first : $"exit code {exitCode}, no output";
    }

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

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's default timeout (empty output and a null exit
    /// code for a timed-out run).</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 20000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: string.Empty);
}
