using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #495 (legacy-filter half): enumerates HKLM\SYSTEM\CurrentControlSet\Services for Type=2
/// (SERVICE_FILE_SYSTEM_DRIVER) / Type=8 (SERVICE_RECOGNIZER_DRIVER) services that are NOT
/// registered as a minifilter - a minifilter's service key always has an \Instances subkey (that's
/// how FltRegisterFilter's registration shows up in the registry), so its absence is what
/// distinguishes an old-style, pre-Filter-Manager file-system filter from a modern minifilter
/// already covered by #493's `fltmc filters` list. Also excludes a small hand-maintained set of
/// well-known BASE file systems (NTFS, FAT variants, CDFS/UDFS, the SMB redirector stack, ...) that
/// are Type=2 services too but aren't filters at all - not exhaustive, so an unrecognized base file
/// system would still show up here; that's a false positive worth a second look, not a bug.
/// </summary>
public static class LegacyFilterDriverService
{
    /// <summary>Base (non-filter) file-system-family services, and a few non-file-system drivers
    /// that happen to share the Type=2/8 classification for legacy load-ordering reasons, excluded
    /// from the legacy-filter list - deliberately small and named, not a guess at completeness.
    /// Verified against a real Windows 11 system's own Services tree while building this (a Type=2
    /// scan without this list turned up FltMgr itself - the modern Filter Manager, the opposite of
    /// a legacy pre-minifilter driver - plus NetBIOS, a network transport driver, neither of which
    /// belongs here).</summary>
    private static readonly HashSet<string> KnownBaseFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntfs", "fastfat", "exfat", "refs", "refsv1", "cdfs", "udfs", "fat", "fatfilter", "cimfs",
        "csc", "mup", "npfs", "msfs", "rdbss", "mrxsmb", "mrxsmb10", "mrxsmb20", "mrxdav", "dfsc",
        "srv", "srv2", "srvnet", "fs_rec", "fs_rec2", "bindflt", "wcifs", "cldflt",
        "fltmgr", "netbios", "bowser",
    };

    public static Task<List<LegacyFilterDriverEntry>> ScanAsync(IReadOnlyCollection<string>? knownMinifilterNames = null) =>
        Task.Run(() => Scan(knownMinifilterNames ?? Array.Empty<string>()));

    private static List<LegacyFilterDriverEntry> Scan(IReadOnlyCollection<string> knownMinifilterNames)
    {
        var minifilterNames = new HashSet<string>(knownMinifilterNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<LegacyFilterDriverEntry>();

        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return results;

            foreach (var name in servicesKey.GetSubKeyNames())
            {
                if (KnownBaseFileSystems.Contains(name)) continue;
                if (minifilterNames.Contains(name)) continue; // already shown as a minifilter (#493)

                try
                {
                    using var svc = servicesKey.OpenSubKey(name);
                    if (svc is null) continue;

                    if (svc.GetValue("Type") is not int type || (type != 2 && type != 8)) continue;
                    if (svc.GetSubKeyNames().Contains("Instances", StringComparer.OrdinalIgnoreCase)) continue; // it's a minifilter

                    string displayName = svc.GetValue("DisplayName") as string is { Length: > 0 } d ? d : name;
                    int startType = svc.GetValue("Start") is int s ? s : -1;

                    string? imagePath = svc.GetValue("ImagePath") as string;
                    string? resolvedPath = null;
                    bool isOrphaned = false;
                    if (!string.IsNullOrWhiteSpace(imagePath))
                    {
                        resolvedPath = ClassFilterDriverService.ResolveDriverPath(imagePath);
                        try { isOrphaned = !File.Exists(resolvedPath); }
                        catch { isOrphaned = false; } // can't tell - don't guess "orphaned"
                    }

                    results.Add(new LegacyFilterDriverEntry
                    {
                        ServiceName = name,
                        DisplayName = displayName,
                        ImagePath = resolvedPath,
                        StartTypeText = DescribeStart(startType),
                        IsOrphaned = isOrphaned,
                    });
                }
                catch
                {
                    // One malformed/access-denied service key shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Services key unavailable - degrade to empty, same as every other registry sweep here.
        }

        return results.OrderByDescending(r => r.IsOrphaned).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DescribeStart(int start) => start switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => "Unknown",
    };
}
