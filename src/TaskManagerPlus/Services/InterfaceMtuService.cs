using System.Globalization;

namespace TaskManagerPlus.Services;

/// <summary>One adapter's configured MTU (#511), from `netsh interface ipv4/ipv6 show
/// subinterfaces`. <see cref="IsMismatched"/> is set by NetworkViewModel after a #510 path MTU
/// discovery completes - null/false until then, since there's nothing to compare against yet.</summary>
public sealed class InterfaceMtuInfo
{
    public string AddressFamily { get; init; } = string.Empty; // "IPv4" or "IPv6"
    public string InterfaceName { get; init; } = string.Empty;
    public int Mtu { get; init; }
    public bool IsMismatched { get; set; }
    public string? MismatchReason { get; set; }
}

/// <summary>
/// Item #511: per-adapter configured MTU inventory, parsed from `netsh interface ipv4 show
/// subinterfaces` / `netsh interface ipv6 show subinterfaces` - the same "known Windows tool over
/// raw interop" tradeoff every other netsh/sc/schtasks call in this app already takes, rather than
/// re-deriving MTU from IP_ADAPTER_ADDRESSES via P/Invoke. Table is shown under the Adapters card's
/// link-speed list; NetworkViewModel cross-references each row against the most recent #510
/// discovered path MTU to flag an interface configured larger than the path can actually carry.
/// On-demand (constructor call + alongside the #510 button), not on a timer - it shells out, which
/// this app's own convention gates behind an explicit trigger rather than a per-tick poll.
/// </summary>
public static class InterfaceMtuService
{
    public static async Task<List<InterfaceMtuInfo>> ReadAllAsync()
    {
        var results = new List<InterfaceMtuInfo>();
        results.AddRange(await ReadForFamilyAsync("ipv4"));
        results.AddRange(await ReadForFamilyAsync("ipv6"));
        return results;
    }

    private static async Task<List<InterfaceMtuInfo>> ReadForFamilyAsync(string family)
    {
        var results = new List<InterfaceMtuInfo>();
        try
        {
            // A timeout returns the sentinel (not partial output), which parses to no rows.
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh.exe", $"interface {family} show subinterfaces", 10000, includeStderr: false);

            string af = family == "ipv4" ? "IPv4" : "IPv6";
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                // Both families print the same table: "MTU  MediaSenseState  Bytes In  Bytes Out
                // Interface" - 4 numeric columns then the interface name (which may contain spaces).
                // The byte counters are lifetime totals that pass int.MaxValue on any busy adapter,
                // so they're parsed as long; an MTU too big for int is loopback's 4294967295
                // "no limit" value, which has nothing to compare against a path MTU anyway.
                if (tokens.Length < 5) continue;
                if (!long.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out long mtu)) continue; // header/separator row
                if (!long.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                if (!long.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                if (!long.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                if (mtu > int.MaxValue) continue;
                string name = string.Join(' ', tokens.Skip(4));
                results.Add(new InterfaceMtuInfo { AddressFamily = af, InterfaceName = name, Mtu = (int)mtu });
            }
        }
        catch
        {
            // Best-effort - return whatever was parsed before the failure.
        }
        return results;
    }
}
