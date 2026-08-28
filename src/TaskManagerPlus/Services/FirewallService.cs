using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>One Windows Firewall profile's headline state (#566) - Domain/Private/Public, parsed
/// from `netsh advfirewall show allprofiles`. <see cref="Enabled"/> is null when the profile's
/// block couldn't be found/parsed at all (degrade to Unknown, never guess). <see cref="IsFlagged"/>
/// is true when the profile is off, or its default outbound action isn't Windows' own default
/// (Allow) - a quick flag, not a verdict: a Block-by-default outbound policy is a deliberate,
/// legitimate lockdown in some environments.</summary>
public sealed record FirewallProfileStatus(
    string ProfileName, bool? Enabled, string InboundPolicy, string OutboundPolicy, bool IsFlagged, string? FlagReason)
{
    /// <summary>Plain string form of <see cref="Enabled"/> for the view - avoids a bool?-to-text
    /// converter for a value that never changes after construction, same "compute it once in C#"
    /// tradeoff AdapterHealthRow's own ArpOffloadText/WakeOnMagicPacketText already take.</summary>
    public string EnabledText => Enabled switch { true => "ON", false => "OFF", null => "Unknown" };
}

/// <summary>One firewall rule (#567), from one `netsh advfirewall firewall show rule name=all
/// verbose` block. <see cref="ApplicationPath"/> is empty when the rule isn't scoped to a specific
/// program.</summary>
public sealed record FirewallRuleInfo(
    string Name, string Direction, string Action, string Protocol,
    string LocalPort, string RemotePort, string ApplicationPath, bool Enabled, string Profiles, string Grouping);

/// <summary>
/// Items #566/#567 (suggestions.md "Firewall rules and blocked connections"): profile status and
/// the full rule set. Deliberately parses `netsh advfirewall`'s own English-locale text output
/// rather than the HNetCfg.FwPolicy2 COM object these items' text suggests -
/// `netsh advfirewall firewall show rule name=all verbose` already prints one clean "Field: value"
/// block per rule covering every column both items ask for (name/direction/action/protocol/ports/
/// program/enabled state), so COM interop here would only add real build risk (either a design-
/// time-generated interop assembly this build has no Windows SDK tlbimp step to produce, or
/// late-bound `dynamic` COM calls needing an extra Microsoft.CSharp package reference just to
/// foreach a COM collection) for no parsing benefit over the pattern every other network diagnostic
/// in this app already uses. Same documented "English-locale field labels" limitation as
/// WifiDiagnosticsService/DnsConfigService/TcpGlobalSettingsService's own netsh parses.
/// </summary>
public static class FirewallService
{
    private const int TimeoutMs = 20000;
    private const int RulesTimeoutMs = 45000; // a few hundred rules is common; verbose output can run to several thousand lines
    private static readonly string[] ProfileNames = { "Domain", "Private", "Public" };

    public static async Task<List<FirewallProfileStatus>> ReadProfileStatusAsync()
    {
        string output = await RunNetshAsync("advfirewall show allprofiles", TimeoutMs);
        return ParseProfiles(output);
    }

    public static async Task<List<FirewallRuleInfo>> ReadRulesAsync()
    {
        string output = await RunNetshAsync("advfirewall firewall show rule name=all verbose", RulesTimeoutMs);
        return ParseRules(output);
    }

    /// <summary>#567: the filter box (free-text over name/program/protocol/grouping) and the
    /// "show only rules for this executable" mode (exact-ish match on the rule's Program value) -
    /// one shared helper so the Firewall card and the #570 wizard (which always wants the
    /// executable-only mode) apply identical matching logic.</summary>
    public static List<FirewallRuleInfo> FilterRules(IEnumerable<FirewallRuleInfo> rules, string? filterText, bool executableOnly)
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

    private static List<FirewallProfileStatus> ParseProfiles(string output)
    {
        var results = new List<FirewallProfileStatus>();
        foreach (var name in ProfileNames)
        {
            string? block = ExtractProfileBlock(output, name);
            if (block is null)
            {
                results.Add(new FirewallProfileStatus(name, null, "Unknown", "Unknown", false, null));
                continue;
            }

            bool? enabled = ExtractField(block, "State") switch
            {
                "ON" => true,
                "OFF" => false,
                _ => null,
            };

            string policy = ExtractField(block, "Firewall Policy") ?? string.Empty;
            var parts = policy.Split(',', StringSplitOptions.TrimEntries);
            string inbound = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "Unknown";
            string outbound = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Unknown";

            bool flagged = enabled == false || !outbound.Equals("AllowOutbound", StringComparison.OrdinalIgnoreCase);
            string? reason = enabled == false
                ? "This profile is turned off - no filtering applies while it's active."
                : !outbound.Equals("AllowOutbound", StringComparison.OrdinalIgnoreCase) && outbound != "Unknown"
                    ? "Default outbound action isn't Allow - outbound traffic is blocked unless a rule explicitly permits it. Unusual outside a deliberately locked-down environment. Quick flag, not a verdict."
                    : null;

            results.Add(new FirewallProfileStatus(name, enabled, inbound, outbound, flagged, reason));
        }
        return results;
    }

    private static string? ExtractProfileBlock(string output, string profileName)
    {
        string marker = $"{profileName} Profile Settings:";
        int start = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;

        int end = output.Length;
        foreach (var other in ProfileNames)
        {
            if (other == profileName) continue;
            int idx = output.IndexOf($"{other} Profile Settings:", start, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < end) end = idx;
        }
        return output[start..end];
    }

    /// <summary>Field extraction for the "show allprofiles" block, which (unlike "show rule
    /// verbose") uses whitespace-aligned columns rather than a colon separator - matches the label
    /// at the start of a trimmed line, guarding against a longer label incidentally starting with
    /// the requested one.</summary>
    private static string? ExtractField(string block, string label)
    {
        foreach (var rawLine in block.Split('\n'))
        {
            string trimmed = rawLine.TrimEnd('\r').TrimStart();
            if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Length > label.Length && char.IsLetterOrDigit(trimmed[label.Length])) continue;

            string rest = trimmed[label.Length..].Trim();
            if (rest.Length == 0) continue;
            return rest;
        }
        return null;
    }

    private static List<FirewallRuleInfo> ParseRules(string output)
    {
        var rules = new List<FirewallRuleInfo>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (block.Count == 0) return;
            if (block.TryGetValue("Rule Name", out var name) && name.Length > 0)
            {
                rules.Add(new FirewallRuleInfo(
                    name,
                    block.GetValueOrDefault("Direction", "Unknown"),
                    block.GetValueOrDefault("Action", "Unknown"),
                    block.GetValueOrDefault("Protocol", "Any"),
                    block.GetValueOrDefault("LocalPort", string.Empty),
                    block.GetValueOrDefault("RemotePort", string.Empty),
                    block.GetValueOrDefault("Program", string.Empty),
                    block.GetValueOrDefault("Enabled", string.Empty).Equals("Yes", StringComparison.OrdinalIgnoreCase),
                    block.GetValueOrDefault("Profiles", string.Empty),
                    block.GetValueOrDefault("Grouping", string.Empty)));
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

    private static async Task<string> RunNetshAsync(string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return string.Empty;
            }

            return (await outputTask) + (await errorTask);
        }
        catch
        {
            return string.Empty;
        }
    }
}
