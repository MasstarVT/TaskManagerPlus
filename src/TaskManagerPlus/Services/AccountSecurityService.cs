using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Xml.Linq;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #891-894: "Accounts and remote access" - local account audit, autologon credential
/// exposure, an RDP exposure card, and a sign-in/account-change timeline off the Security event
/// log. Grouped in one file/one Security tab section (like FirewallService bundles #881-883)
/// since all four are read together and shown together as "who can get onto this machine, and
/// how would you know."
///
/// "Prefer a known Windows tool over a WMI association join": local-group membership (891's
/// Administrators check, 893's "Remote Desktop Users" check) shells out to `net localgroup`
/// and parses its plain-text member list, rather than a Win32_Group/Win32_GroupUser
/// association query - the item's own text calls this the simpler, acceptable fallback when the
/// join "isn't too awkward" is itself awkward (it needs the exact machine-name-qualified
/// Win32_Group.Name path string), the same "known tool over raw plumbing" tradeoff this app
/// takes everywhere else (schtasks.exe, sc.exe, icacls.exe, netsh, ...).
/// </summary>
public static class AccountSecurityService
{
    // ==================================================================================
    // #891: local account audit.
    // ==================================================================================

    public static (List<LocalAccountInfo> Accounts, List<SecurityFinding> Findings) ReadLocalAccounts()
    {
        var accounts = new List<LocalAccountInfo>();
        var findings = new List<SecurityFinding>();

        var adminNames = ReadLocalGroupMembers("Administrators");
        var hiddenNames = ReadHiddenAccountNames();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, SID, Disabled, PasswordRequired, PasswordExpires FROM Win32_UserAccount WHERE LocalAccount=True");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    string name = mo["Name"] as string ?? string.Empty;
                    if (name.Length == 0) continue;

                    bool isAdmin = adminNames.Contains(name);
                    bool isHidden = hiddenNames.Contains(name);
                    bool enabled = !(mo["Disabled"] is bool disabled && disabled);

                    var account = new LocalAccountInfo
                    {
                        Name = name,
                        Sid = mo["SID"] as string ?? string.Empty,
                        Enabled = enabled,
                        IsAdministrator = isAdmin,
                        PasswordRequired = !(mo["PasswordRequired"] is bool pr) || pr,
                        PasswordExpires = !(mo["PasswordExpires"] is bool pe) || pe,
                        IsHiddenFromSignInScreen = isHidden,
                    };
                    accounts.Add(account);

                    if (account.IsHighValueCombination)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.High,
                            Title = $"Hidden administrator account: {name}",
                            Reason = $"\"{name}\" is enabled, a member of Administrators, AND hidden from the sign-in screen (via Winlogon\\SpecialAccounts\\UserList) - that specific combination is the classic shape of a backdoor account planted to survive a casual look at who can log on.",
                            Path = $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList\{name}",
                            WhatDisablingDoes = "This app makes no account changes - review the account (Computer Management > Local Users and Groups) and disable/remove group membership yourself if you don't recognize it.",
                        });
                    }
                }
                catch { /* one bad row shouldn't stop the rest */ }
            }
        }
        catch
        {
            // Win32_UserAccount unavailable/denied - "Unknown", not fabricated - empty list.
        }

        return (accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(), findings);
    }

    /// <summary>Shells `net localgroup "&lt;name&gt;"` and parses the member list between the
    /// dashed separator line and the trailing "The command completed successfully." line - the
    /// same plain-text shape for any local group, reused for both Administrators (#891) and
    /// "Remote Desktop Users" (#893).</summary>
    private static HashSet<string> ReadLocalGroupMembers(string groupName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string output = RunCapturedSync("net.exe", $"localgroup \"{groupName}\"", TimeSpan.FromSeconds(10));
            var lines = output.Replace("\r\n", "\n").Split('\n');
            bool inMembers = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("---", StringComparison.Ordinal)) { inMembers = true; continue; }
                if (!inMembers) continue;
                if (line.Length == 0) continue;
                if (line.StartsWith("The command completed", StringComparison.OrdinalIgnoreCase)) break;
                result.Add(line.Trim());
            }
        }
        catch
        {
            // net.exe unavailable/failed - "Unknown membership", an empty set (never fabricated).
        }
        return result;
    }

    private const string SpecialAccountsUserListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    private static HashSet<string> ReadHiddenAccountNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SpecialAccountsUserListPath);
            if (key is null) return result;
            foreach (var valueName in key.GetValueNames())
            {
                // Documented convention: DWORD 0 = hidden, 1 = explicitly shown (used to
                // force-show a built-in account that Windows would otherwise hide by default).
                if (key.GetValue(valueName) is int i && i == 0) result.Add(valueName);
            }
        }
        catch { /* key absent/denied - no hidden accounts reported, not fabricated */ }
        return result;
    }

    // ==================================================================================
    // #892: autologon credential exposure. HARD REQUIREMENT: this reads only WHETHER
    // DefaultPassword exists, never its value - see AutologonExposureInfo's remarks.
    // ==================================================================================

    private const string WinlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>#892: presence-only. DefaultPasswordPresent is a bool computed from
    /// GetValueNames().Contains("DefaultPassword") - the actual string value is never read,
    /// logged, or stored anywhere by this method or its caller.</summary>
    public sealed record AutologonExposureInfo(bool Enabled, string UserName, string DomainName, bool DefaultPasswordPresent);

    public static (AutologonExposureInfo Info, SecurityFinding? Finding) ReadAutologonExposure()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
            if (key is null) return (new AutologonExposureInfo(false, string.Empty, string.Empty, false), null);

            bool enabled = (key.GetValue("AutoAdminLogon") as string) == "1";
            string userName = key.GetValue("DefaultUserName") as string ?? string.Empty;
            string domainName = key.GetValue("DefaultDomainName") as string ?? string.Empty;
            // Presence check only - never GetValue("DefaultPassword").
            bool passwordPresent = key.GetValueNames().Contains("DefaultPassword", StringComparer.OrdinalIgnoreCase);

            var info = new AutologonExposureInfo(enabled, userName, domainName, passwordPresent);

            SecurityFinding? finding = null;
            if (enabled && passwordPresent)
            {
                finding = new SecurityFinding
                {
                    Severity = FindingSeverity.High,
                    Title = "Autologon password stored in plaintext",
                    Reason = $"AutoAdminLogon is on for \"{(domainName.Length > 0 ? domainName + "\\" : string.Empty)}{userName}\" and a DefaultPassword value exists under Winlogon - that value is plaintext-readable by any admin process or anyone with local admin/registry access, not just at boot. This app never reads the value itself, only that it's present.",
                    Path = $@"HKLM\{WinlogonPath}\DefaultPassword",
                    WhatDisablingDoes = "Safer alternative: use the LSA-secret method instead (Control Panel's \"control userpasswords2\" dialog, or Sysinternals Autologon.exe), which stores the credential as an LSA secret rather than a plaintext registry value.",
                };
            }
            return (info, finding);
        }
        catch
        {
            return (new AutologonExposureInfo(false, string.Empty, string.Empty, false), null);
        }
    }

    // ==================================================================================
    // #893: RDP exposure card.
    // ==================================================================================

    public sealed record RdpExposureInfo(
        bool? RdpEnabled,
        bool? NlaRequired,
        int Port,
        bool? RestrictedAdminDisabled,
        int? ShadowValue,
        string ShadowDescription,
        IReadOnlyList<string> RemoteDesktopUsersMembers);

    private const string TerminalServerPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpWinStationPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

    public static (RdpExposureInfo Info, SecurityFinding? Finding) ReadRdpExposure()
    {
        bool? rdpEnabled = null, nlaRequired = null, restrictedAdminDisabled = null;
        int port = 3389;
        int? shadowValue = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TerminalServerPath);
            if (key?.GetValue("fDenyTSConnections") is int deny) rdpEnabled = deny == 0;
        }
        catch { /* Unknown - not fabricated */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RdpTcpWinStationPath);
            if (key is not null)
            {
                if (key.GetValue("UserAuthentication") is int ua) nlaRequired = ua != 0;
                if (key.GetValue("PortNumber") is int p) port = p;
                if (key.GetValue("DisableRestrictedAdmin") is int dra) restrictedAdminDisabled = dra != 0;
                if (key.GetValue("Shadow") is int shadow) shadowValue = shadow;
            }
        }
        catch { /* Unknown - not fabricated */ }

        string shadowDescription = shadowValue switch
        {
            null => "Not configured",
            0 => "Disabled - no remote control/observation allowed",
            1 => "Full Control, with the user's permission",
            2 => "Full Control, without the user's permission",
            3 => "View only, with the user's permission",
            4 => "View only, without the user's permission",
            _ => $"Unrecognized value ({shadowValue})",
        };

        var members = ReadLocalGroupMembers("Remote Desktop Users").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var info = new RdpExposureInfo(rdpEnabled, nlaRequired, port, restrictedAdminDisabled, shadowValue, shadowDescription, members);

        SecurityFinding? finding = null;
        if (rdpEnabled == true && nlaRequired == false)
        {
            finding = new SecurityFinding
            {
                Severity = FindingSeverity.Medium,
                Title = "RDP is enabled without Network Level Authentication",
                Reason = $"Remote Desktop is enabled (port {port}) and Network Level Authentication is NOT required - RDP sessions can reach the full logon screen before any credential is verified, a larger attack surface than NLA-protected RDP.",
                Path = $@"HKLM\{RdpTcpWinStationPath}\UserAuthentication",
                WhatDisablingDoes = "Turn on \"Require Network Level Authentication\" under System Properties > Remote, or set this registry value to 1 - existing Remote Desktop clients that don't support NLA will need to be updated.",
            };
        }
        return (info, finding);
    }

    // ==================================================================================
    // #894: sign-in and account-change timeline - Security event log, on-demand only (gated
    // behind its own explicit button, same "expensive, so make it explicit" discipline as
    // StabilityViewModel's own event-log scan and DefenderService's Operational-log reads).
    // ==================================================================================

    private const string SecurityLog = "Security";
    private const int LookbackDays = 30;
    private const int MaxSignInEvents = 200;

    public sealed record SignInTimelineEvent(DateTime Time, int EventId, string EventType, string TargetUser, string LogonType, string SourceIp, string Summary);

    private static readonly Dictionary<int, string> SignInEventLabels = new()
    {
        [4624] = "Logon success",
        [4625] = "Logon failure",
        [4672] = "Admin logon (special privileges)",
        [4720] = "Account created",
        [4726] = "Account deleted",
        [4732] = "Added to a local group",
    };

    private static readonly Dictionary<string, string> LogonTypeLabels = new()
    {
        ["2"] = "Interactive",
        ["3"] = "Network",
        ["4"] = "Batch",
        ["5"] = "Service",
        ["7"] = "Unlock",
        ["8"] = "NetworkCleartext",
        ["9"] = "NewCredentials",
        ["10"] = "RemoteInteractive (RDP)",
        ["11"] = "CachedInteractive",
    };

    /// <summary>#894: the whole point of this being a separate on-demand method (never auto-run)
    /// is that Security-log queries are the most expensive/most likely to be access-denied of
    /// anything on this tab. Degrades to "no events found (or auditing may not be enabled)" -
    /// exactly the wording the item's own text asks for - rather than surfacing a bare
    /// UnauthorizedAccessException.</summary>
    public static (List<SignInTimelineEvent> Events, bool LogAvailable, List<SecurityFinding> Findings) ReadSignInTimeline()
    {
        var events = new List<SignInTimelineEvent>();
        bool logAvailable = true;
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", SignInEventLabels.Keys.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(SecurityLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxSignInEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    events.Add(BuildTimelineEvent(record));
                }
            }
        }
        catch
        {
            // Access denied (even elevated, if local audit policy doesn't log these event types
            // at all) or the log is otherwise unavailable - degrade gracefully, never throw.
            logAvailable = false;
        }

        var findings = logAvailable ? BuildBurstFindings(events) : new List<SecurityFinding>();
        return (events, logAvailable, findings);
    }

    private static SignInTimelineEvent BuildTimelineEvent(EventRecord record)
    {
        int id = record.Id;
        string label = SignInEventLabels.TryGetValue(id, out var l) ? l : $"Event {id}";
        DateTime time = record.TimeCreated ?? DateTime.MinValue;

        string targetUser = ExtractDataValue(record, "TargetUserName") ?? ExtractDataValue(record, "MemberName") ?? string.Empty;
        string logonTypeRaw = ExtractDataValue(record, "LogonType") ?? string.Empty;
        string logonType = logonTypeRaw.Length > 0 && LogonTypeLabels.TryGetValue(logonTypeRaw, out var lt) ? lt : logonTypeRaw;
        string ip = ExtractDataValue(record, "IpAddress") ?? string.Empty;
        if (ip is "-" or "::1" or "127.0.0.1") ip = ip == "-" ? string.Empty : ip; // "-" is Windows' own "no network address" placeholder

        string summary = id switch
        {
            4624 or 4625 => $"{(targetUser.Length > 0 ? targetUser : "(unknown user)")}" + (ip.Length > 0 ? $" from {ip}" : string.Empty) + (logonType.Length > 0 ? $" - {logonType}" : string.Empty),
            4672 => $"{ExtractDataValue(record, "SubjectUserName") ?? targetUser} - admin-level logon",
            4720 => $"Account created: {targetUser}",
            4726 => $"Account deleted: {targetUser}",
            4732 => $"{targetUser} added to group \"{ExtractDataValue(record, "TargetUserName") ?? "?"}\" by {ExtractDataValue(record, "SubjectUserName") ?? "?"}",
            _ => label,
        };

        return new SignInTimelineEvent(time, id, label, targetUser, logonType, ip, summary);
    }

    /// <summary>Same adaptive "scan the event's rendered XML for a &lt;Data Name=...&gt; field"
    /// approach as BootPerformanceService.ExtractBootTimeFields, generalized to look up one named
    /// field instead of pattern-matching the name.</summary>
    private static string? ExtractDataValue(EventRecord record, string dataName)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { return null; }

        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var data = doc.Descendants(ns + "Data")
                .FirstOrDefault(d => string.Equals(d.Attribute("Name")?.Value, dataName, StringComparison.OrdinalIgnoreCase));
            return data?.Value;
        }
        catch { return null; }
    }

    /// <summary>"Burst of failed network logons" - any 5-minute window containing 5+ 4625 events
    /// (regardless of whether the source IP is the same or varies, per the item's own text)
    /// raises one summarizing finding rather than one finding per overlapping window.</summary>
    private static List<SecurityFinding> BuildBurstFindings(List<SignInTimelineEvent> events)
    {
        var failures = events.Where(e => e.EventId == 4625).OrderBy(e => e.Time).ToList();
        if (failures.Count < 5) return new List<SecurityFinding>();

        int bestCount = 0;
        DateTime bestStart = default, bestEnd = default;
        int windowStart = 0;
        for (int i = 0; i < failures.Count; i++)
        {
            while (failures[i].Time - failures[windowStart].Time > TimeSpan.FromMinutes(5)) windowStart++;
            int countInWindow = i - windowStart + 1;
            if (countInWindow > bestCount)
            {
                bestCount = countInWindow;
                bestStart = failures[windowStart].Time;
                bestEnd = failures[i].Time;
            }
        }

        if (bestCount < 5) return new List<SecurityFinding>();

        return new List<SecurityFinding>
        {
            new SecurityFinding
            {
                Severity = bestCount >= 10 ? FindingSeverity.High : FindingSeverity.Medium,
                Title = $"Burst of {bestCount} failed logons within 5 minutes",
                Reason = $"{bestCount} failed logon events (4625) occurred between {bestStart:g} and {bestEnd:g} - a tight burst like this is a common shape for a password-guessing attempt (local or over RDP/network), though a mistyped-password loop from a saved-credential app is also a common innocent cause.",
                Path = "Security event log",
                WhatDisablingDoes = "Review the source IP addresses/accounts involved below - if this wasn't you, consider whether RDP/network exposure needs tightening (see the RDP exposure card and Network exposure section above).",
            },
        };
    }

    private static string RunCapturedSync(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return string.Empty;
        }

        return outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
    }
}
