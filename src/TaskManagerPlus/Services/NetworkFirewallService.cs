using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>One Windows Firewall profile's headline state (#566) - Domain/Private/Public, from the
/// effective (ActiveStore) policy. <see cref="Enabled"/> is null when the profile couldn't be read
/// at all (degrade to Unknown, never guess). <see cref="IsFlagged"/>
/// is true when the profile is off, or its default outbound action is Block rather than Windows'
/// own default (Allow) - a quick flag, not a verdict: a Block-by-default outbound policy is a deliberate,
/// legitimate lockdown in some environments. Named distinctly from the Security tab's own
/// FirewallService/FirewallProfileInfo (#881/#882, a separate independently-built posture/rule
/// audit) since both exist side by side.</summary>
public sealed record NetworkFirewallProfileStatus(
    string ProfileName, bool? Enabled, string InboundPolicy, string OutboundPolicy, bool IsFlagged, string? FlagReason)
{
    /// <summary>Plain string form of <see cref="Enabled"/> for the view - avoids a bool?-to-text
    /// converter for a value that never changes after construction, same "compute it once in C#"
    /// tradeoff AdapterHealthRow's own ArpOffloadText/WakeOnMagicPacketText already take.</summary>
    public string EnabledText => Enabled switch { true => "ON", false => "OFF", null => "Unknown" };
}

/// <summary>One firewall rule (#567), from one `netsh advfirewall firewall show rule name=all
/// verbose` block. <see cref="ApplicationPath"/> is empty when the rule isn't scoped to a specific
/// program. Named distinctly from the Security tab's own FirewallRuleInfo for the same reason as
/// NetworkFirewallProfileStatus above. <see cref="Source"/> is "Local" for the local store's rules,
/// or netsh's own "Rule source" text for a rule only the active (merged) policy has.</summary>
public sealed record NetworkFirewallRuleInfo(
    string Name, string Direction, string Action, string Protocol,
    string LocalPort, string RemotePort, string ApplicationPath, bool Enabled, string Profiles, string Grouping,
    string Source = "Local");

/// <summary><see cref="ActivePolicyNote"/> is non-null when the active-policy listing couldn't be
/// read, so rules deployed by Group Policy/MDM may be missing from <see cref="Rules"/>.</summary>
public sealed record NetworkFirewallRuleSet(List<NetworkFirewallRuleInfo> Rules, int PolicyOnlyCount, string? ActivePolicyNote);

/// <summary>
/// Items #566/#567 (suggestions.md "Firewall rules and blocked connections"): profile status and
/// the full rule set, for the Network tab's own Firewall card. Profiles come from the ActiveStore
/// (see ReadProfileStatusAsync); for rules this deliberately parses
/// `netsh advfirewall`'s own English-locale text output rather than the HNetCfg.FwPolicy2 COM
/// object these items' text suggests - `netsh advfirewall firewall show rule name=all verbose`
/// already prints one clean "Field: value" block per rule covering every column both items ask for
/// (name/direction/action/protocol/ports/program/enabled state), so COM interop here would only add
/// real build risk (either a design-time-generated interop assembly this build has no Windows SDK
/// tlbimp step to produce, or late-bound `dynamic` COM calls needing an extra Microsoft.CSharp
/// package reference just to foreach a COM collection) for no parsing benefit over the pattern every
/// other network diagnostic in this app already uses. Same documented "English-locale field labels"
/// limitation as WifiDiagnosticsService/DnsConfigService/TcpGlobalSettingsService's own netsh
/// parses.
///
/// Named NetworkFirewallService (not FirewallService) since the Security tab independently built
/// its own FirewallService for #881/#882's profile-posture/inbound-rule-audit section - a real
/// class-name collision only surfaced when domain 6 (Network) and domain 9 (Security) merged
/// together, unified here by renaming this one rather than picking a side, since both are actively
/// used by different tabs.
/// </summary>
public static class NetworkFirewallService
{
    private const int RulesTimeoutMs = 45000; // a few hundred rules is common; verbose output can run to several thousand lines
    private static readonly string[] ProfileNames = { "Domain", "Private", "Public" };

    /// <summary>`netsh advfirewall show allprofiles` reads the Local store, where a Group Policy that
    /// turns a profile off or blocks outbound doesn't show - so this reads the effective ActiveStore
    /// policy through the Security tab's FirewallService.ReadPosture instead. A failed read gives
    /// Unknown rows.</summary>
    public static async Task<List<NetworkFirewallProfileStatus>> ReadProfileStatusAsync()
    {
        List<FirewallService.FirewallProfileInfo> profiles;
        try { profiles = (await Task.Run(FirewallService.ReadPosture)).Profiles; }
        catch { profiles = new(); }

        var results = new List<NetworkFirewallProfileStatus>();
        foreach (var name in ProfileNames)
        {
            var p = profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                results.Add(new NetworkFirewallProfileStatus(name, null, "Unknown", "Unknown", false, null));
                continue;
            }

            bool outboundBlocked = p.DefaultOutboundAction.Equals("Block", StringComparison.OrdinalIgnoreCase);
            string? reason = p.Enabled == false
                ? "This profile is turned off - no filtering applies while it's active."
                : outboundBlocked
                    ? "Default outbound action isn't Allow - outbound traffic is blocked unless a rule explicitly permits it. Unusual outside a deliberately locked-down environment. Quick flag, not a verdict."
                    : null;
            results.Add(new NetworkFirewallProfileStatus(name, p.Enabled, p.DefaultInboundAction, p.DefaultOutboundAction,
                p.Enabled == false || outboundBlocked, reason));
        }
        return results;
    }

    /// <summary>Throws when the rule set couldn't be read (timeout, non-zero exit, or output with
    /// no parseable rule block) - an empty list here must mean "netsh listed no rules", never
    /// "netsh failed", since both callers present the count as a fact.
    /// The full list (disabled rules included) comes from the Local store; `monitor show firewall
    /// rule` lists the active policy's enabled rules for the current profile only, so any rule it
    /// has that the Local store lacks (Group Policy/MDM-deployed) is appended.</summary>
    public static async Task<NetworkFirewallRuleSet> ReadRulesAsync()
    {
        var (output, exitCode) = await RunNetshAsync("advfirewall firewall show rule name=all verbose", RulesTimeoutMs);
        if (exitCode is null)
            throw new InvalidOperationException($"netsh timed out after {RulesTimeoutMs / 1000} s.");
        var rules = ParseRules(output);
        if (rules.Count == 0)
        {
            string text = output.Trim();
            throw new InvalidOperationException(exitCode != 0
                ? (text.Length > 0 ? text : $"netsh exited with code {exitCode}.")
                : "netsh printed no rule this parser recognizes (non-English netsh output isn't supported).");
        }

        var (activeOutput, activeExit) = await RunNetshAsync("advfirewall monitor show firewall rule name=all verbose", RulesTimeoutMs);
        var activeRules = activeExit == 0 ? ParseRules(activeOutput) : new List<NetworkFirewallRuleInfo>();
        if (activeRules.Count == 0)
        {
            string why = activeExit is null ? $"timed out after {RulesTimeoutMs / 1000} s" : $"netsh exited with code {activeExit}";
            return new NetworkFirewallRuleSet(rules, 0,
                $"The active-policy rule list couldn't be read ({why}) - rules deployed by Group Policy or MDM may be missing.");
        }

        // Source isn't part of the key: the Local store's own rules print "Local Setting" there.
        static string Key(NetworkFirewallRuleInfo r) => string.Join("\u001f",
            r.Name, r.Direction, r.Action, r.Protocol, r.LocalPort, r.RemotePort, r.ApplicationPath, r.Enabled, r.Profiles, r.Grouping);
        var known = new HashSet<string>(rules.Select(Key), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var r in activeRules)
        {
            if (!known.Add(Key(r))) continue;
            rules.Add(r);
            added++;
        }
        return new NetworkFirewallRuleSet(rules, added, null);
    }

    /// <summary>#567: the filter box (free-text over name/program/protocol/grouping) and the
    /// "show only rules for this executable" mode (exact-ish match on the rule's Program value) -
    /// one shared helper so the Firewall card and the #570 wizard (which always wants the
    /// executable-only mode) apply identical matching logic.</summary>
    public static List<NetworkFirewallRuleInfo> FilterRules(IEnumerable<NetworkFirewallRuleInfo> rules, string? filterText, bool executableOnly)
    {
        if (string.IsNullOrWhiteSpace(filterText)) return rules.ToList();
        string needle = filterText.Trim();

        if (executableOnly)
        {
            return rules.Where(r => r.ApplicationPath.Length > 0 &&
                (Path.GetFileName(r.ApplicationPath).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                 r.ApplicationPath.Contains(needle, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        return rules.Where(r =>
            r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.ApplicationPath.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.Protocol.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.Grouping.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<NetworkFirewallRuleInfo> ParseRules(string output)
    {
        var rules = new List<NetworkFirewallRuleInfo>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (block.Count == 0) return;
            if (block.TryGetValue("Rule Name", out var name) && name.Length > 0)
            {
                rules.Add(new NetworkFirewallRuleInfo(
                    name,
                    block.GetValueOrDefault("Direction", "Unknown"),
                    block.GetValueOrDefault("Action", "Unknown"),
                    block.GetValueOrDefault("Protocol", "Any"),
                    block.GetValueOrDefault("LocalPort", string.Empty),
                    block.GetValueOrDefault("RemotePort", string.Empty),
                    block.GetValueOrDefault("Program", string.Empty),
                    block.GetValueOrDefault("Enabled", string.Empty).Equals("Yes", StringComparison.OrdinalIgnoreCase),
                    block.GetValueOrDefault("Profiles", string.Empty),
                    block.GetValueOrDefault("Grouping", string.Empty),
                    block.GetValueOrDefault("Rule source", "Local")));
            }
            block.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            int idx = line.IndexOf(':');
            if (idx < 0) continue; // the "----" separator line under "Rule Name:", or an unrecognized line
            string key = line[..idx].Trim();
            string value = line[(idx + 1)..].Trim();
            if (key.Length == 0) continue;
            block[key] = value;
        }
        Flush();

        return rules;
    }

    /// <summary>ExitCode null = timed out (Output empty); a start failure reads as exit code -1.</summary>
    private static async Task<(string Output, int? ExitCode)> RunNetshAsync(string arguments, int timeoutMs)
    {
        try
        {
            return await ToolRunner.RunCapturedAsync("netsh.exe", arguments, timeoutMs, timeoutOutput: string.Empty);
        }
        catch (Exception ex)
        {
            return ($"Couldn't run netsh.exe: {ex.Message}", -1);
        }
    }
}
