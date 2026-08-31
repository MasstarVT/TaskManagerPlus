using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One resolver cache entry (#518), as parsed from one `ipconfig /displaydns` record
/// block.</summary>
public sealed record DnsCacheEntry(string RecordName, string RecordType, int Ttl, string Data);

/// <summary>
/// Item #518: DNS resolver cache viewer with flush. Shells out to `ipconfig /displaydns` and
/// `ipconfig /flushdns` - the same known-tool tradeoff every other diagnostic in this app takes,
/// and the only supported way to read/clear the resolver cache short of the undocumented DNS
/// Client WMI provider. Parses the label/value block format ipconfig prints per record (one block
/// per cached name, separated by a "----" rule and/or a blank line) rather than returning the raw
/// text, so the grid can be searched/sorted - but degrades to an empty list (not a fabricated row)
/// on any parse surprise, per this app's own convention.
/// </summary>
public static class DnsCacheService
{
    // Matches ipconfig's "Label . . . . . : value" line shape - the same padded-dots label format
    // WifiDiagnosticsService's netsh parse and MtrService-adjacent netsh table reads already deal
    // with elsewhere in this app, just with literal ". . ." filler instead of a plain colon.
    private static readonly Regex LabelLineRegex = new(@"^\s*([A-Za-z0-9 ()/\-]+?)[\s.]*:\s*(.*)$", RegexOptions.Compiled);

    public static async Task<List<DnsCacheEntry>> ReadCacheAsync()
    {
        var entries = new List<DnsCacheEntry>();
        try
        {
            string output = (await RunIpconfigAsync("/displaydns")).Output;
            entries = ParseDisplayDns(output);
        }
        catch
        {
            // Best-effort - empty list just means nothing to show.
        }
        return entries;
    }

    /// <summary>Parses `ipconfig /displaydns`'s per-record blocks into structured rows - internal
    /// (not private) so it's directly testable/reusable without needing a live process each call.</summary>
    internal static List<DnsCacheEntry> ParseDisplayDns(string output)
    {
        var entries = new List<DnsCacheEntry>();
        string? name = null, typeCode = null, data = null;
        int ttl = 0;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(name))
                entries.Add(new DnsCacheEntry(name, MapRecordType(typeCode), ttl, data ?? string.Empty));
            name = null; typeCode = null; data = null; ttl = 0;
        }

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Trim().Length == 0 || line.Contains("----", StringComparison.Ordinal)) continue;

            var match = LabelLineRegex.Match(line);
            if (!match.Success) continue;

            string label = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();
            if (label.Length == 0) continue;

            if (label.Equals("Record Name", StringComparison.OrdinalIgnoreCase))
            {
                Flush(); // a new record's Name line starts a fresh block
                name = value;
            }
            else if (label.Equals("Record Type", StringComparison.OrdinalIgnoreCase))
            {
                typeCode = value;
            }
            else if (label.Equals("Time To Live", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, out ttl);
            }
            else if (label.EndsWith("Record", StringComparison.OrdinalIgnoreCase) &&
                     !label.Equals("Record Type", StringComparison.OrdinalIgnoreCase))
            {
                // e.g. "A (Host) Record", "CNAME Record", "AAAA (Host) Record", "PTR Record" -
                // whichever type-specific line ipconfig printed the actual answer data on.
                data = value;
            }
        }
        Flush();
        return entries;
    }

    private static string MapRecordType(string? code) => code switch
    {
        "1" => "A",
        "2" => "NS",
        "5" => "CNAME",
        "6" => "SOA",
        "12" => "PTR",
        "15" => "MX",
        "16" => "TXT",
        "28" => "AAAA",
        "33" => "SRV",
        "255" => "ANY",
        null or "" => "Unknown",
        _ => $"Type {code}",
    };

    /// <summary>#518: `ipconfig /flushdns` - true on a clean run, false (never thrown) on any
    /// failure so the caller can show a plain "flush failed" message instead of crashing.</summary>
    public static async Task<bool> FlushAsync()
    {
        try
        {
            var (output, exitCode) = await RunIpconfigAsync("/flushdns");
            // English-locale success text, same documented limitation as every other netsh/ipconfig
            // text parse in this app - #1056: a non-English install won't print this string, so a
            // clean exit code counts as success too (ipconfig exits nonzero when the flush fails).
            return output.Contains("Successfully flushed", StringComparison.OrdinalIgnoreCase) || exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Task<(string Output, int? ExitCode)> RunIpconfigAsync(string args)
        => ToolRunner.RunCapturedAsync("ipconfig.exe", args, 10_000, timeoutOutput: string.Empty);
}
