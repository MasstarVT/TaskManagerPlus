using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One saved Wi-Fi profile's audit result (#544).</summary>
public sealed record WifiProfileAudit(
    string Name, string? InterfaceName, string ConnectionMode, bool IsHiddenSsid,
    string Authentication, string Cipher, bool IsWeakSecurity, bool AutoConnectsToOpenNetwork);

/// <summary>
/// Items #543/#544: two independent uses of netsh's own profile/report tooling.
///
/// #543 shells `netsh wlan show wlanreport` (Windows' own built-in wireless diagnostics report
/// generator - the best offline Wi-Fi history Windows offers, zero parsing needed) and opens the
/// resulting HTML file in the default browser.
///
/// #544 parses `netsh wlan show profiles` for the saved-network list, then `netsh wlan show
/// profile name="X"` (deliberately never with `key=clear` - this app has no use for the stored
/// password and avoids handling it at all, the same "don't touch credentials unnecessarily" instinct
/// this app already applies elsewhere) per profile, flagging hidden-SSID profiles, WEP/TKIP profiles,
/// and profiles set to auto-connect to an open (unsecured) network - the classic "joins any evil-twin
/// AP with a familiar name" footgun. Read-only except for an explicit per-profile delete, itself
/// behind a confirmation (see NetworkViewModel's DeleteWifiProfile, matching the ReleaseSelectedAdapter/
/// ProcessesViewModel.EndSelected confirm pattern).
/// </summary>
public static class WifiProfileService
{
    private const int TimeoutMs = 5000;

    public static async Task<List<WifiProfileAudit>> ListProfilesAsync()
    {
        string listOutput = await RunNetshAsync("wlan show profiles");
        string? interfaceName = ExtractInterfaceName(listOutput);
        var names = ExtractProfileNames(listOutput);

        var results = new List<WifiProfileAudit>();
        foreach (var name in names)
        {
            string detailArgs = interfaceName is null
                ? $"wlan show profile name=\"{EscapeArg(name)}\""
                : $"wlan show profile name=\"{EscapeArg(name)}\" interface=\"{EscapeArg(interfaceName)}\"";
            string detail = await RunNetshAsync(detailArgs);
            results.Add(ParseProfileDetail(name, interfaceName, detail));
        }
        return results;
    }

    public static async Task<string> DeleteProfileAsync(string profileName, string? interfaceName)
    {
        string args = interfaceName is null
            ? $"wlan delete profile name=\"{EscapeArg(profileName)}\""
            : $"wlan delete profile name=\"{EscapeArg(profileName)}\" interface=\"{EscapeArg(interfaceName)}\"";
        string output = await RunNetshAsync(args);
        return output.Trim();
    }

    /// <summary>#543: runs Windows' own wireless-report generator, then opens the fixed, documented
    /// output path in the default browser. Surfaces the path itself if the browser launch fails
    /// (no default handler for .html, Smart App Control blocking a shell launch, etc.) rather than
    /// swallowing the failure.</summary>
    public static async Task<(bool Success, string Message, string? ReportPath)> RunWlanReportAsync()
    {
        string output = await RunNetshAsync("wlan show wlanreport", timeoutMs: 15000);
        string reportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "WlanReport", "wlan-report-latest.html");

        if (!File.Exists(reportPath))
            return (false, $"netsh didn't produce a report at the expected path.\n{output.Trim()}", null);

        try
        {
            Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true });
            return (true, "Report generated and opened in your default browser.", reportPath);
        }
        catch (Exception ex)
        {
            return (true, $"Report generated at: {reportPath}\n(couldn't open it automatically - {ex.Message})", reportPath);
        }
    }

    // ---- netsh output parsing ----------------------------------------------------------------

    private static readonly Regex InterfaceHeaderRegex = new(@"^Profile(?:s)? on interface (.+):$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ProfileNameRegex = new(@"^\s*(?:All User Profile|Group Policy Profile|Per user Profile)\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static string? ExtractInterfaceName(string output)
    {
        var m = InterfaceHeaderRegex.Match(output);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static List<string> ExtractProfileNames(string output)
    {
        var names = new List<string>();
        foreach (Match m in ProfileNameRegex.Matches(output))
        {
            string name = m.Groups[1].Value.Trim();
            if (name.Length > 0 && name != "<none>") names.Add(name);
        }
        return names;
    }

    private static WifiProfileAudit ParseProfileDetail(string name, string? interfaceName, string detail)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in detail.Split('\n'))
        {
            string trimmed = rawLine.TrimEnd('\r').Trim();
            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;
            string label = trimmed[..colonIdx].Trim();
            string value = trimmed[(colonIdx + 1)..].Trim();
            if (label.Length == 0 || value.Length == 0) continue;
            fields[label] = value; // later occurrences (the duplicated "Security settings" block) simply overwrite with the same value
        }

        fields.TryGetValue("Connection mode", out var connectionMode);
        fields.TryGetValue("Network broadcast", out var networkBroadcast);
        fields.TryGetValue("Authentication", out var authentication);
        fields.TryGetValue("Cipher", out var cipher);

        connectionMode ??= "Unknown";
        authentication ??= "Unknown";
        cipher ??= "Unknown";

        bool isHidden = networkBroadcast is not null && networkBroadcast.Contains("not broadcasting", StringComparison.OrdinalIgnoreCase);
        bool isWeak = cipher.Contains("WEP", StringComparison.OrdinalIgnoreCase) || cipher.Contains("TKIP", StringComparison.OrdinalIgnoreCase)
            || authentication.Contains("WEP", StringComparison.OrdinalIgnoreCase);
        bool autoConnect = connectionMode.Contains("automatically", StringComparison.OrdinalIgnoreCase);
        // "Open" authentication with no cipher is a true unsecured network - distinct from WEP,
        // which also uses "Open" 802.11 authentication but is already caught by isWeak above.
        bool isOpenNetwork = authentication.Equals("Open", StringComparison.OrdinalIgnoreCase)
            && (cipher.Equals("None", StringComparison.OrdinalIgnoreCase) || cipher.Length == 0);

        return new WifiProfileAudit(name, interfaceName, connectionMode, isHidden, authentication, cipher, isWeak, autoConnect && isOpenNetwork);
    }

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"");

    /// <summary>Thin adapter over the shared ToolRunner (#1084) - empty string on timeout or
    /// launch failure, this file's historical degrade-to-nothing shape.</summary>
    private static async Task<string> RunNetshAsync(string arguments, int timeoutMs = TimeoutMs)
    {
        try
        {
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh", arguments, timeoutMs, timeoutOutput: string.Empty);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
