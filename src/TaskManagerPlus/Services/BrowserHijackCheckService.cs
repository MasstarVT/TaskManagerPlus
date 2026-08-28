using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #897: browser hijack check - per-profile homepage/startup-pages/new-tab-override/
/// default-search-engine, PLUS the enterprise policy keys for force-installed extensions and a
/// policy-set homepage/proxy, PLUS NativeMessagingHosts registrations and an External Extensions
/// file check. "Policy-forced extensions on a home PC are the single clearest adware signature"
/// per the item's own text - always flagged prominently (kept simple/safe per the item's own
/// allowed shortcut, rather than trying to reliably detect domain-joined status).
///
/// Extends BrowserExtensionService's existing profile-enumeration shape (Chromium's
/// "User Data\&lt;Profile&gt;" walk, Firefox's "Profiles\" walk) rather than re-deriving it, but
/// reads different files per profile (Preferences/prefs.js, not manifest.json), so this is its own
/// pass over the same directories rather than a shared helper - the file I/O these two do per
/// profile doesn't overlap.
/// </summary>
public static class BrowserHijackCheckService
{
    private const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";

    public static (List<BrowserHijackFinding> Findings, List<SecurityFinding> SecurityFindings) Scan()
    {
        var findings = new List<BrowserHijackFinding>();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        ScanChromiumProfiles(findings, "Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        ScanChromiumProfiles(findings, "Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
        ScanFirefoxProfiles(findings, Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles"));

        ScanPolicy(findings, "Chrome", ChromePolicyKey);
        ScanPolicy(findings, "Edge", EdgePolicyKey);

        ScanNativeMessagingHosts(findings, "Chrome", @"SOFTWARE\Google\Chrome\NativeMessagingHosts");
        ScanNativeMessagingHosts(findings, "Edge", @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts");
        ScanNativeMessagingHosts(findings, "Firefox", @"SOFTWARE\Mozilla\NativeMessagingHosts");

        ScanExternalExtensionsFolders(findings);

        var securityFindings = findings
            .Where(f => f.Severity is FindingSeverity.High or FindingSeverity.Medium)
            .Select(f => new SecurityFinding
            {
                Severity = f.Severity,
                Title = $"{f.Browser} ({f.ProfileOrScope}): {f.Category}",
                Reason = f.Detail,
                Path = $"{f.Browser} - {f.ProfileOrScope}",
                WhatDisablingDoes = f.Category.Contains("extension", StringComparison.OrdinalIgnoreCase)
                    ? "Policy-forced extensions can't be removed from the browser's own extensions page - they come from Group Policy/registry. Removing the policy key (or, on a managed PC, asking whoever manages it) is what actually removes them."
                    : "Review this in the browser's own Settings - this app makes no browser configuration changes itself.",
            })
            .ToList();

        return (findings, securityFindings);
    }

    // ==================================================================================
    // Chromium (Chrome/Edge): Preferences / Secure Preferences JSON, per profile.
    // ==================================================================================

    private static void ScanChromiumProfiles(List<BrowserHijackFinding> into, string browser, string userDataDir)
    {
        try
        {
            if (!Directory.Exists(userDataDir)) return;

            foreach (var profileDir in Directory.EnumerateDirectories(userDataDir))
            {
                var profileName = Path.GetFileName(profileDir);
                if (!profileName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                    !profileName.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
                    continue;

                JsonElement? root = ReadMergedPreferences(profileDir);
                if (root is null) continue;
                var prefs = root.Value;

                // homepage / homepage_is_newtabpage.
                if (prefs.TryGetProperty("homepage", out var hpEl) && hpEl.ValueKind == JsonValueKind.String)
                {
                    string homepage = hpEl.GetString() ?? string.Empty;
                    bool isNewTab = prefs.TryGetProperty("homepage_is_newtabpage", out var isNtpEl) && isNtpEl.ValueKind == JsonValueKind.True;
                    if (!isNewTab && homepage.Length > 0 && !IsKnownBenignHomepage(homepage))
                    {
                        into.Add(new BrowserHijackFinding
                        {
                            Browser = browser,
                            ProfileOrScope = profileName,
                            Category = "Homepage",
                            Detail = $"Set to \"{homepage}\" - worth a look if you didn't set this yourself.",
                            Severity = FindingSeverity.Low,
                        });
                    }
                }

                // session.restore_on_startup + session.startup_urls.
                if (prefs.TryGetProperty("session", out var sessionEl))
                {
                    var startupUrls = new List<string>();
                    if (sessionEl.TryGetProperty("startup_urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
                        startupUrls.AddRange(urlsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty));

                    if (startupUrls.Count > 0)
                    {
                        var suspicious = startupUrls.Where(u => !IsKnownBenignHomepage(u)).ToList();
                        if (suspicious.Count > 0)
                        {
                            into.Add(new BrowserHijackFinding
                            {
                                Browser = browser,
                                ProfileOrScope = profileName,
                                Category = "Startup pages",
                                Detail = $"Opens {suspicious.Count} specific page(s) at launch: {string.Join(", ", suspicious.Take(5))}",
                                Severity = FindingSeverity.Low,
                            });
                        }
                    }
                }

                // default_search_provider_data.template_url_data.
                if (prefs.TryGetProperty("default_search_provider_data", out var dspEl) &&
                    dspEl.TryGetProperty("template_url_data", out var tudEl))
                {
                    string shortName = tudEl.TryGetProperty("short_name", out var snEl) ? snEl.GetString() ?? string.Empty : string.Empty;
                    string keyword = tudEl.TryGetProperty("keyword", out var kwEl) ? kwEl.GetString() ?? string.Empty : string.Empty;
                    if (shortName.Length > 0 && !IsKnownSearchEngine(shortName))
                    {
                        into.Add(new BrowserHijackFinding
                        {
                            Browser = browser,
                            ProfileOrScope = profileName,
                            Category = "Default search engine",
                            Detail = $"Set to \"{shortName}\" (keyword \"{keyword}\") - not one of the well-known engines this app recognizes; worth a look if unfamiliar.",
                            Severity = FindingSeverity.Low,
                        });
                    }
                }
            }
        }
        catch { /* browser not installed / nonstandard profile path - contribute nothing */ }
    }

    /// <summary>Secure Preferences (when present) takes precedence over the same key in plain
    /// Preferences - Chrome moves some settings there specifically because it's integrity-checked -
    /// so this does a shallow top-level merge, Secure Preferences winning per top-level key.</summary>
    private static JsonElement? ReadMergedPreferences(string profileDir)
    {
        JsonElement? securePrefs = TryParseJsonFile(Path.Combine(profileDir, "Secure Preferences"));
        JsonElement? plainPrefs = TryParseJsonFile(Path.Combine(profileDir, "Preferences"));
        if (securePrefs is null) return plainPrefs;
        if (plainPrefs is null) return securePrefs;

        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in plainPrefs.Value.EnumerateObject())
                    if (!securePrefs.Value.TryGetProperty(prop.Name, out _)) prop.WriteTo(writer);
                foreach (var prop in securePrefs.Value.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WriteEndObject();
            }
            ms.Position = 0;
            using var mergedDoc = JsonDocument.Parse(ms.ToArray());
            return mergedDoc.RootElement.Clone();
        }
        catch { return plainPrefs ?? securePrefs; }
    }

    private static JsonElement? TryParseJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static readonly string[] BenignHomepagePrefixes =
    {
        "chrome://", "edge://", "about:", "https://www.google.com", "https://www.bing.com",
        "https://www.msn.com", "https://duckduckgo.com",
    };

    private static bool IsKnownBenignHomepage(string url)
        => BenignHomepagePrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] KnownSearchEngines = { "google", "bing", "yahoo", "duckduckgo", "ecosia", "startpage", "brave" };

    private static bool IsKnownSearchEngine(string shortName)
        => KnownSearchEngines.Any(e => shortName.Contains(e, StringComparison.OrdinalIgnoreCase));

    // ==================================================================================
    // Firefox: prefs.js - "user_pref("key", value);" lines, regex/line-parsed for the specific
    // known pref names only, NOT a real JS parser per the item's own text.
    // ==================================================================================

    private static readonly System.Text.RegularExpressions.Regex UserPrefRegex =
        new("^user_pref\\(\"([^\"]+)\",\\s*(.*)\\);\\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ScanFirefoxProfiles(List<BrowserHijackFinding> into, string profilesDir)
    {
        try
        {
            if (!Directory.Exists(profilesDir)) return;

            foreach (var profileDir in Directory.EnumerateDirectories(profilesDir))
            {
                var prefsPath = Path.Combine(profileDir, "prefs.js");
                if (!File.Exists(prefsPath)) continue;
                var profileName = Path.GetFileName(profileDir);

                string? homepage = null, searchEngine = null;
                try
                {
                    foreach (var line in File.ReadLines(prefsPath))
                    {
                        var match = UserPrefRegex.Match(line);
                        if (!match.Success) continue;
                        string key = match.Groups[1].Value;
                        string rawValue = match.Groups[2].Value.Trim();
                        string value = rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"' ? rawValue[1..^1] : rawValue;

                        if (key == "browser.startup.homepage") homepage = value;
                        else if (key == "browser.search.defaultenginename") searchEngine = value;
                    }
                }
                catch { /* unreadable prefs.js for this one profile - skip it */ }

                if (homepage is { Length: > 0 } && !IsKnownBenignHomepage(homepage))
                {
                    into.Add(new BrowserHijackFinding
                    {
                        Browser = "Firefox",
                        ProfileOrScope = profileName,
                        Category = "Homepage",
                        Detail = $"Set to \"{homepage}\" - worth a look if you didn't set this yourself.",
                        Severity = FindingSeverity.Low,
                    });
                }
                if (searchEngine is { Length: > 0 } && !IsKnownSearchEngine(searchEngine))
                {
                    into.Add(new BrowserHijackFinding
                    {
                        Browser = "Firefox",
                        ProfileOrScope = profileName,
                        Category = "Default search engine",
                        Detail = $"Set to \"{searchEngine}\" - not one of the well-known engines this app recognizes.",
                        Severity = FindingSeverity.Low,
                    });
                }
            }
        }
        catch { /* Firefox not installed / no profiles directory - contribute nothing */ }
    }

    // ==================================================================================
    // Enterprise policy: ExtensionInstallForcelist/Blocklist, HomepageLocation, ProxySettings.
    // "Policy-forced extensions on a home PC are the single clearest adware signature."
    // ==================================================================================

    private static void ScanPolicy(List<BrowserHijackFinding> into, string browser, string policyKeyPath)
    {
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(policyKeyPath);
            if (policyKey is null) return;

            var forced = ReadMultiValueOrNumberedSubkey(policyKey, "ExtensionInstallForcelist");
            if (forced.Count > 0)
            {
                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = "Policy",
                    Category = "Force-installed extension(s)",
                    Detail = $"{forced.Count} extension(s) force-installed via policy, can't be removed from the browser UI: {string.Join(", ", forced.Take(5))}" + (forced.Count > 5 ? ", ..." : string.Empty),
                    Severity = FindingSeverity.High,
                });
            }

            var blocked = ReadMultiValueOrNumberedSubkey(policyKey, "ExtensionInstallBlocklist");
            if (blocked.Count > 0)
            {
                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = "Policy",
                    Category = "Blocked extension(s)",
                    Detail = $"{blocked.Count} extension(s) blocked via policy - informational, blocking isn't itself a hijack sign.",
                    Severity = FindingSeverity.Info,
                });
            }

            if (policyKey.GetValue("HomepageLocation") is string homepageLoc && homepageLoc.Length > 0)
            {
                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = "Policy",
                    Category = "Policy-set homepage",
                    Detail = $"HomepageLocation policy forces the homepage to \"{homepageLoc}\" - can't be changed from browser Settings while this policy is set.",
                    Severity = FindingSeverity.Medium,
                });
            }

            string? proxyDetail = null;
            if (policyKey.GetValue("ProxySettings") is string proxyJson && proxyJson.Length > 0)
                proxyDetail = proxyJson;
            else if (policyKey.GetValue("ProxyServer") is string proxyServer && proxyServer.Length > 0)
                proxyDetail = $"ProxyServer={proxyServer}";
            if (proxyDetail is not null)
            {
                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = "Policy",
                    Category = "Policy-set proxy",
                    Detail = $"A ProxySettings/ProxyServer policy is set: {proxyDetail} - traffic can be silently routed through this proxy with no per-user override.",
                    Severity = FindingSeverity.Medium,
                });
            }
        }
        catch { /* policy key absent/denied - "nothing found," not fabricated */ }
    }

    /// <summary>ExtensionInstallForcelist/Blocklist can be set as a REG_MULTI_SZ (one string
    /// value holding many lines) or as numbered subvalues/subkeys ("1","2",... each one entry) -
    /// GPO vs. a hand-written registry script produce different shapes for the documented-same
    /// policy, so both are checked.</summary>
    private static List<string> ReadMultiValueOrNumberedSubkey(RegistryKey policyKey, string valueOrKeyName)
    {
        var result = new List<string>();
        try
        {
            if (policyKey.GetValue(valueOrKeyName) is string[] multiSz)
            {
                result.AddRange(multiSz.Where(s => s.Length > 0));
                return result;
            }
        }
        catch { /* fall through to the subkey shape */ }

        try
        {
            using var sub = policyKey.OpenSubKey(valueOrKeyName);
            if (sub is null) return result;
            foreach (var name in sub.GetValueNames())
            {
                if (sub.GetValue(name) is string s && s.Length > 0) result.Add(s);
            }
        }
        catch { /* neither shape present */ }
        return result;
    }

    // ==================================================================================
    // NativeMessagingHosts registrations - HKLM and HKCU, per browser family.
    // ==================================================================================

    private static void ScanNativeMessagingHosts(List<BrowserHijackFinding> into, string browser, string keyPath)
    {
        foreach (var (hive, scope) in new[] { (Registry.LocalMachine, "machine"), (Registry.CurrentUser, "user") })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null) continue;
                var hostNames = key.GetSubKeyNames();
                if (hostNames.Length == 0) continue;

                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = $"Native messaging ({scope})",
                    Category = "Native messaging host registration(s)",
                    Detail = $"{hostNames.Length} registered: {string.Join(", ", hostNames.Take(8))}" + (hostNames.Length > 8 ? ", ..." : string.Empty) +
                              " - lets a native (non-browser) program exchange messages with an extension. Normal for password managers/some legitimate tools; worth a look if you don't recognize a name.",
                    Severity = FindingSeverity.Info,
                });
            }
            catch { /* key absent - normal, most machines have none */ }
        }
    }

    // ==================================================================================
    // external_extensions.json / an "External Extensions" folder - best-effort existence check
    // only, per the item's own "check defensively" framing (the exact path varies by Chrome
    // version/install type and isn't a single documented stable location).
    // ==================================================================================

    private static void ScanExternalExtensionsFolders(List<BrowserHijackFinding> into)
    {
        var candidateDirs = new List<(string Browser, string Dir)>();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        candidateDirs.Add(("Chrome", Path.Combine(pfx86, "Google", "Chrome", "Application", "External Extensions")));
        candidateDirs.Add(("Chrome", Path.Combine(pf, "Google", "Chrome", "Application", "External Extensions")));
        candidateDirs.Add(("Chrome", Path.Combine(localAppData, "Google", "Chrome", "Application", "External Extensions")));
        candidateDirs.Add(("Edge", Path.Combine(pfx86, "Microsoft", "Edge", "Application", "External Extensions")));

        foreach (var (browser, dir) in candidateDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var jsonFiles = Directory.GetFiles(dir, "*.json");
                if (jsonFiles.Length == 0) continue;

                into.Add(new BrowserHijackFinding
                {
                    Browser = browser,
                    ProfileOrScope = "Policy",
                    Category = "External Extensions file(s)",
                    Detail = $"{jsonFiles.Length} file(s) in \"{dir}\" - each one force-registers an extension outside the Web Store install flow, the same effect as the ExtensionInstallForcelist policy above.",
                    Severity = FindingSeverity.Medium,
                });
            }
            catch { /* path unavailable/denied - skip this candidate */ }
        }
    }
}
