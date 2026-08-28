using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #467: reads UpperFilters/LowerFilters (REG_MULTI_SZ) under
/// HKLM\SYSTEM\CurrentControlSet\Control\Class\{class-guid} for every device setup class
/// (ScanClassWideAsync, gated behind its own button on the Devices &amp; Drivers tab - a full
/// Control\Class sweep, on the same tier as the registry-tree-sweep work CLAUDE.md calls out to
/// keep opt-in) and, per selected device in the #468 device tree, the same values under that
/// device's own HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId} key (ReadDeviceFilters - a single,
/// cheap per-device read, not a tree walk). Each filter's ServiceExists flag is a
/// HKLM\SYSTEM\CurrentControlSet\Services\{name} existence check - a filter driver whose service
/// key is gone (a leftover from an uninstalled security/virtualization/storage-filter product) can
/// still be inserted into the stack and break every device in that class, so this is exactly the
/// kind of leftover worth flagging even though its presence alone isn't proof of an active problem.
/// </summary>
public static class ClassFilterDriverService
{
    public static Task<List<ClassFilterEntry>> ScanClassWideAsync() => Task.Run(ScanClassWide);

    private static List<ClassFilterEntry> ScanClassWide()
    {
        var results = new List<ClassFilterEntry>();
        try
        {
            using var classesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            if (classesKey is null) return results;

            foreach (var guidName in classesKey.GetSubKeyNames())
            {
                try
                {
                    using var classKey = classesKey.OpenSubKey(guidName);
                    if (classKey is null) continue;

                    string className = classKey.GetValue("Class") as string is { Length: > 0 } c ? c : guidName;
                    AddFiltersFromKey(results, classKey, guidName, className, deviceId: null);
                }
                catch
                {
                    // One malformed/access-denied class key shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Control\Class unavailable - empty result, same degrade-to-nothing convention as
            // every other registry sweep in this app.
        }
        return results.OrderBy(r => r.ClassName, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(r => r.FilterName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Per-device filters for one selected #468 device-tree node - a single registry key
    /// open, not a tree walk, so this is cheap enough to call directly on selection rather than
    /// gating it behind its own button.</summary>
    public static List<ClassFilterEntry> ReadDeviceFilters(string? deviceId)
    {
        var results = new List<ClassFilterEntry>();
        if (string.IsNullOrEmpty(deviceId)) return results;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            if (key is null) return results;

            string classGuid = key.GetValue("ClassGUID") as string ?? string.Empty;
            AddFiltersFromKey(results, key, classGuid, className: null, deviceId);
        }
        catch
        {
            // Device removed since the tree was loaded, or access denied - "no filters found".
        }
        return results;
    }

    private static void AddFiltersFromKey(List<ClassFilterEntry> results, RegistryKey key, string classGuid, string? className, string? deviceId)
    {
        AddOne(results, key, "UpperFilters", isUpper: true, classGuid, className, deviceId);
        AddOne(results, key, "LowerFilters", isUpper: false, classGuid, className, deviceId);
    }

    private static void AddOne(List<ClassFilterEntry> results, RegistryKey key, string valueName, bool isUpper,
        string classGuid, string? className, string? deviceId)
    {
        if (key.GetValue(valueName) is not string[] filters || filters.Length == 0) return;

        foreach (var raw in filters)
        {
            string filter = raw.Trim();
            if (filter.Length == 0) continue;

            var (serviceExists, imagePath, fileExists) = ReadServiceFileState(filter);
            results.Add(new ClassFilterEntry
            {
                ClassGuid = classGuid,
                ClassName = className ?? "Unknown",
                DeviceId = deviceId,
                FilterName = filter,
                IsUpperFilter = isUpper,
                ServiceExists = serviceExists,
                ImagePath = imagePath,
                FileExists = fileExists,
            });
        }
    }

    /// <summary>#495 (disk/volume-class-orphan half): extends the plain "does the service key
    /// exist" check (#467) with a second, stronger check - does the .sys the service key itself
    /// points at still exist on disk. A service key can outlive an uninstaller that only deleted
    /// the driver binary, leaving a filter entry that still references a real service registration
    /// but points at nothing runnable.</summary>
    private static (bool ServiceExists, string? ImagePath, bool? FileExists) ReadServiceFileState(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null) return (false, null, null);

            string? imagePath = key.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return (true, null, null);

            string resolved = ResolveDriverPath(imagePath);
            bool? exists;
            try { exists = File.Exists(resolved); }
            catch { exists = null; } // can't tell - not the same as "missing"

            return (true, resolved, exists);
        }
        catch
        {
            // Access-denied reads as "can't tell" rather than "missing" - false here would flag a
            // perfectly normal filter as broken just because this process couldn't read that one key.
            return (true, null, null);
        }
    }

    /// <summary>Driver ImagePath values are commonly `\SystemRoot\system32\drivers\x.sys` or a bare
    /// file name resolved relative to system32\drivers - neither is directly usable with
    /// File.Exists, unlike a full `C:\Windows\...` path some services do store.</summary>
    internal static string ResolveDriverPath(string imagePath)
    {
        string expanded = Environment.ExpandEnvironmentVariables(imagePath.Trim('"'));
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windowsDir, expanded[@"\SystemRoot\".Length..]);
        if (expanded.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windowsDir, expanded);
        if (Path.IsPathRooted(expanded))
            return expanded;
        // Bare file name (e.g. "mydriver.sys") - services with no explicit path load from drivers\.
        return Path.Combine(windowsDir, "system32", "drivers", expanded);
    }
}
