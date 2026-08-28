using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Enumerates registered Explorer shell extensions (#20, widened by #829) - context-menu handlers,
/// icon-overlay COM add-ins, property sheet handlers, copy hooks, drag-drop handlers, and column
/// handlers, a classic cause of a slow Explorer right-click menu / Properties dialog / drag-drop
/// operation that nothing else in this app surfaces. Reads the registered CLSIDs from the
/// well-known locations Explorer itself consults across several category roots (icon overlays;
/// context-menu/property-sheet/copy-hook/drag-drop/column handlers on files, directories, folders,
/// drives, and all filesystem objects - a reasonable, non-exhaustive subset of every root x handler
/// combination Windows shell docs allow, per #829's own scope note), then resolves each CLSID's
/// friendly name, backing DLL, and Authenticode signature status from
/// HKEY_CLASSES_ROOT\CLSID\{guid}, and cross-references the "Shell Extensions\Approved" list
/// Windows itself uses to allow a handler to load without a warning prompt - now also writable
/// (#829's approve/block toggle), not just read. Every read is wrapped independently to degrade to
/// "not found"/empty rather than throwing - a missing key, an unreadable value, or an unresolvable
/// CLSID are all normal on a given machine, not bugs. Loaded on demand (a "Load shell extensions"
/// button), the same "walking several registry trees is more than this tab's live-polled scans do"
/// tradeoff as the Scheduled Tasks and browser-extension sections above.
/// </summary>
public static class ShellExtensionService
{
    private const string ApprovedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    public static List<ShellExtensionInfo> List()
    {
        var clsids = new List<(string Category, string Name, string Clsid)>();

        ReadOverlayIdentifiers(clsids);

        // Context-menu handlers - the original three roots plus Directory/Folder/Drive, per #829.
        ReadHandlerClsids(clsids, @"*\shellex\ContextMenuHandlers", "Context menu (all files)");
        ReadHandlerClsids(clsids, @"AllFilesystemObjects\shellex\ContextMenuHandlers", "Context menu (all objects)");
        ReadHandlerClsids(clsids, @"Directory\Background\shellex\ContextMenuHandlers", "Context menu (folder background)");
        ReadHandlerClsids(clsids, @"Directory\shellex\ContextMenuHandlers", "Context menu (directory)");
        ReadHandlerClsids(clsids, @"Folder\shellex\ContextMenuHandlers", "Context menu (folder)");
        ReadHandlerClsids(clsids, @"Drive\shellex\ContextMenuHandlers", "Context menu (drive)");

        // #829: property sheet handlers - shown on a file/folder's right-click "Properties" dialog.
        ReadHandlerClsids(clsids, @"*\shellex\PropertySheetHandlers", "Property sheet (all files)");
        ReadHandlerClsids(clsids, @"AllFilesystemObjects\shellex\PropertySheetHandlers", "Property sheet (all objects)");
        ReadHandlerClsids(clsids, @"Directory\shellex\PropertySheetHandlers", "Property sheet (directory)");
        ReadHandlerClsids(clsids, @"Folder\shellex\PropertySheetHandlers", "Property sheet (folder)");

        // #829: copy hook / drag-drop handlers - documented under Directory (they veto/observe
        // folder copy and drag-drop operations); column handlers under Folder (Details view columns).
        ReadHandlerClsids(clsids, @"Directory\shellex\CopyHookHandlers", "Copy hook (directory)");
        ReadHandlerClsids(clsids, @"Directory\shellex\DragDropHandlers", "Drag-drop (directory)");
        ReadHandlerClsids(clsids, @"Folder\shellex\ColumnHandlers", "Column handler (folder)");

        var approved = ReadApprovedList();

        var result = new List<ShellExtensionInfo>();
        var seen = new HashSet<(string, string)>();
        foreach (var (category, registeredName, clsid) in clsids)
        {
            if (!seen.Add((category, clsid))) continue;

            var (resolvedName, dllPath) = ResolveClsid(clsid);
            bool dllExists = !string.IsNullOrWhiteSpace(dllPath) && File.Exists(dllPath);
            // #837: real publisher extraction (was hardcoded "Unknown" pre-#836/#837).
            string publisher = "Unknown";
            if (dllExists)
            {
                var signer = SignatureCheckService.GetSignerInfo(dllPath);
                publisher = signer.SubjectCn ?? signer.IssuerCn ?? "Unknown";
            }
            result.Add(new ShellExtensionInfo
            {
                Name = string.IsNullOrWhiteSpace(resolvedName) ? registeredName : resolvedName,
                Category = category,
                Clsid = clsid,
                DllPath = dllPath,
                Publisher = publisher,
                SignatureStatus = dllExists ? SignatureCheckService.GetStatus(dllPath) : "Unknown",
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

    /// <summary>Shared reader for every "&lt;root&gt;\shellex\&lt;HandlerType&gt;" shape this
    /// service walks (#829 widens this beyond context-menu handlers to property sheet/copy hook/
    /// drag-drop/column handlers - they're all the same "subkey name -&gt; default value is a
    /// CLSID" registry shape Explorer itself reads).</summary>
    private static void ReadHandlerClsids(List<(string Category, string Name, string Clsid)> into, string path, string category)
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
            using var key = Registry.LocalMachine.OpenSubKey(ApprovedKeyPath);
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
            return (name, string.IsNullOrWhiteSpace(dll) ? string.Empty : Environment.ExpandEnvironmentVariables(dll));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// #829: approve/block toggle - adds or removes the CLSID's value under
    /// "Shell Extensions\Approved" (HKLM). Adding a value there is what Windows itself considers
    /// "approved" (Explorer loads the handler without a warning prompt); removing it means Explorer
    /// will prompt/block the next time it tries to load that handler - it does NOT unregister the
    /// CLSID or delete the extension itself, same "flip a flag, don't touch the underlying
    /// registration" tradeoff StartupManagerService.SetEnabled takes for the classic Run key.
    /// Requires admin (HKLM) - this app runs elevated throughout, so that's a non-issue in practice.
    /// </summary>
    public static (bool Success, string? Error) SetApproved(string clsid, string name, bool approved)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return (false, "No CLSID for this entry.");

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(ApprovedKeyPath, writable: true);
            if (approved)
            {
                // The Approved list's value data is normally the handler's friendly name - purely
                // descriptive (Explorer only checks that the value exists), but writing it makes a
                // manually-approved entry look the same as one Windows itself approved.
                key.SetValue(clsid, string.IsNullOrWhiteSpace(name) ? clsid : name, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(clsid, throwOnMissingValue: false);
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
