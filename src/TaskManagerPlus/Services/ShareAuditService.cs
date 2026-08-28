using System.Diagnostics;
using System.IO;
using System.Management;
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
        public bool HasWeakShareAccess => ShareAccess.Any(a => IsWeakPrincipal(a.AccountName) && IsAllowWriteRight(a));
        public bool HasWeakNtfs => NtfsWeakGrants.Count > 0;
    }

    private static readonly string[] WeakSharePrincipals = { "Everyone", "Authenticated Users" };
    private static readonly string[] WeakAclPrincipals = { "Users", "Authenticated Users", "Everyone", @"BUILTIN\Users" };

    private static bool IsWeakPrincipal(string account) => WeakSharePrincipals.Any(p => account.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowWriteRight(ShareAccessEntry a) =>
        a.AccessControlType.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
        (a.AccessRight.Contains("Change", StringComparison.OrdinalIgnoreCase) ||
         a.AccessRight.Contains("Full", StringComparison.OrdinalIgnoreCase));

    public static (List<ShareInfo> Shares, List<SecurityFinding> Findings) Scan()
    {
        var shares = new List<ShareInfo>();
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
                    share = new ShareInfo
                    {
                        Name = name, Path = path, TypeText = typeText, IsAdministrative = false,
                        ShareAccess = access, ShareAccessCouldBeRead = couldRead,
                        NtfsWeakGrants = ReadNtfsWeakGrants(path),
                    };
                }

                shares.Add(share);
            }
        }
        catch
        {
            // WMI unavailable/denied - whatever was gathered before the failure is returned as-is.
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

        return (shares, findings);
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
            string output = RunCapturedSync("powershell.exe",
                $"-NoProfile -Command \"Get-SmbShareAccess -Name '{escaped}' | Select-Object AccountName,AccessControlType,AccessRight | ConvertTo-Csv -NoTypeInformation\"",
                TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(output)) return (entries, false);

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

    /// <summary>NTFS weak-grant check on the share's target path - mirrors
    /// AutorunsService.CheckWritePermissions's icacls parsing (same principal list, same loose
    /// (W)/(F)/(M) substring match) rather than reinventing that logic.</summary>
    private static List<string> ReadNtfsWeakGrants(string path)
    {
        var grants = new List<string>();
        if (!Directory.Exists(path) && !File.Exists(path)) return grants;

        string output;
        try { output = RunCapturedSync("icacls.exe", $"\"{path}\"", TimeSpan.FromSeconds(10)); }
        catch { return grants; }
        if (string.IsNullOrWhiteSpace(output)) return grants;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            bool mentionsWeakPrincipal = WeakAclPrincipals.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!mentionsWeakPrincipal) continue;

            bool hasWriteGrant = line.Contains("(W)", StringComparison.OrdinalIgnoreCase)
                || line.Contains("(F)", StringComparison.OrdinalIgnoreCase)
                || line.Contains("(M)", StringComparison.OrdinalIgnoreCase);
            if (hasWriteGrant) grants.Add(line);
        }
        return grants;
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
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return string.Empty;
        }

        return outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
    }
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
