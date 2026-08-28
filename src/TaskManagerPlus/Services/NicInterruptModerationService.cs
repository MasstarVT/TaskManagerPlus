using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One physical NIC's interrupt-moderation/RSS registry settings (#221) - read-only, this
/// never writes to the registry. A null field means the adapter's driver doesn't expose that
/// standard OID as a registry value (common on some drivers/virtual adapters), not that the feature
/// is off - see NicInterruptModerationService's remarks.</summary>
public sealed record NicInterruptModerationInfo(
    string AdapterName,
    bool? InterruptModerationEnabled,
    bool? RssEnabled,
    int? RssQueueCount,
    bool? FlowControlEnabled,
    bool FlagInterruptModerationOff,
    bool FlagRssOffOnMultiCore);

/// <summary>
/// #221: reads each network adapter's driver-specific registry parameters under the network device
/// class key (`HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\
/// &lt;nnnn&gt;`) for the standard NDIS "keyword" values `*InterruptModeration`, `*RSS`,
/// `*NumRssQueues`, `*FlowControl` - the same per-adapter numbered-subkey convention every NIC
/// vendor's INF installs its advanced properties into (Device Manager's own "Advanced" tab reads
/// from exactly these values). Not every driver publishes every keyword, or expresses "off/on" the
/// same way (some use 0/1, some use "0"/"1" strings, some use vendor-specific enabled/disabled
/// strings) - a missing/unrecognized value degrades to null ("Unknown"), never a guessed on/off
/// state, per CLAUDE.md's "degrade to Unknown, never fabricate" rule.
/// </summary>
public static class NicInterruptModerationService
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static Task<List<NicInterruptModerationInfo>> LoadAsync() => Task.Run(Load);

    private static List<NicInterruptModerationInfo> Load()
    {
        var results = new List<NicInterruptModerationInfo>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
            if (classKey is null) return results;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Subkeys are 4-digit instance numbers ("0000", "0001", ...); the class key itself
                // also carries some non-numbered informational subkeys ("Properties", etc.) that
                // aren't adapter instances - skip anything that isn't the expected numeric shape.
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit)) continue;

                try
                {
                    using var adapterKey = classKey.OpenSubKey(subKeyName);
                    if (adapterKey is null) continue;

                    string driverDesc = adapterKey.GetValue("DriverDesc") as string ?? string.Empty;
                    // A physical adapter has a NetCfgInstanceId (bound into the TCP/IP stack);
                    // subkeys without one are usually a bus/filter driver's own settings block, not
                    // something worth an audit row for.
                    if (string.IsNullOrEmpty(driverDesc) || adapterKey.GetValue("NetCfgInstanceId") is null) continue;

                    bool? interruptModeration = ReadTriState(adapterKey, "*InterruptModeration");
                    bool? rss = ReadTriState(adapterKey, "*RSS");
                    int? rssQueues = ReadInt(adapterKey, "*NumRssQueues");
                    bool? flowControl = ReadTriState(adapterKey, "*FlowControl", trueValues: new[] { "3", "1", "2" }); // 3=Tx&Rx,1=Tx,2=Rx,0=Disabled

                    bool multiCore = Environment.ProcessorCount > 1;
                    results.Add(new NicInterruptModerationInfo(
                        driverDesc,
                        interruptModeration,
                        rss,
                        rssQueues,
                        flowControl,
                        FlagInterruptModerationOff: interruptModeration == false,
                        FlagRssOffOnMultiCore: multiCore && rss == false));
                }
                catch
                {
                    // One malformed/denied adapter subkey shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Class key unavailable/access denied - empty list, card shows the "no data" message.
        }
        return results.OrderBy(r => r.AdapterName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool? ReadTriState(RegistryKey key, string valueName, string[]? trueValues = null)
    {
        object? raw = key.GetValue(valueName);
        if (raw is null) return null;
        string s = raw.ToString() ?? string.Empty;
        if (trueValues is not null) return trueValues.Contains(s, StringComparer.OrdinalIgnoreCase);
        return s switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };
    }

    private static int? ReadInt(RegistryKey key, string valueName)
    {
        object? raw = key.GetValue(valueName);
        if (raw is null) return null;
        return int.TryParse(raw.ToString(), out int v) ? v : null;
    }
}
