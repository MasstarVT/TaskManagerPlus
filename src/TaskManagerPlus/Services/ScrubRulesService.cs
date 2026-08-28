using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #985: loads/saves the scrub dictionary (AppPaths.SettingsDirectory\scrub-rules.json), seeded
/// with #984's built-in patterns on first run - same fail-silently-to-defaults convention every
/// other settings file in this app follows (ThemeService/theme.json, SummarySettingsService/
/// summary-settings.json, ...). A saved file that predates a newer built-in rule kind (an app
/// update added one) gets that kind merged back in on load, rather than the user's file silently
/// missing it forever.
/// </summary>
public static class ScrubRulesService
{
    // #984: "public IP (reuse PublicIpLookupService if it's cheap/already-cached; otherwise skip
    // this specific one and note why)" - PublicIpLookupService.LookupAsync is neither: it's a real,
    // uncached outbound HTTPS call to ipinfo.io every time it's invoked, and its own remarks are
    // explicit that it's "deliberately never called automatically" for exactly that reason. Making
    // a bundle collection (or, worse, every "Copy forum post" click) silently phone home to look up
    // a value just to redact it would violate that same rule for no real gain - a scrub pass has no
    // legitimate need to know the public IP at all, only to recognize and redact it if it happens
    // to already appear in a collected artifact (which none of #981's collectors surface anyway).
    // So this is skipped entirely rather than reusing/duplicating that lookup.

    private static string SettingsPath => AppPaths.GetPath("scrub-rules.json");

    public static List<ScrubRule> BuiltInRules() => new()
    {
        new ScrubRule { Id = "builtin.username", Label = "Windows username", Kind = ScrubRuleKind.Username, PlaceholderPrefix = "USER" },
        new ScrubRule { Id = "builtin.machinename", Label = "Computer name", Kind = ScrubRuleKind.MachineName, PlaceholderPrefix = "HOST" },
        new ScrubRule { Id = "builtin.domain", Label = "Domain name", Kind = ScrubRuleKind.Domain, PlaceholderPrefix = "DOMAIN" },
        new ScrubRule { Id = "builtin.wifissid", Label = "Wi-Fi network name (SSID)", Kind = ScrubRuleKind.WifiSsid, PlaceholderPrefix = "SSID" },
        new ScrubRule { Id = "builtin.mac", Label = "MAC addresses", Kind = ScrubRuleKind.MacAddress, PlaceholderPrefix = "MAC" },
        new ScrubRule { Id = "builtin.productkey", Label = "Windows product keys", Kind = ScrubRuleKind.ProductKey, PlaceholderPrefix = "KEY" },
    };

    public static ScrubRuleSet Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<ScrubRuleSet>(json);
                if (loaded is not null)
                {
                    var existingKinds = loaded.Rules.Where(r => r.IsBuiltIn).Select(r => r.Kind).ToHashSet();
                    var missing = BuiltInRules().Where(b => !existingKinds.Contains(b.Kind)).ToList();
                    if (missing.Count > 0)
                    {
                        loaded.Rules.InsertRange(0, missing);
                        Save(loaded);
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable file - fall back to built-in-only defaults, same as every other
            // settings file in this app.
        }

        var seeded = new ScrubRuleSet { Rules = BuiltInRules() };
        Save(seeded);
        return seeded;
    }

    public static void Save(ScrubRuleSet set)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }

    /// <summary>#984: "Wi-Fi SSIDs (if easily available from `netsh wlan show interfaces`)" - a
    /// real shell-out, so this is only ever called on demand (right before building a scrubber for
    /// an actual collection/review pass), never ambient. Returns null on any failure/timeout/no
    /// adapter - the built-in Wi-Fi SSID rule then simply contributes no replacements, same
    /// "degrade, never fabricate" shape every other optional signal in this app follows.</summary>
    public static async Task<string?> TryGetCurrentSsidAsync()
    {
        try
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync("netsh.exe", "wlan show interfaces", timeoutMs: 8000);
            if (exitCode != 0) return null;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                // "SSID" (the network name) vs "BSSID" (the AP's MAC) - match SSID only, and only
                // when it's not immediately preceded by "B" on the same line.
                if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon < 0) continue;
                    string value = trimmed[(colon + 1)..].Trim();
                    if (value.Length > 0) return value;
                }
            }
        }
        catch
        {
            // Best-effort - see remarks above.
        }
        return null;
    }
}

/// <summary>
/// #984: applies an enabled <see cref="ScrubRuleSet"/> over text, replacing every match with a
/// stable, first-seen-numbered placeholder (`&lt;USER1&gt;`, `&lt;MAC1&gt;`, ...) - the same value
/// always maps to the same placeholder across every file scrubbed in one run, via
/// <see cref="_placeholderByValue"/>. <see cref="Summaries"/> is the #984 review list itself
/// (original value → placeholder → occurrence count) - nothing is scrubbed silently; the caller is
/// expected to show this list before finalizing anything.
/// </summary>
public sealed class PiiScrubber
{
    private readonly List<(ScrubRule Rule, Regex Regex)> _compiled = new();
    private readonly Dictionary<string, string> _placeholderByValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _nextIndexByPrefix = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScrubReplacementSummary> _summaryByPlaceholder = new();

    public IReadOnlyList<ScrubReplacementSummary> Summaries =>
        _summaryByPlaceholder.Values.OrderBy(s => s.Placeholder, StringComparer.OrdinalIgnoreCase).ToList();

    private PiiScrubber() { }

    /// <summary>Resolves each enabled rule's live match text/regex once (the current username,
    /// machine name, domain, the Wi-Fi SSID passed in already-looked-up per
    /// ScrubRulesService.TryGetCurrentSsidAsync's remarks) and compiles it - a rule with nothing to
    /// match (e.g. not domain-joined, no Wi-Fi adapter, a too-short literal) simply contributes no
    /// compiled pattern rather than matching everything.</summary>
    public static PiiScrubber Build(ScrubRuleSet ruleSet, string? wifiSsid)
    {
        var scrubber = new PiiScrubber();
        string machineName = Environment.MachineName;
        string userName = Environment.UserName;
        string? domain = ResolveDomain(machineName);

        foreach (var rule in ruleSet.Rules.Where(r => r.Enabled))
        {
            Regex? regex = rule.Kind switch
            {
                ScrubRuleKind.MacAddress =>
                    new Regex(@"([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled),
                ScrubRuleKind.ProductKey =>
                    new Regex(@"\b[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}\b", RegexOptions.Compiled),
                ScrubRuleKind.Username => LiteralRegexOrNull(userName),
                ScrubRuleKind.MachineName => LiteralRegexOrNull(machineName),
                ScrubRuleKind.Domain => LiteralRegexOrNull(domain),
                ScrubRuleKind.WifiSsid => LiteralRegexOrNull(wifiSsid),
                ScrubRuleKind.CustomLiteral => LiteralRegexOrNull(rule.LiteralValue),
                _ => null,
            };

            if (regex is not null) scrubber._compiled.Add((rule, regex));
        }
        return scrubber;
    }

    /// <summary>A literal value shorter than 2 characters (or the machine's own domain equaling
    /// its workgroup/machine name when not actually domain-joined) is skipped rather than compiled
    /// - matching every occurrence of a 1-character "value" would scrub the file into uselessness.</summary>
    private static Regex? LiteralRegexOrNull(string? literal) =>
        literal is { Length: >= 2 } ? new Regex(Regex.Escape(literal), RegexOptions.Compiled | RegexOptions.IgnoreCase) : null;

    private static string? ResolveDomain(string machineName)
    {
        try
        {
            var domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (string.IsNullOrWhiteSpace(domain)) return null;
            // A non-domain-joined machine's "DomainName" is often just its own workgroup/machine
            // name repeated - nothing meaningful to redact there.
            return domain.Equals(machineName, StringComparison.OrdinalIgnoreCase) ? null : domain;
        }
        catch
        {
            return null;
        }
    }

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (rule, regex) in _compiled)
            text = regex.Replace(text, m => ReplaceMatch(rule, m.Value));
        return text;
    }

    private string ReplaceMatch(ScrubRule rule, string original)
    {
        string key = rule.PlaceholderPrefix + ":" + original.ToLowerInvariant();
        if (!_placeholderByValue.TryGetValue(key, out var placeholder))
        {
            int next = _nextIndexByPrefix.TryGetValue(rule.PlaceholderPrefix, out var n) ? n + 1 : 1;
            _nextIndexByPrefix[rule.PlaceholderPrefix] = next;
            placeholder = $"<{rule.PlaceholderPrefix}{next}>";
            _placeholderByValue[key] = placeholder;
        }

        if (!_summaryByPlaceholder.TryGetValue(placeholder, out var summary))
        {
            summary = new ScrubReplacementSummary { RuleLabel = rule.Label, Placeholder = placeholder, OriginalValue = original };
            _summaryByPlaceholder[placeholder] = summary;
        }
        summary.OccurrenceCount++;
        return placeholder;
    }
}
