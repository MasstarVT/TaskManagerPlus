using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>One IP-configured adapter's DHCP lease detail (#527) plus every flag layered on top of
/// the same Win32_NetworkAdapterConfiguration snapshot: #528's APIPA banner, and #534's addressing
/// sanity checklist. A mutable class (not a record) so <see cref="DhcpAddressingService.ReadAll"/>
/// can compute the derived fields after construction, the same shape RouteEntry/ArpEntry already
/// use for their own post-parse annotation passes.</summary>
public sealed class AdapterAddressInfo
{
    public string AdapterName { get; init; } = string.Empty; // NetworkInterface.Name - what ipconfig/netsh call it, and what #529's actions target
    public string Description { get; init; } = string.Empty; // WMI Description - shown as a subtitle
    public string MacAddress { get; init; } = string.Empty;
    public bool DhcpEnabled { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string SubnetMask { get; init; } = string.Empty;
    public string? DefaultGateway { get; init; }
    public string? DhcpServer { get; init; }
    public DateTime? LeaseObtained { get; init; }
    public DateTime? LeaseExpires { get; init; }

    // #527: derived lease presentation.
    public string LeaseStatusText { get; set; } = string.Empty;
    public bool LeaseExpiringSoon { get; set; }

    // #527: rogue-DHCP-server flag, from DhcpServerBaselineService - null when the serving server
    // matches what was last recorded for this adapter (or this is the first time it's been seen).
    public string? RogueDhcpReason { get; set; }

    // #528: APIPA / self-assigned address - DHCP was expected but nothing answered.
    public bool IsApipa { get; set; }

    // #534: addressing sanity checklist - each entry is one flagged issue's message; empty means clean.
    public List<string> SanityFlags { get; set; } = new();
}

/// <summary>
/// Items #527/#528/#529/#534 (suggestions.md "DHCP, addressing, ARP and gateway"): everything the
/// new Addressing card shows or acts on, all read from one Win32_NetworkAdapterConfiguration sweep
/// per <see cref="ReadAll"/> call so the four items don't each re-query WMI separately.
///
/// #527 (lease detail) reads DHCPEnabled/DHCPServer/DHCPLeaseObtained/DHCPLeaseExpires directly, the
/// same WMI class SystemSpecsService/NetworkDiagnosticsService already query elsewhere in this app
/// for other adapter facts - the "known API over raw interop" convention applies here too, since
/// there's no simpler tool whose text output covers all four lease fields together. #528 (APIPA) and
/// #534 (sanity checks) are pure derivations from that same snapshot plus #527's persisted DHCP
/// baseline (DhcpServerBaselineService) - no extra I/O. #529 (release/renew/register-DNS) shells out
/// to ipconfig.exe, the standard tool for exactly this, scoped to one adapter's friendly name (the
/// same name NetworkInterface/ipconfig/netsh all agree on) except register-DNS, which ipconfig.exe
/// itself has no per-adapter switch for and always re-registers every adapter.
///
/// On-demand only (constructor call + explicit refresh, and the three action buttons) - a WMI sweep
/// and three shell-outs, none of them a trivial local counter read.
/// </summary>
public static class DhcpAddressingService
{
    // Same pseudo-adapter exclusion ReadAdapterDriverInfo already applies elsewhere in this file's
    // sibling NetworkDiagnosticsService - a DHCP lease on a loopback/kernel-debug adapter isn't
    // meaningful to someone troubleshooting their actual connection.
    private static readonly string[] ExcludedNameHints = { "Loopback", "Kernel Debug" };

    public static List<AdapterAddressInfo> ReadAll()
    {
        var results = new List<AdapterAddressInfo>();
        var baseline = DhcpServerBaselineService.Load();
        bool baselineTouched = false;

        try
        {
            var physicalByMac = new Dictionary<string, string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                string mac = NormalizeMac(ni.GetPhysicalAddress().ToString());
                if (mac.Length > 0) physicalByMac[mac] = ni.Name;
            }

            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, MACAddress, DHCPEnabled, DHCPServer, DHCPLeaseObtained, DHCPLeaseExpires, IPAddress, IPSubnet, DefaultIPGateway " +
                "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

            foreach (ManagementObject mo in searcher.Get())
            {
                string description = (mo["Description"] as string ?? string.Empty).Trim();
                if (ExcludedNameHints.Any(h => description.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

                string mac = (mo["MACAddress"] as string ?? string.Empty).Trim();
                string normalizedMac = NormalizeMac(mac);
                string adapterName = normalizedMac.Length > 0 && physicalByMac.TryGetValue(normalizedMac, out var name) ? name : description;

                var ipAddresses = (mo["IPAddress"] as string[]) ?? Array.Empty<string>();
                var subnetMasks = (mo["IPSubnet"] as string[]) ?? Array.Empty<string>();
                var gateways = (mo["DefaultIPGateway"] as string[]) ?? Array.Empty<string>();

                string ipv4 = ipAddresses.FirstOrDefault(ip => IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork) ?? string.Empty;
                if (ipv4.Length == 0) continue; // nothing to show for an adapter with no IPv4 address at all (IPv6-only tunnels, etc.)

                int ipv4Index = Array.IndexOf(ipAddresses, ipv4);
                string mask = ipv4Index >= 0 && ipv4Index < subnetMasks.Length ? subnetMasks[ipv4Index] : string.Empty;
                string? gateway = gateways.FirstOrDefault(g => IPAddress.TryParse(g, out var pg) && pg.AddressFamily == AddressFamily.InterNetwork);

                bool dhcpEnabled = mo["DHCPEnabled"] is bool de && de;
                string? dhcpServer = dhcpEnabled ? (mo["DHCPServer"] as string)?.Trim() : null;
                if (string.IsNullOrWhiteSpace(dhcpServer)) dhcpServer = null;

                var info = new AdapterAddressInfo
                {
                    AdapterName = adapterName,
                    Description = description,
                    MacAddress = mac,
                    DhcpEnabled = dhcpEnabled,
                    IpAddress = ipv4,
                    SubnetMask = mask,
                    DefaultGateway = gateway,
                    DhcpServer = dhcpServer,
                    LeaseObtained = ParseWmiDate(mo["DHCPLeaseObtained"] as string),
                    LeaseExpires = ParseWmiDate(mo["DHCPLeaseExpires"] as string),
                };

                ApplyLeaseStatus(info);
                info.IsApipa = ipv4.StartsWith("169.254.", StringComparison.Ordinal);

                if (dhcpEnabled && dhcpServer is not null && normalizedMac.Length > 0)
                {
                    info.RogueDhcpReason = DhcpServerBaselineService.CheckAndUpdate(baseline, normalizedMac, dhcpServer, ipv4, mask);
                    baselineTouched = true;
                }

                ApplySanityChecks(info, baseline, normalizedMac);

                results.Add(info);
            }
        }
        catch
        {
            // Best-effort - return whatever was gathered before the failure.
        }

        if (baselineTouched) DhcpServerBaselineService.Save(baseline);
        return results;
    }

    private static void ApplyLeaseStatus(AdapterAddressInfo info)
    {
        if (!info.DhcpEnabled)
        {
            info.LeaseStatusText = "Static configuration (DHCP disabled).";
            return;
        }
        if (info.LeaseObtained is null || info.LeaseExpires is null)
        {
            info.LeaseStatusText = "DHCP enabled - lease timing unavailable.";
            return;
        }

        var now = DateTime.Now;
        var age = now - info.LeaseObtained.Value;
        var remaining = info.LeaseExpires.Value - now;
        string ageText = FormatDuration(age.Duration());

        if (remaining <= TimeSpan.Zero)
        {
            info.LeaseExpiringSoon = true;
            info.LeaseStatusText = $"Lease expired {FormatDuration(remaining.Duration())} ago (obtained {ageText} ago) - Windows should be renewing automatically.";
        }
        else
        {
            info.LeaseExpiringSoon = remaining <= TimeSpan.FromMinutes(15);
            info.LeaseStatusText = info.LeaseExpiringSoon
                ? $"Lease expires in {FormatDuration(remaining)} (obtained {ageText} ago) - renewing soon."
                : $"Lease age {ageText}, expires in {FormatDuration(remaining)}.";
        }
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }

    /// <summary>#534: derived flags for an implausible mask, a missing default gateway, an IP
    /// outside the gateway's own subnet, and (for a currently-static adapter) a static address that
    /// doesn't match what DHCP last served on this same physical adapter.</summary>
    private static void ApplySanityChecks(AdapterAddressInfo info, DhcpServerBaselineFile baseline, string adapterMac)
    {
        var flags = new List<string>();

        if (info.SubnetMask == "255.255.255.255")
        {
            flags.Add("Subnet mask is 255.255.255.255 (/32) - this adapter can't reach any other host directly, not even its own gateway, without an explicit route for every destination.");
        }
        else if (info.SubnetMask.Length > 0 && !IsPlausibleIPv4Mask(info.SubnetMask))
        {
            flags.Add($"Subnet mask {info.SubnetMask} isn't a standard contiguous mask - worth double-checking this configuration.");
        }

        if (string.IsNullOrEmpty(info.DefaultGateway))
        {
            flags.Add("No default gateway configured - this adapter can reach only its own local subnet, nothing beyond it.");
        }
        else if (info.SubnetMask.Length > 0 && !NetworkContainsAddress(info.IpAddress, info.SubnetMask, info.DefaultGateway))
        {
            flags.Add($"Default gateway {info.DefaultGateway} is outside this adapter's own subnet ({info.IpAddress}/{info.SubnetMask}) - traffic to it may be silently dropped.");
        }

        if (!info.DhcpEnabled && adapterMac.Length > 0)
        {
            var lastKnown = DhcpServerBaselineService.GetLastKnownDhcpSubnet(baseline, adapterMac);
            if (lastKnown is { } known && known.SubnetMask.Length > 0 && !NetworkContainsAddress(known.LeasedIp, known.SubnetMask, info.IpAddress))
            {
                flags.Add($"This adapter is statically configured as {info.IpAddress}/{info.SubnetMask}, but DHCP last served it an address in a different range ({known.LeasedIp}/{known.SubnetMask}) - worth checking this static address still matches the network it's plugged into.");
            }
        }

        info.SanityFlags = flags;
    }

    /// <summary>A valid IPv4 mask, in binary, is a run of 1 bits followed by a run of 0 bits (any
    /// length, including all-1s or all-0s) - anything else (e.g. 255.0.255.0) is non-contiguous and
    /// not something any real network actually uses.</summary>
    private static bool IsPlausibleIPv4Mask(string mask)
    {
        if (!IPAddress.TryParse(mask, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        uint bits = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        bool seenZero = false;
        for (int i = 31; i >= 0; i--)
        {
            bool bitSet = ((bits >> i) & 1) == 1;
            if (!bitSet) seenZero = true;
            else if (seenZero) return false;
        }
        return true;
    }

    /// <summary>Same network-containment check RoutingTableService.NetworkContains (#513) already
    /// applies to on-link routes, duplicated here rather than shared since that method is private to
    /// a different class - degrades to "don't flag" on unparsable input, never fabricates a match.</summary>
    private static bool NetworkContainsAddress(string subnetSampleIp, string mask, string testIp)
    {
        if (!IPAddress.TryParse(subnetSampleIp, out var sampleIp) || !IPAddress.TryParse(mask, out var maskIp) || !IPAddress.TryParse(testIp, out var target))
            return true;

        var sampleBytes = sampleIp.GetAddressBytes();
        var maskBytes = maskIp.GetAddressBytes();
        var targetBytes = target.GetAddressBytes();
        if (sampleBytes.Length != maskBytes.Length || sampleBytes.Length != targetBytes.Length) return true;

        for (int i = 0; i < sampleBytes.Length; i++)
            if ((sampleBytes[i] & maskBytes[i]) != (targetBytes[i] & maskBytes[i])) return false;
        return true;
    }

    private static DateTime? ParseWmiDate(string? wmiDate)
    {
        if (string.IsNullOrWhiteSpace(wmiDate)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { return null; }
    }

    private static string NormalizeMac(string mac) => new(mac.Where(char.IsLetterOrDigit).ToArray().Select(char.ToUpperInvariant).ToArray());

    // ---- #529: release / renew / register-DNS, each a plain ipconfig.exe shell-out -----------

    public static Task<string> ReleaseAsync(string adapterName) => RunIpconfigAsync($"/release \"{adapterName}\"");

    public static Task<string> RenewAsync(string adapterName) => RunIpconfigAsync($"/renew \"{adapterName}\"");

    /// <summary>ipconfig.exe has no per-adapter switch for /registerdns - it always re-registers
    /// every adapter's records with its configured DNS server(s), so this deliberately takes no
    /// adapter parameter rather than pretending to scope something ipconfig itself can't scope.</summary>
    public static Task<string> RegisterDnsAsync() => RunIpconfigAsync("/registerdns");

    private static async Task<string> RunIpconfigAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start ipconfig.exe.";

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // release/renew can take a few seconds to negotiate a new lease
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "ipconfig.exe timed out.";
            }

            string output = (await outputTask) + (await errorTask);
            return string.IsNullOrWhiteSpace(output) ? "Done." : output.Trim();
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }
}
