using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #887: hosts file audit - total entry count, entries that look like they're blocking
/// Windows Update or a common AV vendor, entries pointing at a non-loopback address, an "unusually
/// large" file size flag, and a Mark of the Web check on the hosts file itself (reusing
/// ZoneIdentifierService from an earlier chunk). Read-only - "Open in Notepad" is the only action
/// this offers, per #887's own explicit "no direct in-app editing" text.
/// </summary>
public static class HostsFileAuditService
{
    public sealed record FlaggedEntry(string Hostname, string Address, string Reason);

    public sealed class HostsFileAuditInfo
    {
        public string HostsPath { get; init; } = string.Empty;
        public bool FileFound { get; init; }
        public int TotalEntries { get; init; }
        public List<FlaggedEntry> UpdateOrAvBlocks { get; init; } = new();
        public List<FlaggedEntry> NonLoopbackEntries { get; init; } = new();
        public bool IsLarge { get; init; }
        public ZoneIdentifierInfo Zone { get; init; } = ZoneIdentifierInfo.NotFound;
    }

    private const int LargeFileEntryThreshold = 500;

    private static readonly string[] UpdateBlockHints = { "update.microsoft", "windowsupdate", "download.windowsupdate" };
    private static readonly string[] AvVendorHints = { "avast", "norton", "mcafee", "kaspersky", "bitdefender", "eset", "malwarebytes", "windowsdefender", "microsoft.com" };
    private static readonly string[] LoopbackAddresses = { "127.0.0.1", "0.0.0.0", "::1" };

    public static HostsFileAuditInfo Scan()
    {
        string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        List<string> lines;
        try
        {
            if (!File.Exists(hostsPath)) return new HostsFileAuditInfo { HostsPath = hostsPath, FileFound = false };
            lines = File.ReadAllLines(hostsPath).ToList();
        }
        catch
        {
            return new HostsFileAuditInfo { HostsPath = hostsPath, FileFound = false };
        }

        int total = 0;
        var updateOrAv = new List<FlaggedEntry>();
        var nonLoopback = new List<FlaggedEntry>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // Strip an inline trailing comment, then split address + hostname(s) on whitespace.
            var hashIdx = line.IndexOf('#');
            if (hashIdx >= 0) line = line[..hashIdx].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string address = parts[0];
            foreach (var hostname in parts.Skip(1))
            {
                total++;

                if (UpdateBlockHints.Any(h => hostname.Contains(h, StringComparison.OrdinalIgnoreCase)) ||
                    AvVendorHints.Any(h => hostname.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    updateOrAv.Add(new FlaggedEntry(hostname, address, "May be blocking Windows Update or an antivirus vendor"));
                }

                if (!LoopbackAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    nonLoopback.Add(new FlaggedEntry(hostname, address, "Points at a non-loopback address - more unusual, worth a look"));
                }
            }
        }

        return new HostsFileAuditInfo
        {
            HostsPath = hostsPath,
            FileFound = true,
            TotalEntries = total,
            UpdateOrAvBlocks = updateOrAv,
            NonLoopbackEntries = nonLoopback,
            IsLarge = total > LargeFileEntryThreshold,
            Zone = ZoneIdentifierService.Read(hostsPath),
        };
    }
}
