using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Enumerates registered Explorer shell extensions (#20) - context-menu handlers and icon-overlay
/// COM add-ins, a classic cause of a slow Explorer right-click menu that nothing else in this app
/// surfaces. Reads the registered CLSIDs from the well-known locations Explorer itself consults
/// (icon overlays, and context-menu handlers on files/all-objects/the folder-background menu),
/// then resolves each CLSID's friendly name and backing DLL from HKEY_CLASSES_ROOT\CLSID\{guid},
/// and cross-references the "Shell Extensions\Approved" list Windows itself uses to allow a
/// handler to load without a warning prompt. Every read is wrapped independently to degrade to
/// "not found"/empty rather than throwing - a missing key, an unreadable value, or an unresolvable
/// CLSID are all normal on a given machine, not bugs. Loaded on demand (a "Load shell extensions"
/// button), the same "walking several registry trees is more than this tab's live-polled scans do"
/// tradeoff as the Scheduled Tasks and browser-extension sections above.
/// </summary>
public static class ShellExtensionService
{
    public static List<ShellExtensionInfo> List()
    {
        var clsids = new List<(string Category, string Name, string Clsid)>();

        ReadOverlayIdentifiers(clsids);
        ReadContextMenuHandlers(clsids, @"*\shellex\ContextMenuHandlers", "Context menu (all files)");
        ReadContextMenuHandlers(clsids, @"AllFilesystemObjects\shellex\ContextMenuHandlers", "Context menu (all objects)");
        ReadContextMenuHandlers(clsids, @"Directory\Background\shellex\ContextMenuHandlers", "Context menu (folder background)");

        var approved = ReadApprovedList();

        var result = new List<ShellExtensionInfo>();
        var seen = new HashSet<(string, string)>();
        foreach (var (category, registeredName, clsid) in clsids)
        {
            if (!seen.Add((category, clsid))) continue;

            var (resolvedName, dllPath) = ResolveClsid(clsid);
            result.Add(new ShellExtensionInfo
            {
                Name = string.IsNullOrWhiteSpace(resolvedName) ? registeredName : resolvedName,
                Category = category,
                Clsid = clsid,
                DllPath = dllPath,
                IsApproved = approved.Contains(clsid),
            });
        }

        return result.OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadOverlayIdentifiers(List<(string Category, string Name, string Clsid)> into)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers");
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue(null) is string clsid && clsid.Length > 0)
                        into.Add(("Icon overlay", name, clsid));
                }
                catch { /* one bad subkey shouldn't stop the rest */ }
            }
        }
        catch
        {
            // Key unavailable - contribute nothing from this category.
        }
    }

    private static void ReadContextMenuHandlers(List<(string Category, string Name, string Clsid)> into, string path, string category)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    var raw = sub?.GetValue(null) as string;
                    // The default value is normally a bare "{guid}" but can carry surrounding
                    // whitespace/casing quirks on some third-party installers - trim only.
                    if (!string.IsNullOrWhiteSpace(raw))
                        into.Add((category, name, raw.Trim()));
                }
                catch { /* one bad subkey shouldn't stop the rest */ }
            }
        }
        catch
        {
            // Key unavailable - contribute nothing from this category.
        }
    }

    private static HashSet<string> ReadApprovedList()
    {
        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved");
            if (key is null) return approved;

            foreach (var valueName in key.GetValueNames())
                approved.Add(valueName);
        }
        catch
        {
            // Key unavailable (or a locked-down policy) - every entry just shows "not approved"
            // rather than a false positive.
        }
        return approved;
    }

    private static (string Name, string DllPath) ResolveClsid(string clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
            if (key is null) return (string.Empty, string.Empty);

            string name = key.GetValue(null) as string ?? string.Empty;
            string dll = string.Empty;
            using (var inproc = key.OpenSubKey("InprocServer32"))
            {
                dll = inproc?.GetValue(null) as string ?? string.Empty;
            }
            return (name, dll);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }
}
