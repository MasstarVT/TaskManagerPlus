using System.Diagnostics;

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
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh.exe", $"interface {family} show subinterfaces", 10_000,
                timeoutOutput: string.Empty, includeStderr: false);

            string af = family == "ipv4" ? "IPv4" : "IPv6";
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (family == "ipv4")
                {
                    // "    MTU  MediaSenseState   Bytes In  Bytes Out  Interface" - 4 numeric
                    // columns then the interface name (which itself may contain spaces).
                    if (tokens.Length < 5) continue;
                    if (!int.TryParse(tokens[0], out int mtu)) continue; // header/separator row
                    if (!int.TryParse(tokens[1], out _)) continue;
                    if (!int.TryParse(tokens[2], out _)) continue;
                    if (!int.TryParse(tokens[3], out _)) continue;
                    string name = string.Join(' ', tokens.Skip(4));
                    results.Add(new InterfaceMtuInfo { AddressFamily = af, InterfaceName = name, Mtu = mtu });
                }
                else
                {
                    // "Idx     Met         MTU          State                Name" - 3 numeric
                    // columns, a state word, then the interface name.
                    if (tokens.Length < 5) continue;
                    if (!int.TryParse(tokens[0], out _)) continue; // header/separator row
                    if (!int.TryParse(tokens[1], out _)) continue;
                    if (!int.TryParse(tokens[2], out int mtu)) continue;
                    string name = string.Join(' ', tokens.Skip(4));
                    results.Add(new InterfaceMtuInfo { AddressFamily = af, InterfaceName = name, Mtu = mtu });
                }
            }
        }
        catch
        {
            // Best-effort - return whatever was parsed before the failure.
        }
        return results;
    }
}
