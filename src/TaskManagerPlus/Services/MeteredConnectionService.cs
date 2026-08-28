using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One network profile's metered-connection flag (round 9, #52), extended by #595 with its
/// Category (Public/Private/Domain) read from the very same per-profile registry subkey - a second
/// value alongside the metered flag this class already opens each profile subkey to read, not a
/// second pass over the profile list. <see cref="LooksStuckPublic"/> is a "quick flag, not a
/// verdict" heuristic, not proof this specific network should be Private: this app has no reliable
/// way to tell "coffee-shop Wi-Fi, correctly Public" apart from "home network, stuck Public by
/// oversight" - the profile name is usually just the SSID/a generic label, not something this app
/// can classify - so every Public profile is flagged the same hedged way, leaving the actual
/// "should this be Private" judgment call to the user.</summary>
public sealed record MeteredAdapterInfo(string ProfileName, bool IsMetered, string CostText, string CategoryText, bool LooksStuckPublic);

/// <summary>
/// Metered-connection flag (#52) and network-category (#595) per network profile - Windows' own
/// Settings > Network > "Set as metered connection" toggle and "Network profile" (Public/Private)
/// picker, read from the registry rather than the WinRT NetworkInformation/ConnectionProfile surface
/// (see this class's original #52 remarks for why: a UWP-contracts package this classic-WPF-exe
/// project doesn't otherwise take, not worth it for two flags). Category lives at
/// <c>NetworkList\Profiles\{guid}\Category</c> - a plain DWORD (0 = Public, 1 = Private, 2 = Domain),
/// the same per-profile subkey ProfileName/cost are already read from, so reading it here is one
/// more GetValue call per already-open subkey, not a second registry sweep. A Public network
/// silently blocks network discovery, printer sharing and inbound SMB - the classic "my printer
/// isn't showing up" root cause nobody thinks to check here, per this item's own text. Wrapped to
/// degrade to an empty list (Network tab hides the whole card) on any failure, same as #52's
/// original read.
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

                    // #595: NLM_NETWORK_CATEGORY - 0 Public, 1 Private, 2 Domain (matches
                    // Settings' own "Public network"/"Private network" wording; Domain only ever
                    // appears on a domain-joined machine and isn't user-choosable).
                    int? category = profile?.GetValue("Category") is int c2 ? c2 : null;
                    string categoryText = category switch
                    {
                        0 => "Public",
                        1 => "Private",
                        2 => "Domain",
                        _ => "Unknown",
                    };

                    bool stuckPublic = category == 0;

                    result.Add(new MeteredAdapterInfo(name, metered, costText, categoryText, stuckPublic));
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
