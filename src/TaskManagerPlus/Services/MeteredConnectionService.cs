using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One network profile's metered-connection flag (round 9, #52).</summary>
public sealed record MeteredAdapterInfo(string ProfileName, bool IsMetered, string CostText);

/// <summary>
/// Metered-connection flag per network profile (#52) - Windows' own Settings > Network > "Set as
/// metered connection" toggle, relevant context for this app's own logging/remote-monitoring
/// bandwidth use. There's no simple public .NET API for this short of the WinRT
/// Windows.Networking.Connectivity.NetworkInformation/ConnectionCost surface, which would need a
/// UWP-contracts package reference this classic-WPF-exe project doesn't otherwise take (a real
/// target-framework-shaped tradeoff, not worth it for one flag) - instead this reads the same
/// per-profile DefaultMediaCost registry values DUSM (the Data Usage Subscription Manager service
/// behind that Settings toggle) itself writes. This location isn't a documented, versioned public
/// API either, so it's the same "best-effort, not a verified fact" tier as the SecurityCenter2 AV
/// productState bitmask read elsewhere in this app - wrapped to degrade to an empty list (Network
/// tab hides the whole card) on any failure.
/// </summary>
public static class MeteredConnectionService
{
    private const string ProfilesKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\NetworkList\Profiles";
    private const string CostKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\NetworkList\DefaultMediaCost";

    public static List<MeteredAdapterInfo> ReadMeteredStatus()
    {
        var result = new List<MeteredAdapterInfo>();
        try
        {
            using var profilesKey = Registry.LocalMachine.OpenSubKey(ProfilesKeyPath);
            using var costKey = Registry.LocalMachine.OpenSubKey(CostKeyPath);
            if (profilesKey is null) return result;

            foreach (var guid in profilesKey.GetSubKeyNames())
            {
                try
                {
                    using var profile = profilesKey.OpenSubKey(guid);
                    string name = (profile?.GetValue("ProfileName") as string ?? "Network").Trim();

                    int cost = 0;
                    using (var costSub = costKey?.OpenSubKey(guid))
                    {
                        if (costSub?.GetValue("Cost") is int c) cost = c;
                    }

                    // Low bit values match the documented NLM_CONNECTION_COST flags: 0/1 =
                    // unrestricted (not metered), 2 = fixed, 4 = variable - fixed/variable both
                    // mean "treat this as metered" for logging/bandwidth-use purposes.
                    bool metered = cost is 2 or 4;
                    string costText = cost switch
                    {
                        0 or 1 => "Unrestricted",
                        2 => "Fixed",
                        4 => "Variable",
                        _ => $"Cost code {cost}",
                    };

                    result.Add(new MeteredAdapterInfo(name, metered, costText));
                }
                catch
                {
                    // One malformed profile subkey shouldn't stop the rest of the enumeration.
                }
            }
        }
        catch
        {
            // Registry path unavailable - empty list, card hidden by the view.
        }
        return result;
    }
}
