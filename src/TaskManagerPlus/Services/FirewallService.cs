using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #881/#882: "Network exposure" section of the Security tab - firewall profile posture
/// (per-profile Domain/Private/Public state + which profile each adapter is currently on) and an
/// inbound-allow-rule audit. Both read via <c>root\StandardCimv2</c> WMI where that's clean
/// (MSFT_NetFirewallProfile/MSFT_NetConnectionProfile for #881), and via netsh text parsing where
/// the WMI route would mean an awkward associated-class join (#882's enabled inbound allow rules -
/// MSFT_NetFirewallRule's Program/Port filters are separate associated instances joined by
/// InstanceID, which "netsh advfirewall firewall show rule name=all verbose" sidesteps entirely
/// with one well-known, stable block-per-rule text format) - the same "prefer the known tool when
/// the structured API is awkward" call CLAUDE.md documents for schtasks/sc/vssadmin elsewhere.
/// On-demand only, reached from the Security tab's "Network exposure" section - never on a timer.
/// </summary>
public static class FirewallService
{
    // ==================================================================================
    // #881: per-profile firewall posture + which profile each adapter is currently on.
    // ==================================================================================

    public sealed class FirewallProfileInfo
    {
        public string Name { get; init; } = string.Empty;
        public bool? Enabled { get; init; }
        public string DefaultInboundAction { get; init; } = "Unknown";
        public string DefaultOutboundAction { get; init; } = "Unknown";
        public bool? NotifyOnListen { get; init; }
        public bool? LogAllowed { get; init; }
        public bool? LogBlocked { get; init; }

        public string EnabledText => Enabled switch { true => "On", false => "Off", null => "Unknown" };
        public bool LooksRisky => Enabled == false || DefaultInboundAction.Equals("Allow", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record AdapterFirewallProfileInfo(string InterfaceAlias, string ProfileName);

    private static string ActionName(object? raw) => raw switch
    {
        null => "Unknown",
        _ when Convert.ToInt32(raw) == 2 => "Allow",
        _ when Convert.ToInt32(raw) == 4 => "Block",
        _ when Convert.ToInt32(raw) == 0 => "NotConfigured",
        _ => $"Unknown ({raw})",
    };

    /// <summary>Reads per-profile posture + adapter-to-profile mapping in one pass. Returns an
    /// empty profile list (never throws) when the WMI namespace/class is unavailable - "Unknown"
    /// is shown per CLAUDE.md's "degrade, never fabricate" rule rather than assuming a default.</summary>
    public static (List<FirewallProfileInfo> Profiles, List<AdapterFirewallProfileInfo> AdapterProfiles) ReadPosture()
    {
        var profiles = new List<FirewallProfileInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Name, Enabled, DefaultInboundAction, DefaultOutboundAction, NotifyOnListen, LogAllowed, LogBlocked FROM MSFT_NetFirewallProfile");
            foreach (ManagementObject mo in searcher.Get())
            {
                profiles.Add(new FirewallProfileInfo
                {
                    Name = mo["Name"] as string ?? "Unknown",
                    Enabled = TryBool(mo, "Enabled"),
                    DefaultInboundAction = ActionName(TryValue(mo, "DefaultInboundAction")),
                    DefaultOutboundAction = ActionName(TryValue(mo, "DefaultOutboundAction")),
                    NotifyOnListen = TryBool(mo, "NotifyOnListen"),
                    LogAllowed = TryBool(mo, "LogAllowed"),
                    LogBlocked = TryBool(mo, "LogBlocked"),
                });
            }
        }
        catch
        {
            // Namespace/class unavailable (very old Windows, or WMI repository issue) - empty list,
            // the UI shows "couldn't read firewall profile posture" rather than fabricating rows.
        }

        var adapterProfiles = new List<AdapterFirewallProfileInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile");
            foreach (ManagementObject mo in searcher.Get())
            {
                string alias = mo["InterfaceAlias"] as string ?? "Unknown";
                int category = TryValue(mo, "NetworkCategory") is { } c ? Convert.ToInt32(c) : -1;
                string profileName = category switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => "Unknown" };
                adapterProfiles.Add(new AdapterFirewallProfileInfo(alias, profileName));
            }
        }
        catch
        {
            // Same degrade-to-empty as above.
        }

        return (profiles, adapterProfiles);
    }

    /// <summary>Medium/High finding per profile that's disabled OR whose default inbound action is
    /// Allow - either shape means "this profile lets unsolicited inbound traffic through by
    /// default," the two most consequential firewall-posture mistakes.</summary>
    public static List<SecurityFinding> BuildProfileFindings(List<FirewallProfileInfo> profiles)
    {
        var findings = new List<SecurityFinding>();
        foreach (var p in profiles)
        {
            if (p.Enabled == false)
            {
                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.High,
                    Title = $"Windows Firewall is OFF for the {p.Name} profile",
                    Reason = $"MSFT_NetFirewallProfile reports Enabled=False for the {p.Name} profile - Windows Firewall provides no inbound/outbound filtering at all while a network is on this profile.",
                    Path = $"Windows Defender Firewall > {p.Name} profile",
                    WhatDisablingDoes = "Turn the firewall back on for this profile via Windows Security > Firewall & network protection, unless another firewall (a router/UTM, or third-party host firewall) is deliberately covering this role instead.",
                });
            }
            else if (p.DefaultInboundAction.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.Medium,
                    Title = $"Default inbound action is Allow for the {p.Name} profile",
                    Reason = $"MSFT_NetFirewallProfile reports DefaultInboundAction=Allow for the {p.Name} profile - Windows Firewall's default posture is to let unsolicited inbound connections through unless a specific Block rule stops them, the opposite of the normal \"block by default, allow by exception\" model.",
                    Path = $"Windows Defender Firewall > {p.Name} profile > default inbound action",
                    WhatDisablingDoes = "Set the default inbound action back to Block via Windows Security > Firewall & network protection > (profile) > Advanced settings, unless this was deliberately set for a specific, understood reason.",
                });
            }
        }
        return findings;
    }

    // ==================================================================================
    // #882: enabled inbound allow rule audit, via netsh text parsing (preferred over the WMI
    // associated-class join per this file's own class remarks).
    // ==================================================================================

    public sealed class FirewallRuleInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Direction { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Profiles { get; init; } = string.Empty;
        public string Program { get; init; } = string.Empty;
        public string LocalPort { get; init; } = string.Empty;
        public string RemotePort { get; init; } = string.Empty;
        public string RemoteIp { get; init; } = string.Empty;
        public string Grouping { get; init; } = string.Empty;
        public List<string> RiskReasons { get; init; } = new();
        public bool IsRisky => RiskReasons.Count > 0;
        public string RiskReasonsText => RiskReasons.Count == 0 ? string.Empty : string.Join("; ", RiskReasons);
    }

    // Groupings netsh prints for Microsoft's own built-in rules are almost always either empty or
    // an "@shell32.dll,-..." / "@FirewallAPI.dll,-..." MUI resource-string reference - a resource
    // string reference is the "known Microsoft" shape; anything else non-empty is loosely flagged
    // as "possibly non-Microsoft," per #882's own explicit "be loose, false positives are fine" text.
    private static bool LooksLikeMicrosoftGroupResourceString(string grouping) =>
        grouping.Length == 0 || grouping.StartsWith('@');

    private static readonly Regex FieldLineRegex = new(@"^([A-Za-z][A-Za-z ]{0,40}):\s+(.*)$", RegexOptions.Compiled);

    /// <summary>Enumerates ENABLED inbound ALLOW rules only (per #882's own scope) by shelling
    /// "netsh advfirewall firewall show rule name=all verbose" and parsing its stable
    /// block-per-rule text output, then flags risky shapes on each one. "Created recently" is not
    /// computed - netsh's output carries no rule-creation timestamp - see this method's caller for
    /// the Implemented-partially note.</summary>
    public static List<FirewallRuleInfo> ScanEnabledInboundAllowRules()
    {
        string output;
        try { output = RunCapturedSync("netsh.exe", "advfirewall firewall show rule name=all verbose", TimeSpan.FromSeconds(30)); }
        catch { return new List<FirewallRuleInfo>(); }
        if (string.IsNullOrWhiteSpace(output)) return new List<FirewallRuleInfo>();

        var results = new List<FirewallRuleInfo>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushIfInboundAllow()
        {
            if (current.Count == 0) return;
            bool enabled = (current.GetValueOrDefault("Enabled") ?? string.Empty).Equals("Yes", StringComparison.OrdinalIgnoreCase);
            string direction = current.GetValueOrDefault("Direction") ?? string.Empty;
            string action = current.GetValueOrDefault("Action") ?? string.Empty;
            if (enabled && direction.Equals("In", StringComparison.OrdinalIgnoreCase) && action.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                results.Add(BuildRuleInfo(current));
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
            {
                FlushIfInboundAllow();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (line.All(c => c == '-')) continue; // the dashed separator line under "Rule Name:"

            var m = FieldLineRegex.Match(line);
            if (m.Success) current[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
        }
        FlushIfInboundAllow();

        return results;
    }

    private static FirewallRuleInfo BuildRuleInfo(Dictionary<string, string> fields)
    {
        string name = fields.GetValueOrDefault("Rule Name") ?? "(unnamed)";
        string program = fields.GetValueOrDefault("Program") ?? string.Empty;
        string localPort = fields.GetValueOrDefault("LocalPort") ?? string.Empty;
        string remotePort = fields.GetValueOrDefault("RemotePort") ?? string.Empty;
        string remoteIp = fields.GetValueOrDefault("RemoteIP") ?? string.Empty;
        string profiles = fields.GetValueOrDefault("Profiles") ?? string.Empty;
        string grouping = fields.GetValueOrDefault("Grouping") ?? string.Empty;

        var reasons = new List<string>();

        if (program.Length == 0 || program.Equals("Any", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Allows any program, not scoped to one executable");

        if (localPort.Equals("Any", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Allows any local port");

        if (program.Length > 0 && !program.Equals("Any", StringComparison.OrdinalIgnoreCase) &&
            !program.Equals("System", StringComparison.OrdinalIgnoreCase) &&
            program.Contains('\\'))
        {
            try
            {
                if (!File.Exists(Environment.ExpandEnvironmentVariables(program)))
                    reasons.Add("Program path no longer exists on disk");
            }
            catch { /* malformed path string - not itself worth a finding */ }
        }

        if (remoteIp.Equals("Any", StringComparison.OrdinalIgnoreCase) &&
            profiles.Contains("Public", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Allows any remote address on the Public profile");

        if (!LooksLikeMicrosoftGroupResourceString(grouping))
            reasons.Add($"Grouping \"{grouping}\" doesn't look like a Microsoft resource-string group - possibly a third-party rule");

        return new FirewallRuleInfo
        {
            Name = name,
            Direction = fields.GetValueOrDefault("Direction") ?? string.Empty,
            Action = fields.GetValueOrDefault("Action") ?? string.Empty,
            Profiles = profiles,
            Program = program,
            LocalPort = localPort,
            RemotePort = remotePort,
            RemoteIp = remoteIp,
            Grouping = grouping,
            RiskReasons = reasons,
        };
    }

    /// <summary>Returns whether a port number is covered by a rule's LocalPort field text ("Any",
    /// a single number, a comma list, or "start-end" ranges) - used by #883's exposed-listener
    /// cross-reference.</summary>
    public static bool RuleCoversLocalPort(FirewallRuleInfo rule, int port)
    {
        if (rule.LocalPort.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in rule.LocalPort.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var single) && single == port) return true;
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi) && port >= lo && port <= hi)
                return true;
        }
        return false;
    }

    /// <summary>#882: per-rule Disable action - NEVER delete, matching this section's own text.
    /// Note the rule name is not guaranteed unique (several built-in rules commonly share a
    /// DisplayName across profiles) - "set rule name=" applies to every rule with that exact name,
    /// the same ambiguity netsh itself has.</summary>
    public static (bool Success, string Output) DisableRule(string exactRuleName)
    {
        try
        {
            string output = RunCapturedSync("netsh.exe", $"advfirewall firewall set rule name=\"{exactRuleName}\" new enable=no", TimeSpan.FromSeconds(15));
            return (output.Contains("Ok", StringComparison.OrdinalIgnoreCase), output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#882: same-session "Undo" - re-enables a rule this app disabled. A full persistent
    /// action journal is item #899's job, not this one - see SecurityViewModel's in-memory
    /// DisabledFirewallRuleNames list.</summary>
    public static (bool Success, string Output) EnableRule(string exactRuleName)
    {
        try
        {
            string output = RunCapturedSync("netsh.exe", $"advfirewall firewall set rule name=\"{exactRuleName}\" new enable=yes", TimeSpan.FromSeconds(15));
            return (output.Contains("Ok", StringComparison.OrdinalIgnoreCase), output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static object? TryValue(ManagementBaseObject mo, string name)
    {
        try { return mo[name]; } catch { return null; }
    }

    private static bool? TryBool(ManagementBaseObject mo, string name)
    {
        try { return mo[name] as bool?; } catch { return null; }
    }

    /// <summary>Synchronous shell-out-and-capture - same kill-on-timeout shape as
    /// AutorunsService.RunCapturedSync/PlatformSecurityService's local copy; not shared directly
    /// since both are private to their own files.</summary>
    private static string RunCapturedSync(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return string.Empty;
        }

        return outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
    }
}
