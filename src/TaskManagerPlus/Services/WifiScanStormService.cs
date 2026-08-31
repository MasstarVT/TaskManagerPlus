using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #222: correlates periodic DPC/interrupt spikes with wireless background scanning - the common
/// case of a Wi-Fi adapter that's still associated (and therefore still periodically re-scanning to
/// maintain/roam its connection, plus passive channel scans while idle) even though the machine is
/// actually getting its traffic over a wired Ethernet connection. Two independent signals, both
/// best-effort:
///   1. `netsh wlan show interfaces` (same tool/parsing tradeoff WifiDiagnosticsService already
///      takes) - is a Wi-Fi adapter currently associated at all.
///   2. A live Ethernet adapter (System.Net.NetworkInformation, no shell-out needed) - is the
///      machine actually getting its traffic another way.
/// A supporting (not required) third signal - recent event volume on the
/// Microsoft-Windows-WLAN-AutoConfig/Operational log - is read the same way EventLogService reads
/// other operational logs. There is no single documented "this was a background scan" event ID
/// contract to key off (the same "no stable field-name contract" situation DpcLatencyService's ETW
/// parsing documents for its own provider), so this counts *any* recent activity on that log as
/// supporting evidence of an active radio, not a scan-specific count - a provider/log that can't be
/// read just means this count stays 0 and the result falls back to the netsh+Ethernet signal alone.
/// On-demand only (an event-log scan is not a per-tick-timer cost).
/// </summary>
public static class WifiScanStormService
{
    private const string OperationalLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    private static readonly TimeSpan ScanWindow = TimeSpan.FromMinutes(10);
    private static readonly Regex LabelLineRegex = new(@"^\s*([^:]+?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static async Task<WifiScanStormResult> CheckAsync()
    {
        var (adapterName, ssid, state) = await ReadWlanInterfaceAsync();
        bool wifiConnected = state?.Contains("connected", StringComparison.OrdinalIgnoreCase) == true &&
                              !state.Contains("disconnected", StringComparison.OrdinalIgnoreCase);

        if (adapterName is null)
        {
            return new WifiScanStormResult
            {
                Detected = false,
                IsOnEthernet = false,
                StatusText = "No Wi-Fi adapter found (or `netsh wlan show interfaces` isn't available on this system).",
            };
        }

        bool isOnEthernet = HasActiveEthernet();
        int recentEvents = ReadRecentOperationalEventCount();

        bool detected = wifiConnected && isOnEthernet;
        string status = !wifiConnected
            ? $"Wi-Fi adapter \"{adapterName}\" is not currently connected - no scan-storm suspicion."
            : !isOnEthernet
                ? $"Wi-Fi adapter \"{adapterName}\" is connected (SSID: {ssid ?? "unknown"}), but no active Ethernet link was found, so this isn't the \"scanning while wired\" case."
                : $"Wi-Fi adapter \"{adapterName}\" is still connected to \"{ssid ?? "an access point"}\" while an Ethernet adapter is also active - the radio is likely still periodically scanning/roaming in the background even though it isn't needed for traffic. Try disabling the Wi-Fi adapter to see if the stutter goes away.";

        return new WifiScanStormResult
        {
            Detected = detected,
            AdapterName = adapterName,
            RecentScanEventCount = recentEvents,
            IsOnEthernet = isOnEthernet,
            StatusText = status,
        };
    }

    private static bool HasActiveEthernet()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                ni.OperationalStatus == OperationalStatus.Up &&
                !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static int ReadRecentOperationalEventCount()
    {
        try
        {
            long maxAgeMs = (long)ScanWindow.TotalMilliseconds;
            var query = new EventLogQuery(OperationalLog, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record) count++;
            }
            return count;
        }
        catch
        {
            // Log/provider unavailable (or access denied) - 0 just means this supporting signal
            // couldn't be read, not that nothing happened.
            return 0;
        }
    }

    private static async Task<(string? Adapter, string? Ssid, string? State)> ReadWlanInterfaceAsync()
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("netsh", "wlan show interfaces", 4000);
            if (exitCode is null) return (null, null, null);
            string? name = ExtractField(output, "Name");
            string? ssid = ExtractField(output, "SSID");
            string? state = ExtractField(output, "State");
            return (name, ssid, state);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? ExtractField(string output, string label)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = LabelLineRegex.Match(line.TrimEnd('\r'));
            if (match.Success && match.Groups[1].Value.Equals(label, StringComparison.OrdinalIgnoreCase))
                return match.Groups[2].Value.Trim();
        }
        return null;
    }
}
