using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One `netsh dns show encryption` row (#520) - best-effort parse of an admittedly
/// obscure, lightly-documented table format. <see cref="EncryptionStatus"/>/<see cref="AutoUpgrade"/>/
/// <see cref="UdpFallback"/> are shown verbatim as netsh printed them rather than normalized into a
/// bool, since this app has no authoritative source for exactly which strings that command can
/// print across Windows builds - degrade to showing the raw column text, never fabricate a
/// true/false it isn't sure of.</summary>
public sealed record DohServerStatus(string InterfaceName, string ServerAddress, string EncryptionStatus, string AutoUpgrade, string UdpFallback);

/// <summary>#520's combined result - the parsed table above (best-effort) plus the untouched raw
/// `netsh dns show encryption` text (always shown too, so nothing is lost if the parse comes up
/// short) and whether any interface actually has a DohInterfaceSettings registry key at all.</summary>
public sealed record DohConfigResult(List<DohServerStatus> Parsed, string RawOutput, bool RegistryKeyPresent, bool CommandSupported);

/// <summary>One NRPT rule (#521) - <see cref="Namespaces"/> is the list of suffixes/namespaces this
/// rule's DNS servers apply to; <see cref="DnsServers"/> is whichever of GenericDNSServers /
/// DirectAccessDNSServers the rule actually set (comma-separated, as the registry stores it).</summary>
public sealed record NrptRule(string RuleId, List<string> Namespaces, string DnsServers);

/// <summary>One adapter's own DNS suffix (#523) - a plain record (not a value tuple) so it binds
/// cleanly from XAML, which resolves properties by name and can't see a tuple's compile-time-only
/// element names.</summary>
public sealed record AdapterSuffixInfo(string AdapterName, string Suffix);

/// <summary>#523's suffix/search-list snapshot.</summary>
public sealed record DnsSuffixInfo(string PrimarySuffix, List<string> SearchList, List<AdapterSuffixInfo> AdapterSuffixes);

/// <summary>
/// Items #520/#521/#523 (suggestions.md "DNS resolution, cache and configuration"): three
/// read-only DNS *configuration* snapshots, grouped into one service since none of them poll and
/// none of them write anything - encrypted-DNS status (#520), split-DNS policy rules a VPN or
/// group policy pushed (#521), and the suffix/search list that decides how a short internal name
/// gets expanded (#523). All three degrade to an empty result (hidden section, or "Unknown") on a
/// denied key/unsupported command rather than guessing, per this app's own convention.
/// </summary>
public static class DnsConfigService
{
    // Windows 11's per-interface DoH settings - see item #520's own spec. This app only checks
    // whether the key/subtree exists at all (a coarse "has this interface had DoH configured
    // through it" signal); the actual per-server DohFlags bitmask underneath it is undocumented,
    // so rather than guess at bit meanings this app leans on `netsh dns show encryption`'s own
    // human-readable output (read below) as the source of truth for the actual on/off/required
    // state, same "undocumented bitmask -> prefer the tool's own text" tradeoff
    // MeteredConnectionService's remarks describe for a different registry value.
    private const string InterfaceParamsKeyPath = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    /// <summary>#520: DoH configuration - `netsh dns show encryption` cross-checked against the
    /// per-interface registry key's mere presence.</summary>
    public static async Task<DohConfigResult> ReadDohConfigAsync()
    {
        bool registryPresent = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(InterfaceParamsKeyPath);
            if (key is not null)
            {
                foreach (var guid in key.GetSubKeyNames())
                {
                    try
                    {
                        using var iface = key.OpenSubKey(guid);
                        using var doh = iface?.OpenSubKey("DohInterfaceSettings");
                        if (doh is not null) { registryPresent = true; break; }
                    }
                    catch { /* one malformed subkey shouldn't stop the scan */ }
                }
            }
        }
        catch
        {
            // Denied/absent key - registryPresent stays false, not a crash.
        }

        string raw;
        bool commandSupported;
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", "dns show encryption")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new DohConfigResult(new(), string.Empty, registryPresent, false);

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(); } catch { /* best-effort */ } }

            raw = ((await outputTask) + (await errorTask)).Trim();
            // Older Windows builds don't recognize the "dns" netsh context at all - netsh's own
            // "The following command was not found" text is how that shows up.
            commandSupported = raw.Length > 0 &&
                !raw.Contains("was not found", StringComparison.OrdinalIgnoreCase) &&
                !raw.Contains("invalid", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return new DohConfigResult(new(), $"Couldn't run netsh: {ex.Message}", registryPresent, false);
        }

        var parsed = commandSupported ? ParseEncryptionTable(raw) : new List<DohServerStatus>();
        return new DohConfigResult(parsed, raw, registryPresent, commandSupported);
    }

    /// <summary>Best-effort table parse - splits into per-interface blocks on netsh's own
    /// "Configuration for interface" header line (English-locale text, same documented limitation
    /// WifiDiagnosticsService's netsh parse already carries), then splits each data row on runs of
    /// 2+ spaces, the common fixed-width-table shape netsh's other `show` commands use elsewhere in
    /// this app (RoutingTableService, InterfaceMtuService). A row whose first token isn't a
    /// plausible IP is skipped rather than guessed at.</summary>
    private static List<DohServerStatus> ParseEncryptionTable(string output)
    {
        var results = new List<DohServerStatus>();
        string currentInterface = string.Empty;

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Trim().Length == 0) continue;

            if (line.Contains("Configuration for interface", StringComparison.OrdinalIgnoreCase))
            {
                int quoteStart = line.IndexOf('"');
                int quoteEnd = line.LastIndexOf('"');
                currentInterface = quoteStart >= 0 && quoteEnd > quoteStart
                    ? line[(quoteStart + 1)..quoteEnd]
                    : line.Trim();
                continue;
            }
            if (line.Contains("DNS Servers", StringComparison.OrdinalIgnoreCase) && line.Contains("Encryption", StringComparison.OrdinalIgnoreCase))
                continue; // header row
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal)) continue;

            var tokens = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
            if (tokens.Length == 0) continue;
            if (!System.Net.IPAddress.TryParse(tokens[0], out _)) continue;

            results.Add(new DohServerStatus(
                currentInterface,
                tokens[0],
                tokens.Length > 1 ? tokens[1] : "Unknown",
                tokens.Length > 2 ? tokens[2] : "Unknown",
                tokens.Length > 3 ? tokens[3] : "Unknown"));
        }
        return results;
    }

    // #521: split-DNS rules a VPN client or group policy pushes - the reason internal hostnames
    // resolve on VPN and nothing else does, or vice versa. Each subkey under DnsPolicyConfig is
    // one rule (subkey name is usually a GUID, not meant to be shown to the user directly).
    private const string NrptPolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig";

    public static List<NrptRule> ReadNrptRules()
    {
        var rules = new List<NrptRule>();
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(NrptPolicyKeyPath);
            if (policyKey is null) return rules;

            foreach (var ruleId in policyKey.GetSubKeyNames())
            {
                try
                {
                    using var rule = policyKey.OpenSubKey(ruleId);
                    if (rule is null) continue;

                    var namespaces = rule.GetValue("Name") switch
                    {
                        string[] arr => arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
                        string s when !string.IsNullOrWhiteSpace(s) => new List<string> { s },
                        _ => new List<string>(),
                    };
                    if (namespaces.Count == 0) continue; // not a namespace-routing rule this app can meaningfully show

                    string servers = (rule.GetValue("GenericDNSServers") as string)?.Trim() ?? string.Empty;
                    if (servers.Length == 0)
                        servers = (rule.GetValue("DirectAccessDNSServers") as string)?.Trim() ?? string.Empty;

                    rules.Add(new NrptRule(ruleId, namespaces, servers.Length > 0 ? servers : "(no server override - rule has other effects, e.g. proxy or DirectAccess settings)"));
                }
                catch
                {
                    // One malformed rule subkey shouldn't stop the rest of the enumeration.
                }
            }
        }
        catch
        {
            // Denied/absent key - most machines have no NRPT policy at all, and the view hides
            // this expander entirely on an empty list, so this is the common case, not an error.
        }
        return rules;
    }

    // #523: suffix/search list.
    private const string TcpipParamsKeyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    public static DnsSuffixInfo ReadSuffixInfo()
    {
        string primary = string.Empty;
        var adapterSuffixes = new List<AdapterSuffixInfo>();
        try
        {
            primary = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName ?? string.Empty;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                string suffix = ni.GetIPProperties().DnsSuffix;
                if (!string.IsNullOrWhiteSpace(suffix)) adapterSuffixes.Add(new AdapterSuffixInfo(ni.Name, suffix));
            }
        }
        catch
        {
            // Best-effort - primary/adapterSuffixes just stay at whatever was gathered.
        }

        var searchList = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TcpipParamsKeyPath);
            if (key?.GetValue("SearchList") is string raw && !string.IsNullOrWhiteSpace(raw))
                searchList = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            // Denied/absent value - empty search list, not a crash.
        }

        return new DnsSuffixInfo(primary, searchList, adapterSuffixes);
    }
}
