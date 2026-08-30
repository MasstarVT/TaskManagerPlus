using System.Diagnostics;
using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #888: DNS posture - per-adapter configured DNS servers cross-referenced against DHCP
/// status (the "static DNS on an otherwise-DHCP adapter" shape a DNS-hijack leaves, per this
/// item's own text), DNS-over-HTTPS template state, and NRPT policy rules. Lands as a card on the
/// Network tab (NetworkViewModel calls <see cref="ReadPosture"/> from its own on-demand button,
/// NOT the tab's existing 15s polling timer - the PowerShell cmdlet calls here are too heavy for
/// that) with the static-DNS-on-DHCP finding mirrored to the Security tab's "Network exposure"
/// section (SecurityViewModel independently calls <see cref="ReadPosture"/> + <see
/// cref="BuildFindings"/> the same "shared static service method called from two ViewModels"
/// pattern this app already uses elsewhere, e.g. SignatureCheckService).
/// </summary>
public static class DnsPostureService
{
    public sealed record DnsAdapterInfo(string Description, bool DhcpEnabled, List<string> ConfiguredServers, bool StaticDnsOnDhcpAdapter)
    {
        public string DhcpEnabledText => DhcpEnabled ? "Yes" : "No";
    }
    public sealed record DohServerInfo(string ServerAddress, string DohTemplate, string AutoUpgradeText);

    public sealed class DnsPostureResult
    {
        public List<DnsAdapterInfo> Adapters { get; init; } = new();
        public List<DohServerInfo> DohServers { get; init; } = new();
        public bool DohCmdletAvailable { get; init; }
        public List<string> NrptRules { get; init; } = new();
        public bool NrptCmdletAvailable { get; init; }

        public string DohAvailabilityText => DohCmdletAvailable ? "Read" : "Not available on this Windows build";
        public string NrptAvailabilityText => NrptCmdletAvailable ? "Read" : "Cmdlet not available";
    }

    /// <summary>Per-adapter DNS servers + DHCP status via Win32_NetworkAdapterConfiguration - the
    /// simpler, reliable proxy #888's own text calls out in place of a full
    /// Get-DnsClientServerAddress-vs-DHCP-lease comparison. DoH/NRPT still need the PowerShell
    /// cmdlets below (no WMI/registry equivalent) - both degrade gracefully (empty list, "cmdlet
    /// not available") on a Windows build that doesn't ship them.</summary>
    public static DnsPostureResult ReadPosture()
    {
        var adapters = new List<DnsAdapterInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DHCPEnabled, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementObject mo in searcher.Get())
            {
                string desc = mo["Description"] as string ?? "Unknown adapter";
                bool dhcp = mo["DHCPEnabled"] is bool b && b;
                var servers = (mo["DNSServerSearchOrder"] as string[])?.ToList() ?? new List<string>();
                bool staticOnDhcp = dhcp && servers.Count > 0;
                adapters.Add(new DnsAdapterInfo(desc, dhcp, servers, staticOnDhcp));
            }
        }
        catch
        {
            // WMI unavailable/denied - empty adapter list, the UI shows "couldn't read DNS posture".
        }

        var (dohServers, dohAvailable) = ReadDohServers();
        var (nrptRules, nrptAvailable) = ReadNrptRules();

        return new DnsPostureResult
        {
            Adapters = adapters,
            DohServers = dohServers,
            DohCmdletAvailable = dohAvailable,
            NrptRules = nrptRules,
            NrptCmdletAvailable = nrptAvailable,
        };
    }

    /// <summary>Medium finding, mirrored to the Security tab, for the specific
    /// static-DNS-on-DHCP-adapter shape - exactly "the shape a DNS-hijack leaves," per #888's own
    /// text, not a general "you have custom DNS servers" complaint (which is extremely common and
    /// intentional, e.g. 1.1.1.1/8.8.8.8/a Pi-hole).</summary>
    public static List<SecurityFinding> BuildFindings(DnsPostureResult result)
    {
        var findings = new List<SecurityFinding>();
        foreach (var a in result.Adapters.Where(a => a.StaticDnsOnDhcpAdapter))
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.Medium,
                Title = $"Static DNS servers set on a DHCP-managed adapter: {a.Description}",
                Reason = $"\"{a.Description}\" gets its IP configuration from DHCP but has explicit DNS server(s) set anyway ({string.Join(", ", a.ConfiguredServers)}) - this specific shape (DHCP for everything else, DNS pinned separately) is exactly what a DNS-hijacking malware/router-compromise leaves behind, though it's also a common deliberate choice (e.g. pointing at 1.1.1.1/8.8.8.8/a Pi-hole). Quick flag, not a verdict - confirm you recognize these DNS servers.",
                Path = $"Network adapter: {a.Description}",
                WhatDisablingDoes = "If you don't recognize these DNS servers, switch the adapter back to \"Obtain DNS server address automatically\" in its adapter properties, or set the DNS servers you actually intend.",
            });
        }
        return findings;
    }

    private static (List<DohServerInfo> Servers, bool Available) ReadDohServers()
    {
        try
        {
            string output = RunCapturedSync("powershell.exe",
                "-NoProfile -Command \"Get-DnsClientDohServerAddress | Select-Object ServerAddress,DohTemplate,AutoUpgrade | ConvertTo-Csv -NoTypeInformation\"",
                TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(output)) return (new List<DohServerInfo>(), false);

            var rows = SimpleCsv.ParseRows(output);
            if (rows.Count == 0) return (new List<DohServerInfo>(), false);

            var header = rows[0];
            int addrIdx = header.FindIndex(h => h.Equals("ServerAddress", StringComparison.OrdinalIgnoreCase));
            int templateIdx = header.FindIndex(h => h.Equals("DohTemplate", StringComparison.OrdinalIgnoreCase));
            int autoIdx = header.FindIndex(h => h.Equals("AutoUpgrade", StringComparison.OrdinalIgnoreCase));
            if (addrIdx < 0) return (new List<DohServerInfo>(), false); // cmdlet ran but the shape wasn't what we expected

            var servers = new List<DohServerInfo>();
            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count <= addrIdx) continue;
                servers.Add(new DohServerInfo(
                    row[addrIdx],
                    templateIdx >= 0 && row.Count > templateIdx ? row[templateIdx] : string.Empty,
                    autoIdx >= 0 && row.Count > autoIdx ? row[autoIdx] : string.Empty));
            }
            return (servers, true);
        }
        catch
        {
            // Get-DnsClientDohServerAddress doesn't exist on this Windows build (it's not universal
            // even across Windows 11 builds) - degrade to "not available" rather than an error.
            return (new List<DohServerInfo>(), false);
        }
    }

    private static (List<string> Rules, bool Available) ReadNrptRules()
    {
        try
        {
            string output = RunCapturedSync("powershell.exe",
                "-NoProfile -Command \"Get-DnsClientNrptPolicy | Format-List | Out-String -Width 300\"",
                TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(output)) return (new List<string>(), true); // ran fine, nothing found - the common case

            var rules = output
                .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(block => block.Trim())
                .Where(block => block.Length > 0)
                .ToList();
            return (rules, true);
        }
        catch
        {
            return (new List<string>(), false);
        }
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/> (run tool, capture
    /// stdout+stderr, kill the whole process tree on timeout).</summary>
    private static string RunCapturedSync(string exe, string args, TimeSpan timeout)
        => ToolRunner.RunCaptured(exe, args, timeout).Output;
}
