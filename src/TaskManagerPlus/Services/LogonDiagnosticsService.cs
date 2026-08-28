using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Logon-to-desktop breakdown (#715), Group Policy processing time + slowest CSE (#716/#717),
/// synchronous-foreground-policy audit (#718), and logon/startup script inventory (#719) - the
/// "Sign-in" section of the Startup tab. Same adaptive event-field-reading style as
/// BootPerformanceService (field names aren't a documented, versioned schema, so every read
/// searches by field name rather than assuming a fixed index/layout) and the same
/// degrade-to-empty-list-never-throw pattern every other event-log read in this app already uses.
/// </summary>
public static class LogonDiagnosticsService
{
    private static readonly XNamespace EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const int LookbackDays = 30;

    #region #715 - Winlogon notification-subscriber timing

    private const string WinlogonLogName = "Microsoft-Windows-Winlogon/Operational";
    private const int SubscriberStartEventId = 811;
    private const int SubscriberStopEventId = 812;
    private const int MaxWinlogonEvents = 2000;

    /// <summary>#715: reads every 811 (subscriber notification starting)/812 (subscriber
    /// notification finished) pair still retained in the Winlogon operational log and returns the
    /// per-subscriber elapsed time for each pairing found - GPClient/Profiles/TermSrv/Sens are the
    /// common subscribers, but this doesn't hardcode that list since it's not a documented,
    /// versioned contract. Pairing is done per (SubscriberName, SessionId) in chronological order
    /// (nearest following 812 after each 811) so overlapping subscribers on the same session don't
    /// get cross-matched.</summary>
    public static List<LogonSubscriberTiming> ReadSubscriberTimings()
    {
        var results = new List<LogonSubscriberTiming>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(WinlogonLogName, PathType.LogName,
                $"*[System[(EventID={SubscriberStartEventId} or EventID={SubscriberStopEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);

            var starts = new List<(string Name, string? Session, DateTime Time)>();
            var stops = new List<(string Name, string? Session, DateTime Time)>();

            int count = 0;
            while (count < MaxWinlogonEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } time) continue;
                    var (name, session) = ExtractSubscriberFields(record);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (record.Id == SubscriberStartEventId) starts.Add((name, session, time));
                    else stops.Add((name, session, time));
                }
            }

            // Pair each start with the nearest later stop sharing the same subscriber+session.
            var usedStops = new HashSet<int>();
            foreach (var start in starts.OrderBy(s => s.Time))
            {
                int matchIdx = -1;
                DateTime bestTime = DateTime.MaxValue;
                for (int i = 0; i < stops.Count; i++)
                {
                    if (usedStops.Contains(i)) continue;
                    var stop = stops[i];
                    if (!string.Equals(stop.Name, start.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (stop.Session != start.Session) continue;
                    if (stop.Time < start.Time) continue;
                    if (stop.Time < bestTime) { bestTime = stop.Time; matchIdx = i; }
                }
                if (matchIdx < 0) continue;
                usedStops.Add(matchIdx);

                results.Add(new LogonSubscriberTiming
                {
                    SubscriberName = start.Name,
                    SessionId = start.Session,
                    StartTime = start.Time,
                    StopTime = stops[matchIdx].Time,
                });
            }
        }
        catch
        {
            // Channel unavailable/access denied - no subscriber breakdown, same degrade-to-nothing
            // pattern every other event-log read in this app uses.
        }
        return results.OrderByDescending(r => r.StartTime).ToList();
    }

    private static (string? Name, string? Session) ExtractSubscriberFields(EventRecord record)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { return (null, null); }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return (null, null); }

        string? name = null, session = null;
        foreach (var data in doc.Descendants(EventNs + "Data"))
        {
            var attrName = data.Attribute("Name")?.Value ?? string.Empty;
            if (name is null && attrName.Contains("Subscriber", StringComparison.OrdinalIgnoreCase))
                name = data.Value;
            else if (session is null && attrName.Contains("Session", StringComparison.OrdinalIgnoreCase))
                session = data.Value;
        }
        return (name, session);
    }

    #endregion

    #region #716/#717 - Group Policy processing time and per-extension breakdown

    private const string GroupPolicyLogName = "Microsoft-Windows-GroupPolicy/Operational";
    private const int ComputerPolicyEventId = 8000;
    private const int UserPolicyEventId = 8001;
    private static readonly int[] CseEventIds = { 5016, 6336, 7016 };
    private const int MaxGpEvents = 2000;

    /// <summary>#716: reads every 8000 (computer boot policy total elapsed time)/8001 (user logon
    /// policy total elapsed time) event still retained in the GroupPolicy operational log - the
    /// elapsed-time field is read adaptively (any Data field whose name mentions "Time" and whose
    /// value parses as a plausible millisecond duration), the same tradeoff
    /// BootPerformanceService.ExtractBootTimeFields already documents.</summary>
    public static List<GroupPolicyProcessingEntry> ReadProcessingTimes()
    {
        var results = new List<GroupPolicyProcessingEntry>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(GroupPolicyLogName, PathType.LogName,
                $"*[System[(EventID={ComputerPolicyEventId} or EventID={UserPolicyEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxGpEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } time) continue;
                    var ms = ExtractElapsedMs(record);
                    if (ms is null) continue;

                    results.Add(new GroupPolicyProcessingEntry
                    {
                        TimeCreated = time,
                        IsUserPolicy = record.Id == UserPolicyEventId,
                        ElapsedMs = ms.Value,
                    });
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - no GP processing-time chart data.
        }
        return results.OrderBy(r => r.TimeCreated).ToList();
    }

    private static int? ExtractElapsedMs(EventRecord record)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { return null; }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }

        foreach (var data in doc.Descendants(EventNs + "Data"))
        {
            var attrName = data.Attribute("Name")?.Value ?? string.Empty;
            if (!attrName.Contains("Time", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(data.Value, out int value)) continue;
            if (value < 0 || value > 30 * 60 * 1000) continue; // plausible processing duration only
            return value;
        }
        return null;
    }

    /// <summary>#717: ranked list of client-side-extension completion events (5016 informational,
    /// 6336/7016 for a slow/warned extension) so "Drive Maps: 41,800 ms" is visible under the bare
    /// total from ReadProcessingTimes above. ExtensionName/ElapsedMs are read the same adaptive
    /// way - by field name, not fixed index.</summary>
    public static List<GroupPolicyCseEntry> ReadSlowestExtensions()
    {
        var results = new List<GroupPolicyCseEntry>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", CseEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(GroupPolicyLogName, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxGpEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var entry = ParseCseEvent(record);
                    if (entry is not null) results.Add(entry);
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - no ranked CSE list.
        }
        return results.OrderByDescending(e => e.ElapsedMs).Take(20).ToList();
    }

    private static GroupPolicyCseEntry? ParseCseEvent(EventRecord record)
    {
        if (record.TimeCreated is not { } time) return null;

        string xml;
        try { xml = record.ToXml(); }
        catch { return null; }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }

        string? extension = null;
        int? elapsedMs = null;
        foreach (var data in doc.Descendants(EventNs + "Data"))
        {
            var attrName = data.Attribute("Name")?.Value ?? string.Empty;
            if (extension is null && (attrName.Contains("Extension", StringComparison.OrdinalIgnoreCase) || attrName.Contains("CSE", StringComparison.OrdinalIgnoreCase)))
                extension = data.Value;
            else if (elapsedMs is null && attrName.Contains("Time", StringComparison.OrdinalIgnoreCase) && int.TryParse(data.Value, out var t) && t >= 0 && t <= 30 * 60 * 1000)
                elapsedMs = t;
        }
        if (string.IsNullOrWhiteSpace(extension) || elapsedMs is null) return null;

        return new GroupPolicyCseEntry
        {
            TimeCreated = time,
            EventId = record.Id,
            ExtensionName = extension,
            ElapsedMs = elapsedMs.Value,
        };
    }

    #endregion

    #region #718 - synchronous foreground policy audit

    private const string SyncPolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows\System";

    /// <summary>#718: reads the handful of documented policy values under
    /// HKLM\SOFTWARE\Policies\Microsoft\Windows\System that force the desktop to wait for policy/
    /// scripts - a read-only audit card, no attempt to change these (they're domain-administered
    /// Group Policy values; flipping them locally would just be overwritten on the next policy
    /// refresh). Missing key/values degrade to null (shown as "Not configured") rather than a
    /// guessed default.</summary>
    public static SyncForegroundPolicyAudit ReadSyncForegroundPolicyAudit()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SyncPolicyKeyPath);
            return new SyncForegroundPolicyAudit
            {
                SyncForegroundPolicy = key?.GetValue("SyncForegroundPolicy") as int?,
                GpNetworkStartTimeoutPolicyValue = key?.GetValue("GpNetworkStartTimeoutPolicyValue") as int?,
                RunLogonScriptSync = key?.GetValue("RunLogonScriptSync") as int?,
                DelayedDesktopSwitchTimeout = key?.GetValue("DelayedDesktopSwitchTimeout") as int?,
            };
        }
        catch
        {
            return new SyncForegroundPolicyAudit();
        }
    }

    #endregion

    #region #719 - logon/startup script inventory

    /// <summary>#719: enumerates local Group Policy startup/shutdown/logon/logoff scripts
    /// (%windir%\System32\GroupPolicy\{Machine,User}\Scripts\{Startup,Logon,Logoff,Shutdown},
    /// including the classic/PowerShell scripts.ini/psscripts.ini manifests alongside each
    /// folder) plus the legacy per-user HKCU\Environment\UserInitMprLogonScript value. Every
    /// script's existence and last-modified time are read straight off the file system - an
    /// unresolvable path (configured but missing) is included and flagged, not silently dropped,
    /// since a missing script is itself a common cause of a logon hang/delay.</summary>
    public static List<LogonScriptInfo> ReadLogonScripts()
    {
        var results = new List<LogonScriptInfo>();
        try
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string gpRoot = Path.Combine(windir, "System32", "GroupPolicy");

            AddScriptFolder(results, Path.Combine(gpRoot, "Machine", "Scripts", "Startup"), "Machine Startup");
            AddScriptFolder(results, Path.Combine(gpRoot, "Machine", "Scripts", "Shutdown"), "Machine Shutdown");
            AddScriptFolder(results, Path.Combine(gpRoot, "User", "Scripts", "Logon"), "User Logon");
            AddScriptFolder(results, Path.Combine(gpRoot, "User", "Scripts", "Logoff"), "User Logoff");

            AddManifestFile(results, Path.Combine(gpRoot, "Machine", "Scripts", "scripts.ini"), "Machine scripts.ini manifest");
            AddManifestFile(results, Path.Combine(gpRoot, "Machine", "Scripts", "psscripts.ini"), "Machine psscripts.ini manifest");
            AddManifestFile(results, Path.Combine(gpRoot, "User", "Scripts", "scripts.ini"), "User scripts.ini manifest");
            AddManifestFile(results, Path.Combine(gpRoot, "User", "Scripts", "psscripts.ini"), "User psscripts.ini manifest");
        }
        catch
        {
            // GroupPolicy scripts tree unavailable/access denied - fall through to the legacy
            // logon-script value below, which is read independently.
        }

        try
        {
            using var env = Registry.CurrentUser.OpenSubKey("Environment");
            var legacy = env?.GetValue("UserInitMprLogonScript") as string;
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                bool exists = FileExistsSafe(legacy);
                results.Add(new LogonScriptInfo
                {
                    Category = "Legacy logon script",
                    Path = legacy,
                    Exists = exists,
                    LastModifiedUtc = exists ? SafeLastWriteUtc(legacy) : null,
                });
            }
        }
        catch
        {
            // Access denied/unavailable - no legacy logon script entry.
        }

        return results;
    }

    private static void AddScriptFolder(List<LogonScriptInfo> into, string folder, string category)
    {
        if (!Directory.Exists(folder)) return;
        string[] files;
        try { files = Directory.GetFiles(folder); }
        catch { return; }

        foreach (var file in files)
        {
            // scripts.ini/psscripts.ini are handled separately below (as manifests, not scripts).
            var name = Path.GetFileName(file);
            if (name.Equals("scripts.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("psscripts.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            into.Add(new LogonScriptInfo
            {
                Category = category,
                Path = file,
                Exists = true,
                LastModifiedUtc = SafeLastWriteUtc(file),
            });
        }
    }

    private static void AddManifestFile(List<LogonScriptInfo> into, string path, string category)
    {
        if (!FileExistsSafe(path)) return;
        into.Add(new LogonScriptInfo
        {
            Category = category,
            Path = path,
            Exists = true,
            LastModifiedUtc = SafeLastWriteUtc(path),
        });
    }

    private static bool FileExistsSafe(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static DateTime? SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    #endregion
}
