using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Enumerates installed browser extensions for Chrome, Edge, and Firefox (#19) - a common, wholly
/// invisible-elsewhere source of "slow startup"/"slow browsing" complaints, since extensions load
/// with the browser but aren't a Windows startup mechanism the registry-Run/Startup-folder/
/// Scheduled-Tasks scans above ever see. Every browser and every profile is read independently and
/// wrapped to degrade to "nothing found" for that one browser - a browser not installed, a
/// nonstandard profile path, or a manifest that fails to parse are all real, expected conditions
/// on any given machine, not bugs. Loaded on demand (a "Load browser extensions" button), the same
/// "expensive-ish, so make it explicit" tradeoff as this tab's Scheduled Tasks section - walking
/// every profile's Extensions folder and parsing a manifest per extension is more I/O than this
/// app does anywhere else in the Startup tab.
/// </summary>
public static class BrowserExtensionService
{
    public static List<BrowserExtensionInfo> List()
    {
        var result = new List<BrowserExtensionInfo>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        ReadChromiumFamily(result, "Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        ReadChromiumFamily(result, "Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
        ReadFirefox(result, Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles"));

        return result.OrderBy(e => e.Browser, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Chrome and Edge share the same on-disk "User Data\&lt;Profile&gt;\Extensions\
    /// &lt;id&gt;\&lt;version&gt;\manifest.json" layout - every profile folder is scanned (not
    /// just "Default"), deduplicated by extension id since the same extension is very often
    /// installed in more than one profile.</summary>
    private static void ReadChromiumFamily(List<BrowserExtensionInfo> into, string browserName, string userDataDir)
    {
        try
        {
            if (!Directory.Exists(userDataDir)) return;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profileDir in Directory.EnumerateDirectories(userDataDir))
            {
                var profileName = Path.GetFileName(profileDir);
                if (!profileName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                    !profileName.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
                    continue;

                var extensionsDir = Path.Combine(profileDir, "Extensions");
                if (!Directory.Exists(extensionsDir)) continue;

                foreach (var extDir in Directory.EnumerateDirectories(extensionsDir))
                {
                    var id = Path.GetFileName(extDir);
                    if (!seenIds.Add(id)) continue;

                    // Chrome/Edge nest one more level: Extensions\<id>\<version>\manifest.json -
                    // pick the highest version folder present (an update can leave more than one).
                    var versionDir = Directory.EnumerateDirectories(extDir)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (versionDir is null) continue;

                    var manifestPath = Path.Combine(versionDir, "manifest.json");
                    if (!File.Exists(manifestPath)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var root = doc.RootElement;
                        string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id;
                        // A localized manifest name looks like "__MSG_extName__", pointing at a
                        // _locales message file this app doesn't resolve - fall back to the raw
                        // folder name (still identifies which extension it is) rather than
                        // showing the unresolved placeholder string.
                        if (name.StartsWith("__MSG_", StringComparison.Ordinal)) name = id;
                        string version = root.TryGetProperty("version", out var verEl) ? verEl.GetString() ?? string.Empty : string.Empty;

                        into.Add(new BrowserExtensionInfo { Browser = browserName, Name = name, Version = version, Id = id });
                    }
                    catch
                    {
                        // Malformed/unreadable manifest.json - skip just this one extension.
                    }
                }
            }
        }
        catch
        {
            // Whole browser unavailable (not installed, nonstandard profile path, access denied) -
            // contribute nothing from it.
        }
    }

    /// <summary>Firefox keeps its extension list in one JSON file per profile
    /// (extensions.json, "addons" array) rather than one manifest.json per extension folder.</summary>
    private static void ReadFirefox(List<BrowserExtensionInfo> into, string profilesDir)
    {
        try
        {
            if (!Directory.Exists(profilesDir)) return;

            foreach (var profileDir in Directory.EnumerateDirectories(profilesDir))
            {
                var extensionsJson = Path.Combine(profileDir, "extensions.json");
                if (!File.Exists(extensionsJson)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(extensionsJson));
                    if (!doc.RootElement.TryGetProperty("addons", out var addons)) continue;

                    foreach (var addon in addons.EnumerateArray())
                    {
                        // Firefox distinguishes real extensions from built-in system add-ons via
                        // "location" ("app-builtin" etc.) - skip anything that isn't a
                        // user-installed extension so this list matches what about:addons shows.
                        string location = addon.TryGetProperty("location", out var locEl) ? locEl.GetString() ?? string.Empty : string.Empty;
                        if (location.Contains("builtin", StringComparison.OrdinalIgnoreCase)) continue;
                        bool isActive = !addon.TryGetProperty("active", out var activeEl) || activeEl.ValueKind != JsonValueKind.False;
                        if (!isActive) continue;

                        string id = addon.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        string version = addon.TryGetProperty("version", out var verEl) ? verEl.GetString() ?? string.Empty : string.Empty;
                        string name = id;
                        if (addon.TryGetProperty("defaultLocale", out var localeEl) &&
                            localeEl.TryGetProperty("name", out var nameEl))
                            name = nameEl.GetString() ?? id;

                        if (id.Length == 0) continue;
                        into.Add(new BrowserExtensionInfo { Browser = "Firefox", Name = name, Version = version, Id = id });
                    }
                }
                catch
                {
                    // Malformed/unreadable extensions.json for this one profile - skip it.
                }
            }
        }
        catch
        {
            // Firefox not installed / no profiles directory - contribute nothing.
        }
    }
}
