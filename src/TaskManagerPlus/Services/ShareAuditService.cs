using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #885: share audit - enumerates SMB shares via Win32_Share (structured, preferred over
/// parsing `net share`), separates administrative ($-suffixed / special-bit) shares from
/// user-created ones, and for user-created shares resolves both the SHARE-level permissions
/// (Get-SmbShareAccess, parsed CSV - much simpler and more reliable than the
/// Win32_LogicalShareSecuritySetting security-descriptor route, per the item's own preference) and
/// the underlying NTFS permissions on the share's target path (icacls, mirroring
/// AutorunsService.CheckWritePermissions's own weak-ACL parsing so this doesn't reinvent that
/// logic). Flags Everyone/Authenticated Users WITH write access at either level.
/// </summary>
public static class ShareAuditService
{
    public sealed record ShareAccessEntry(string AccountName, string AccessControlType, string AccessRight);

    public sealed class ShareInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string TypeText { get; init; } = string.Empty;
        public bool IsAdministrative { get; init; }
        public List<ShareAccessEntry> ShareAccess { get; init; } = new();
        public List<string> NtfsWeakGrants { get; init; } = new();
        public bool ShareAccessCouldBeRead { get; init; }
        public bool NtfsCouldBeRead { get; init; } = true;
        public bool HasWeakShareAccess => ShareAccess.Any(a => IsWeakPrincipal(a.AccountName) && IsAllowWriteRight(a));
        public bool HasWeakNtfs => NtfsWeakGrants.Count > 0;

        /// <summary>A user-created share whose share-level or NTFS permissions couldn't be read -
        /// shown as unchecked, never as "no weak permissions". Administrative shares aren't read.</summary>
        public bool PermissionsUnreadable => !IsAdministrative && Path.Length > 0 && (!ShareAccessCouldBeRead || !NtfsCouldBeRead);
    }

    // Everyone, Authenticated Users. Get-SmbShareAccess reports AccountName in the OS language
    // ("Jeder", "NT-AUTORITÄT\Authentifizierte Benutzer"), so the name is resolved to its SID;
    // the English names are only the fallback when that lookup fails.
    private static readonly string[] WeakShareSids = { "S-1-1-0", "S-1-5-11" };
    private static readonly string[] WeakSharePrincipals = { "Everyone", "Authenticated Users" };

    // HasWeakShareAccess is re-read by the findings pass and the UI; a domain name lookup is a
    // round trip to the DC, so each name is resolved once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> WeakPrincipalCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsWeakPrincipal(string account) => WeakPrincipalCache.GetOrAdd(account, static name =>
    {
        try
        {
            string sid = new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value;
            return WeakShareSids.Contains(sid, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return WeakSharePrincipals.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
    });

    private static bool IsAllowWriteRight(ShareAccessEntry a) =>
        a.AccessControlType.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
        (a.AccessRight.Contains("Change", StringComparison.OrdinalIgnoreCase) ||
         a.AccessRight.Contains("Full", StringComparison.OrdinalIgnoreCase));

    public static (List<ShareInfo> Shares, List<SecurityFinding> Findings, string? Error) Scan()
    {
        var shares = new List<ShareInfo>();
        string? error = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Path, Type FROM Win32_Share");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? string.Empty;
                if (name.Length == 0) continue;

                string path = mo["Path"] as string ?? string.Empty;
                uint typeRaw = mo["Type"] is uint t ? t : 0;
                uint lowByte = typeRaw & 0x000000FF;
                bool specialBit = (typeRaw & 0x80000000) != 0;
                bool isAdmin = name.EndsWith('$') || specialBit;

                string typeText = lowByte switch
                {
                    0 => "Disk", 1 => "Print Queue", 2 => "Device", 3 => "IPC", _ => $"Unknown ({lowByte})",
                };

                var share = new ShareInfo { Name = name, Path = path, TypeText = typeText, IsAdministrative = isAdmin };

                if (!isAdmin && path.Length > 0)
                {
                    var (access, couldRead) = ReadShareAccess(name);
                    // Only a disk share has an NTFS target to check (a print share's Path is a
                    // printer name).
                    var (grants, ntfsRead) = lowByte == 0 ? ReadNtfsWeakGrants(path) : (new List<string>(), true);
                    share = new ShareInfo
                    {
                        Name = name, Path = path, TypeText = typeText, IsAdministrative = false,
                        ShareAccess = access, ShareAccessCouldBeRead = couldRead,
                        NtfsWeakGrants = grants, NtfsCouldBeRead = ntfsRead,
                    };
                }

                shares.Add(share);
            }
        }
        catch (Exception ex)
        {
            // WMI unavailable/denied - whatever was gathered before the failure is returned, and
            // the caller says the list may be incomplete.
            error = ex.Message;
        }

        var findings = new List<SecurityFinding>();
        foreach (var s in shares.Where(s => !s.IsAdministrative))
        {
            if (!s.HasWeakShareAccess && !s.HasWeakNtfs) continue;

            var reasons = new List<string>();
            if (s.HasWeakShareAccess)
                reasons.Add($"share-level permissions grant Change/Full to {string.Join(", ", s.ShareAccess.Where(a => IsWeakPrincipal(a.AccountName) && IsAllowWriteRight(a)).Select(a => $"{a.AccountName} ({a.AccessRight})"))}");
            if (s.HasWeakNtfs)
                reasons.Add($"NTFS permissions on \"{s.Path}\" grant write to a broad principal ({string.Join("; ", s.NtfsWeakGrants)})");

            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = $"Share \"{s.Name}\" is writable by Everyone/Authenticated Users",
                Reason = $"{string.Join(", and ", reasons)}. Anyone who can reach this share over the network can write to it - a common ransomware/lateral-movement target.",
                Path = $@"\\{Environment.MachineName}\{s.Name} ({s.Path})",
                WhatDisablingDoes = "Narrow the share/NTFS permissions to specific users or groups that actually need write access, via the Sharing tab's Advanced Sharing > Permissions and the Security tab's Edit, respectively.",
            });
        }

        return (shares, findings, error);
    }

    /// <summary>Share-level permissions via Get-SmbShareAccess - PREFERRED over the
    /// Win32_LogicalShareSecuritySetting security-descriptor route per #885's own guidance (much
    /// simpler, directly gives AccountName+AccessControlType+AccessRight per share).</summary>
    private static (List<ShareAccessEntry> Entries, bool CouldRead) ReadShareAccess(string shareName)
    {
        var entries = new List<ShareAccessEntry>();
        try
        {
            string escaped = shareName.Replace("'", "''");
            var (output, exitCode) = RunCapturedSync("powershell.exe",
                $"-NoProfile -Command \"Get-SmbShareAccess -Name '{escaped}' | Select-Object AccountName,AccessControlType,AccessRight | ConvertTo-Csv -NoTypeInformation\"",
                TimeSpan.FromSeconds(15));
            // Timed out (null) or the cmdlet failed: partial or error output isn't a permission list.
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return (entries, false);

            var rows = SimpleCsv.ParseRows(output);
            if (rows.Count < 2) return (entries, true); // header only - no access entries, but the call itself worked

            var header = rows[0];
            int accountIdx = header.FindIndex(h => h.Equals("AccountName", StringComparison.OrdinalIgnoreCase));
            int typeIdx = header.FindIndex(h => h.Equals("AccessControlType", StringComparison.OrdinalIgnoreCase));
            int rightIdx = header.FindIndex(h => h.Equals("AccessRight", StringComparison.OrdinalIgnoreCase));
            if (accountIdx < 0 || typeIdx < 0 || rightIdx < 0) return (entries, false);

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count <= Math.Max(accountIdx, Math.Max(typeIdx, rightIdx))) continue;
                entries.Add(new ShareAccessEntry(row[accountIdx], row[typeIdx], row[rightIdx]));
            }
            return (entries, true);
        }
        catch
        {
            return (entries, false);
        }
    }

    /// <summary>NTFS weak-grant check on the share's target path - reuses
    /// AutorunsService.CheckWritePermissions's icacls quoting and ACE parsing (same principal list,
    /// same loose (W)/(F)/(M) match) rather than reinventing that logic. CouldRead is false when
    /// the target is missing or icacls failed/timed out - its error text has no grant lines, which
    /// must not read as "no weak grants".</summary>
    private static (List<string> Grants, bool CouldRead) ReadNtfsWeakGrants(string path)
    {
        var grants = new List<string>();
        if (!Directory.Exists(path) && !File.Exists(path)) return (grants, false);

        string output;
        int? exitCode;
        try { (output, exitCode) = RunCapturedSync("icacls.exe", AutorunsService.IcaclsPathArgument(path), TimeSpan.FromSeconds(10)); }
        catch { return (grants, false); }
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return (grants, false);

        return (AutorunsService.FindWeakWriteGrantLines(output, path), true);
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/> (run tool, capture
    /// stdout+stderr, kill the whole process tree on timeout; a timeout's exit code is null).</summary>
    private static (string Output, int? ExitCode) RunCapturedSync(string exe, string args, TimeSpan timeout)
        => ToolRunner.RunCaptured(exe, args, timeout);
}

/// <summary>Minimal RFC 4180-ish CSV line parser (quoted fields, "" escaping) - shared by every
/// service in this chunk that parses `ConvertTo-Csv -NoTypeInformation` output (PowerShell's own
/// CSV writer, so this only needs to handle its specific quoting behavior, not the full CSV spec).</summary>
internal static class SimpleCsv
{
    public static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("#TYPE", StringComparison.OrdinalIgnoreCase)) continue; // ConvertTo-Csv's type-name comment line
            rows.Add(ParseLine(line));
        }
        return rows;
    }

    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
