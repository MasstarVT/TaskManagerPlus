using System.Diagnostics;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #281: which volume each configured page file lives on, its current/peak usage, and whether that
/// volume is a mechanical (non-SSD) disk - a thin composition, not a re-derivation: media type
/// (and, on demand per row, fragmentation) both call straight into DiskFragmentationService, the
/// same service backing the Storage tab's own HDD-fragmentation card, rather than re-deriving
/// disk-type/fragmentation logic here.
///
/// The other half of the item's ask - "the Storage tab's existing per-disk latency figures" - is
/// worth noting honestly rather than overclaiming: PerformanceViewModel.DiskReadLatencyMs/
/// DiskWriteLatencyMs (which the Storage tab already shows) are the "PhysicalDisk(_Total)"
/// aggregate across every physical disk, not broken out per disk/volume - this app has no true
/// per-disk latency reading today, on the Storage tab or anywhere else. ResponsivenessViewModel
/// surfaces that same system-wide figure alongside the page-file card as honest context, not as a
/// number specific to whichever drive a given page file happens to sit on.
///
/// Page-file placement comes from parsing the PagingFiles REG_MULTI_SZ directly (each entry like
/// "C:\pagefile.sys 0 0", i.e. path + min size MB + max size MB) rather than the single-page-file
/// WMI lookup SystemSpecsService.ReadPageFileLocation already uses for the System Specs tab - this
/// card wants every configured page file, not just whichever one Win32_PageFileUsage happens to
/// enumerate first.
/// </summary>
public static class PageFileLatencyService
{
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFileCategory = "Paging File";

    /// <summary>Parses PagingFiles, joins in media type (DiskFragmentationService.GetMediaType) and
    /// per-instance % Usage/% Usage Peak (Paging File(*) category). Everything here is a fast
    /// registry/perf-counter/WMI-associator read - safe to call from a background thread, matching
    /// the Storage tab's own "load once at start-up, Task.Run'd" tier for its HDD-volume/SMART-disk
    /// enumeration (page-file configuration, like that volume list, essentially never changes
    /// without a reboot).</summary>
    public static List<PageFileVolumeRow> Load()
    {
        var rows = new List<PageFileVolumeRow>();
        var entries = ReadPagingFilesRegistry();
        if (entries.Count == 0) return rows;

        var usageByInstance = ReadUsageCounters();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (configuredPath, minMb, maxMb) in entries)
        {
            // "?:\pagefile.sys" is the system-managed default ("whichever volume Windows picks"),
            // not a drive letter - resolve it to the page file(s) actually in use.
            var paths = configuredPath.StartsWith("?:", StringComparison.Ordinal) ? ResolveActivePageFiles() : new List<string>();
            if (paths.Count == 0) paths.Add(configuredPath);

            foreach (var path in paths)
            {
                if (!emitted.Add(path)) continue;
                string volumeLetter = path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? path[..1].ToUpperInvariant() : string.Empty;
                string mediaType = volumeLetter.Length > 0 ? DiskFragmentationService.GetMediaType(volumeLetter) : "Unknown";
                var (pctUsage, pctPeak) = MatchUsage(usageByInstance, path);

                rows.Add(new PageFileVolumeRow
                {
                    ConfiguredPath = path,
                    VolumeLetter = volumeLetter,
                    MinSizeMb = minMb,
                    MaxSizeMb = maxMb,
                    MediaType = mediaType,
                    PercentUsage = pctUsage,
                    PercentUsagePeak = pctPeak,
                });
            }
        }
        return rows;
    }

    /// <summary>Page files currently in use (Win32_PageFileUsage.Name, e.g. "C:\pagefile.sys"),
    /// falling back to the ExistingPageFiles registry value ("\??\C:\pagefile.sys"). Empty when
    /// neither answers.</summary>
    private static List<string> ResolveActivePageFiles()
    {
        var result = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            foreach (System.Management.ManagementObject mo in searcher.Get())
                if (mo["Name"] is string name && name.Trim().Length > 0) result.Add(name.Trim());
        }
        catch { /* fall back to the registry below */ }

        if (result.Count > 0) return result;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key?.GetValue("ExistingPageFiles") is string[] existing)
                foreach (var line in existing)
                {
                    string p = line.Trim();
                    if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
                    if (p.Length > 0) result.Add(p);
                }
        }
        catch { /* leave empty - caller keeps the unresolved "?:" row */ }
        return result;
    }

    private static List<(string Path, long MinMb, long MaxMb)> ReadPagingFilesRegistry()
    {
        var result = new List<(string, long, long)>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key?.GetValue("PagingFiles") is string[] lines)
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    string path = parts[0];
                    long minMb = parts.Length > 1 && long.TryParse(parts[1], out var mn) ? mn : 0;
                    long maxMb = parts.Length > 2 && long.TryParse(parts[2], out var mx) ? mx : 0;
                    result.Add((path, minMb, maxMb));
                }
            }
        }
        catch
        {
            // Unknown - registry read failed (denied/absent key), empty list, no fabricated row.
        }
        return result;
    }

    private static Dictionary<string, (double Usage, double Peak)> ReadUsageCounters()
    {
        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!PerformanceCounterCategory.Exists(PagingFileCategory)) return result;
            var category = new PerformanceCounterCategory(PagingFileCategory);
            var instances = category.GetInstanceNames().Where(n => n != "_Total").ToList();

            foreach (var inst in instances)
            {
                try
                {
                    using var usageCounter = new PerformanceCounter(PagingFileCategory, "% Usage", inst, readOnly: true);
                    using var peakCounter = new PerformanceCounter(PagingFileCategory, "% Usage Peak", inst, readOnly: true);
                    result[inst] = (usageCounter.NextValue(), peakCounter.NextValue());
                }
                catch
                {
                    // skip this one instance - others still read fine
                }
            }
        }
        catch
        {
            // leave empty - the card just shows blank usage figures for every row
        }
        return result;
    }

    /// <summary>"Paging File" instance names are typically like "\??\C:\pagefile.sys" - matched
    /// against the registry-parsed path by simple substring containment, tolerant of that "\??\"
    /// prefix. Null (blank in the UI) on no match, never a guessed figure.</summary>
    private static (double? Usage, double? Peak) MatchUsage(Dictionary<string, (double Usage, double Peak)> byInstance, string path)
    {
        foreach (var (inst, val) in byInstance)
            if (inst.Contains(path, StringComparison.OrdinalIgnoreCase))
                return (val.Usage, val.Peak);
        return (null, null);
    }
}
