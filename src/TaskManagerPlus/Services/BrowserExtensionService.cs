using System.IO;
using System.Text.Json;
using Microsoft.Win32;
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
///
/// Round 20, #898 extended this with the permission-surface fields on BrowserExtensionInfo -
/// permissions/host_permissions were already sitting in the same manifest.json this service reads
/// for Name/Version, so pulling them out is minimal extra work; install-source and enabled-state
/// are new reads (a policy-forcelist registry check and the profile's Preferences JSON).
/// </summary>
public static class BrowserExtensionService
{
    private const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";

    // Manifest V2 folds host access into "permissions" as URL-match patterns rather than a
    // separate "host_permissions" array (that's MV3-only) - these are the all-hosts shapes worth
    // treating the same as "<all_urls>" wherever they show up.
    private static readonly string[] AllUrlsPatterns = { "<all_urls>", "*://*/*", "http://*/*", "https://*/*", "*://*/", "file:///*" };
    private static readonly string[] SensitivePermissionNames = { "webRequest", "webRequestBlocking", "nativeMessaging", "debugger", "cookies" };

    public static List<BrowserExtensionInfo> List()
    {
        var result = new List<BrowserExtensionInfo>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var policyForcedIds = ReadPolicyForcedExtensionIds();

        ReadChromiumFamily(result, "Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"), policyForcedIds);
        ReadChromiumFamily(result, "Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), policyForcedIds);
        ReadFirefox(result, Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles"));

        return result.OrderBy(e => e.Browser, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>#898: extension IDs named in either browser's ExtensionInstallForcelist policy -
    /// used only to tag an extension already found by the normal profile walk as "Policy-installed"
    /// rather than guessing "Web Store" for it. See #897's BrowserHijackCheckService for the fuller
    /// policy read (blocklist, homepage/proxy policy, ...) - this is deliberately just the ID set.</summary>
    private static HashSet<string> ReadPolicyForcedExtensionIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyPath in new[] { ChromePolicyKey, EdgePolicyKey })
        {
            try
            {
                using var policyKey = Registry.LocalMachine.OpenSubKey(keyPath);
                using var forcelist = policyKey?.OpenSubKey("ExtensionInstallForcelist");
                if (forcelist is null) continue;
                foreach (var valueName in forcelist.GetValueNames())
                {
                    // Each value is "extensionid;updateurl" (numbered subvalues "1","2",... is
                    // the documented shape when set via GPO/registry rather than a single MULTI_SZ).
                    string raw = forcelist.GetValue(valueName) as string ?? string.Empty;
                    int semi = raw.IndexOf(';');
                    string id = (semi > 0 ? raw[..semi] : raw).Trim();
                    if (id.Length > 0) ids.Add(id);
                }
            }
            catch { /* policy not set / access denied - contributes nothing */ }
        }
        return ids;
    }

    /// <summary>Chrome and Edge share the same on-disk "User Data\&lt;Profile&gt;\Extensions\
    /// &lt;id&gt;\&lt;version&gt;\manifest.json" layout - every profile folder is scanned (not
    /// just "Default"), deduplicated by extension id since the same extension is very often
    /// installed in more than one profile.</summary>
    private static void ReadChromiumFamily(List<BrowserExtensionInfo> into, string browserName, string userDataDir, HashSet<string> policyForcedIds)
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

                // #898: enabled state lives in the profile's own Preferences file (Secure
                // Preferences takes precedence when present - Chrome moves some extension state
                // there specifically because it's integrity-checked), read once per profile and
                // reused for every extension found under it.
                var enabledStates = ReadExtensionEnabledStates(profileDir);

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

                        var permissions = ReadStringArray(root, "permissions");
                        var hostPermissions = ReadStringArray(root, "host_permissions");
                        bool hasAllUrls = permissions.Concat(hostPermissions).Any(p => AllUrlsPatterns.Contains(p, StringComparer.OrdinalIgnoreCase));
                        var sensitive = permissions.Where(p => SensitivePermissionNames.Contains(p, StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        bool? isEnabled = enabledStates.TryGetValue(id, out var state) ? state : null;

                        into.Add(new BrowserExtensionInfo
                        {
                            Browser = browserName,
                            Name = name,
                            Version = version,
                            Id = id,
                            Permissions = permissions,
                            HostPermissions = hostPermissions,
                            HasAllUrlsAccess = hasAllUrls,
                            SensitivePermissions = sensitive,
                            InstallSource = DetermineInstallSource(id, policyForcedIds),
                            IsEnabled = isEnabled,
                        });
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

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array) return new List<string>();
        var result = new List<string>();
        foreach (var item in arrEl.EnumerateArray())
        {
            // "permissions" can mix plain strings ("tabs") with API-permission objects
            // ({"usbDevices": [...]}) per the documented manifest schema - only the string form
            // is a name/host pattern this app can classify, so object entries are skipped.
            if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString() ?? string.Empty);
        }
        return result.Where(s => s.Length > 0).ToList();
    }

    /// <summary>#898: "Web Store" / "Policy-installed" / "Unknown source" - see
    /// BrowserExtensionInfo.InstallSource's remarks for the honesty caveat this carries. Chrome/
    /// Edge extension IDs are always exactly 32 characters drawn from the letters a-p (a base-16
    /// alphabet shifted so every "digit" is a letter) - REGARDLESS of install method, so this is
    /// NOT itself proof of a Web Store install, just a shape every real Chromium extension ID has;
    /// this app has no reliable way to distinguish a genuine Web Store install from a sideloaded
    /// .crx with a real-looking id from the data available here, so anything not policy-forced is
    /// labeled "Unknown source" rather than a guessed "Web Store"/"sideloaded" split. (Kept the
    /// id-shape check only so an obviously malformed/synthetic id - the "developer mode, unpacked"
    /// case, whose id Chrome derives from the folder path instead of the Web Store key - can still
    /// be called out as unusual via the id's own shape.)</summary>
    private static string DetermineInstallSource(string id, HashSet<string> policyForcedIds)
    {
        if (policyForcedIds.Contains(id)) return "Policy-installed";
        bool looksLikeStandardId = id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');
        return looksLikeStandardId ? "Unknown source (standard-shaped ID)" : "Unknown source (non-standard ID - possibly unpacked/developer mode)";
    }

    /// <summary>Reads "extensions.settings.&lt;id&gt;.state" out of Secure Preferences (falling
    /// back to plain Preferences) - 1=enabled/0=disabled per Chromium's own convention. Any
    /// mismatch from that exact shape degrades to an empty map (every extension in the profile
    /// then shows "Unknown"), never a guessed enabled/disabled state.</summary>
    private static Dictionary<string, bool> ReadExtensionEnabledStates(string profileDir)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                var path = Path.Combine(profileDir, fileName);
                if (!File.Exists(path)) continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("extensions", out var extEl)) continue;
                if (!extEl.TryGetProperty("settings", out var settingsEl)) continue;

                foreach (var prop in settingsEl.EnumerateObject())
                {
                    if (result.ContainsKey(prop.Name)) continue; // Secure Preferences (read first) wins over Preferences
                    if (prop.Value.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.Number)
                        result[prop.Name] = stateEl.GetInt32() != 0;
                }
            }
            catch
            {
                // Malformed/unreadable/unexpected-shape preferences file - leave whatever this
                // browser's other file (if any) already contributed.
            }
        }
        return result;
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

                        string id = addon.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        string version = addon.TryGetProperty("version", out var verEl) ? verEl.GetString() ?? string.Empty : string.Empty;
                        string name = id;
                        if (addon.TryGetProperty("defaultLocale", out var localeEl) &&
                            localeEl.TryGetProperty("name", out var nameEl))
                            name = nameEl.GetString() ?? id;

                        if (id.Length == 0) continue;
                        if (!isActive) continue;

                        // #898: Firefox's own "userPermissions"/"optionalPermissions" objects,
                        // each with "permissions" (API names) and "origins" (host match patterns)
                        // arrays - a different JSON shape from Chromium's manifest.json, but the
                        // same underlying concept, read here since it's already sitting in the
                        // same parsed addon object.
                        var permissions = new List<string>();
                        var hostPermissions = new List<string>();
                        if (addon.TryGetProperty("userPermissions", out var permsEl))
                        {
                            if (permsEl.TryGetProperty("permissions", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
                                permissions.AddRange(pArr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty));
                            if (permsEl.TryGetProperty("origins", out var oArr) && oArr.ValueKind == JsonValueKind.Array)
                                hostPermissions.AddRange(oArr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty));
                        }
                        bool hasAllUrls = hostPermissions.Any(p => AllUrlsPatterns.Contains(p, StringComparer.OrdinalIgnoreCase) || p == "<all_urls>");
                        var sensitive = permissions.Where(p => SensitivePermissionNames.Contains(p, StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        into.Add(new BrowserExtensionInfo
                        {
                            Browser = "Firefox",
                            Name = name,
                            Version = version,
                            Id = id,
                            Permissions = permissions,
                            HostPermissions = hostPermissions,
                            HasAllUrlsAccess = hasAllUrls,
                            SensitivePermissions = sensitive,
                            InstallSource = "Unknown source", // Firefox's extensions.json carries no comparable Web-Store-vs-sideload marker this app reads
                            IsEnabled = isActive,
                        });
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
