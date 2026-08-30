using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One decoded bit of the <c>DisabledComponents</c> value (#591) - a known, documented
/// (if informally so - Microsoft KB929852) flag rather than a guessed meaning.</summary>
public sealed record Ipv6DisabledComponentFlag(int Bit, string Description);

/// <summary>#591's full DisabledComponents read. <see cref="RawValue"/> is null when the value
/// isn't set at all (Windows' own default - IPv6 fully enabled), matching CLAUDE.md's
/// "degrade to Unknown/0, never fabricate" rule. <see cref="SetFlags"/> only ever contains bits this
/// app actually recognizes; any other bits present in the raw value are surfaced separately via
/// <see cref="UnrecognizedBitsHex"/> rather than silently dropped or guessed at.</summary>
public sealed record Ipv6DisabledComponentsInfo(
    int? RawValue, List<Ipv6DisabledComponentFlag> SetFlags, string? UnrecognizedBitsHex, string SummaryText);

/// <summary>One row of <c>netsh int ipv6 show prefixpolicies</c> (#591) - the RFC 6724 source/
/// destination address-selection table. A lower Precedence number wins; this app doesn't reorder
/// it, only displays it.</summary>
public sealed record Ipv6PrefixPolicyEntry(int Precedence, int Label, string Prefix);

/// <summary>One transition-tunnel adapter's state (#594) - Teredo, 6to4 or ISATAP.
/// <see cref="Present"/> is false when the adapter/service doesn't exist on this machine at all
/// (most machines with 6to4/ISATAP unused never create the pseudo-interface); <see cref="LooksActive"/>
/// is true only when the state text reads as something other than off/disconnected/dormant.</summary>
public sealed record TunnelAdapterState(string Kind, string StateText, bool Present, bool LooksActive);

/// <summary>#594's combined tunnel read. <see cref="AllDormant"/> gates the whole card's visibility
/// per this item's own text - "hidden when all tunnels are dormant".</summary>
public sealed record TransitionTunnelInfo(List<TunnelAdapterState> Tunnels, bool AllDormant);

/// <summary>
/// Items #591/#594 (suggestions.md "IPv6, network location awareness and connectivity semantics"):
/// decodes the <c>DisabledComponents</c> registry override, reads the RFC 6724 prefix-policy table,
/// and reports Teredo/6to4/ISATAP transition-tunnel state - three ways a "partially-disabled or
/// reprioritised IPv6 stack" can otherwise only be diagnosed by manually running three separate
/// netsh commands and knowing what a bitmask like 0x20 means.
///
/// <see cref="DisabledComponentsKeyPath"/> is the real key <c>Tcpip6\Parameters</c> under
/// <c>HKLM\SYSTEM\CurrentControlSet\Services</c> - the actual location Windows itself reads this
/// override from (also documented in KB929852/KB3010600). Every netsh call below follows
/// TcpGlobalSettingsService's own shape (ProcessStartInfo + a 10s cancellation + best-effort parse),
/// same "known tool over raw interop" convention as that class documents - there is no WMI class or
/// documented registry location for the prefix-policy table or live tunnel adapter state.
/// </summary>
public static class Ipv6DiagnosticsService
{
    private const string DisabledComponentsKeyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";

    // Microsoft KB929852's documented bit meanings - the ones this app actually recognizes.
    // Deliberately NOT an exhaustive/self-claimed-authoritative table: an unrecognized bit is
    // reported as raw hex (UnrecognizedBitsHex) rather than guessed at, same tier of honesty
    // AdapterPowerManagementService's own PnPCapabilities 0x18 read already takes.
    private static readonly Ipv6DisabledComponentFlag[] KnownFlags =
    {
        new(0x01, "Tunnel interfaces (6to4, ISATAP, Teredo) disabled"),
        new(0x02, "6to4 specifically disabled"),
        new(0x04, "ISATAP specifically disabled"),
        new(0x08, "Teredo specifically disabled"),
        new(0x10, "IPv6 disabled on LAN/Wi-Fi/PPP interfaces (native IPv6 off)"),
        new(0x20, "IPv4 preferred over IPv6 (prefix policy table reprioritised)"),
    };

    public static Ipv6DisabledComponentsInfo ReadDisabledComponents()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DisabledComponentsKeyPath);
            object? raw = key?.GetValue("DisabledComponents");
            if (raw is null)
                return new Ipv6DisabledComponentsInfo(null, new List<Ipv6DisabledComponentFlag>(), null,
                    "Not set - IPv6 fully enabled (Windows' own default) on every interface type.");

            int value = System.Convert.ToInt32(raw);
            if (value == 0)
                return new Ipv6DisabledComponentsInfo(0, new List<Ipv6DisabledComponentFlag>(), null,
                    "0x0 - IPv6 fully enabled on every interface type.");

            var set = KnownFlags.Where(f => (value & f.Bit) == f.Bit).ToList();
            int recognizedMask = set.Aggregate(0, (acc, f) => acc | f.Bit);
            int leftover = value & ~recognizedMask;
            string? unrecognizedHex = leftover != 0 ? $"0x{leftover:X}" : null;

            string summary = value == 0xFF
                ? "0xFF - IPv6 fully disabled on all interfaces (the commonly-used \"disable IPv6\" value)."
                : set.Count == 0
                    ? $"0x{value:X} - a value this app doesn't recognize any documented bits in. Worth a manual check."
                    : $"0x{value:X} - {set.Count} known flag(s) set.";

            return new Ipv6DisabledComponentsInfo(value, set, unrecognizedHex, summary);
        }
        catch
        {
            return new Ipv6DisabledComponentsInfo(null, new List<Ipv6DisabledComponentFlag>(), null,
                "Couldn't read the registry value - access denied or the key doesn't exist.");
        }
    }

    /// <summary>#591: <c>netsh int ipv6 show prefixpolicies</c> - the RFC 6724 source/destination
    /// address-selection table. A ::ffff:0:0/96 (IPv4-mapped) row with a higher Precedence than the
    /// native ::/0 row is the live, on-the-wire effect of DisabledComponents' 0x20 "prefer IPv4"
    /// bit - shown here, not asserted, so the two reads corroborate each other rather than one
    /// re-deriving the other.</summary>
    public static async Task<List<Ipv6PrefixPolicyEntry>> ReadPrefixPoliciesAsync()
    {
        string output = await RunNetshAsync("interface ipv6 show prefixpolicies");
        var entries = new List<Ipv6PrefixPolicyEntry>();
        foreach (Match m in PrefixPolicyRowRegex.Matches(output))
        {
            if (!int.TryParse(m.Groups[1].Value, out int precedence)) continue;
            if (!int.TryParse(m.Groups[2].Value, out int label)) continue;
            entries.Add(new Ipv6PrefixPolicyEntry(precedence, label, m.Groups[3].Value.Trim()));
        }
        return entries;
    }

    private static readonly Regex PrefixPolicyRowRegex = new(
        @"^\s*(\d+)\s+(\d+)\s+([0-9A-Fa-f:\.\/]+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>#594: Teredo state from <c>netsh int teredo show state</c>, plus 6to4/ISATAP state
    /// mined from the pseudo-interface rows of <c>netsh int ipv6 show interfaces</c> - neither 6to4
    /// nor ISATAP has its own netsh "show state" subcommand the way Teredo does, so their state is
    /// read the same way an admin would eyeball it: by name, from the interface list.</summary>
    public static async Task<TransitionTunnelInfo> ReadTunnelStateAsync()
    {
        var teredoTask = RunNetshAsync("interface teredo show state");
        var interfacesTask = RunNetshAsync("interface ipv6 show interfaces");
        await Task.WhenAll(teredoTask, interfacesTask);

        string teredoState = ExtractField(teredoTask.Result, "State") ?? ExtractField(teredoTask.Result, "Type") ?? "Unknown";
        var teredo = new TunnelAdapterState("Teredo", teredoState, !string.IsNullOrEmpty(teredoTask.Result), LooksActiveState(teredoState));

        var interfaceRows = ParseInterfaceRows(interfacesTask.Result);
        var sixToFourRow = interfaceRows.FirstOrDefault(r => r.Name.Contains("6to4", StringComparison.OrdinalIgnoreCase));
        var isatapRow = interfaceRows.FirstOrDefault(r => r.Name.Contains("isatap", StringComparison.OrdinalIgnoreCase));

        var sixToFour = new TunnelAdapterState("6to4", sixToFourRow.Name is null ? "Not present" : sixToFourRow.State,
            sixToFourRow.Name is not null, sixToFourRow.Name is not null && LooksActiveState(sixToFourRow.State));
        var isatap = new TunnelAdapterState("ISATAP", isatapRow.Name is null ? "Not present" : isatapRow.State,
            isatapRow.Name is not null, isatapRow.Name is not null && LooksActiveState(isatapRow.State));

        var tunnels = new List<TunnelAdapterState> { teredo, sixToFour, isatap };
        bool allDormant = tunnels.All(t => !t.LooksActive);
        return new TransitionTunnelInfo(tunnels, allDormant);
    }

    private static bool LooksActiveState(string stateText) =>
        !string.IsNullOrWhiteSpace(stateText) &&
        stateText is not ("Unknown" or "Not present") &&
        !stateText.Contains("offline", StringComparison.OrdinalIgnoreCase) &&
        !stateText.Contains("disconnected", StringComparison.OrdinalIgnoreCase) &&
        !stateText.Contains("dormant", StringComparison.OrdinalIgnoreCase) &&
        !stateText.Contains("disabled", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex InterfaceRowRegex = new(
        @"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<(string State, string Name)> ParseInterfaceRows(string output)
    {
        var rows = new List<(string, string)>();
        foreach (Match m in InterfaceRowRegex.Matches(output))
            rows.Add((m.Groups[4].Value.Trim(), m.Groups[5].Value.Trim()));
        return rows;
    }

    private static async Task<string> RunNetshAsync(string arguments)
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await proc.WaitForExitAsync(cts.Token); }
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

    private static string? ExtractField(string output, string label)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            if (!line[..idx].Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[(idx + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}
