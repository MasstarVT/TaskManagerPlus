using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>#545's read result. <see cref="Label"/>/<see cref="IsAggressive"/> use a commonly-seen
/// (Intel/Realtek-style) 0-3 numbering for the standardized `*PowerSavingMode` NDIS keyword, but
/// the exact scale is vendor-defined - <see cref="RawValue"/> is always shown alongside the label
/// so a reading that doesn't match the common convention is still visible as-is rather than hidden
/// behind a possibly-wrong label.</summary>
public sealed record WifiPowerSavingInfo(string RawValue, string Label, bool IsAggressive, string Source);

/// <summary>
/// Item #545: the Wi-Fi radio's power-save setting, read from the adapter's own advanced-property
/// registry keyword - `netsh wlan show interfaces` doesn't surface this on stock Windows (its
/// output is the connection-state fields WifiDiagnosticsService already reads: SSID/signal/channel/
/// radio type, not power management), so the actual source here is the Device Manager "Advanced"
/// tab property Windows stores per-adapter under the network class registry key, keyed by the
/// standardized NDIS keyword `*PowerSavingMode` (the same keyword drivers register for the
/// Advanced-tab dropdown many Wi-Fi adapters expose).
///
/// Quick flag, not a verdict (CLAUDE.md): the 0-3 severity numbering below is a common convention,
/// not a guaranteed one - some drivers use their own scale, or expose no keyword at all, in which
/// case this returns null and the card shows nothing rather than a guessed reading.
/// </summary>
public static class WifiPowerSavingService
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private static readonly string[] CandidateValueNames = { "*PowerSavingMode", "PowerSavingMode" };

    public static WifiPowerSavingInfo? Read(string? connectedInterfaceGuid)
    {
        if (string.IsNullOrEmpty(connectedInterfaceGuid)) return null;
        string wantedGuid = connectedInterfaceGuid.Trim('{', '}');

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
            if (classKey is null) return null;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Adapter instance subkeys are always 4-digit numeric ("0000", "0001", ...) -
                // the class key also has non-numeric siblings ("Properties", etc.) to skip.
                if (subKeyName.Length == 0 || !subKeyName.All(char.IsDigit)) continue;

                using var subKey = classKey.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                if (subKey.GetValue("NetCfgInstanceId") is not string instanceId) continue;
                if (!string.Equals(instanceId.Trim('{', '}'), wantedGuid, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var valueName in CandidateValueNames)
                {
                    if (subKey.GetValue(valueName) is string raw && !string.IsNullOrWhiteSpace(raw))
                        return new WifiPowerSavingInfo(raw, LabelFor(raw), IsAggressive(raw), $"Adapter advanced property ({valueName})");
                }
                return null; // found the adapter, but it doesn't expose this keyword - not every driver does
            }
        }
        catch
        {
            // Access denied, or the network class key layout differs from expected - degrade to
            // "not available", never guess.
        }
        return null;
    }

    private static string LabelFor(string raw) => raw switch
    {
        "0" => "Off / Maximum performance",
        "1" => "Low power saving",
        "2" => "Medium power saving",
        "3" => "Maximum power saving",
        _ => $"Vendor-specific value ({raw})",
    };

    private static bool IsAggressive(string raw) => raw is "2" or "3";
}
