using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, #843: "Mark of the Web" - the :Zone.Identifier NTFS alternate data stream
/// Windows writes onto a file downloaded from the internet (or copied off removable/network media
/// with MOTW propagation). No third-party API needed - the colon-suffixed ADS path works directly
/// with plain File I/O on NTFS.</summary>
public sealed record ZoneIdentifierInfo(bool Found, string? ZoneId, string? ZoneDescription, string? ReferrerUrl, string? HostUrl, string RawContent)
{
    public static readonly ZoneIdentifierInfo NotFound = new(false, null, null, null, null, string.Empty);
}

public static class ZoneIdentifierService
{
    public static ZoneIdentifierInfo Read(string path)
    {
        try
        {
            var adsPath = path + ":Zone.Identifier";
            if (!File.Exists(adsPath)) return ZoneIdentifierInfo.NotFound;

            var content = File.ReadAllText(adsPath);
            string? zoneId = null, referrerUrl = null, hostUrl = null;

            // Simple line-split INI-like parse (#843's own scope note - no need for a real INI
            // parser here) - looks for ZoneId/ReferrerUrl/HostUrl under any section header.
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('[')) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase)) zoneId = value;
                else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrerUrl = value;
                else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) hostUrl = value;
            }

            return new ZoneIdentifierInfo(true, zoneId, DescribeZone(zoneId), referrerUrl, hostUrl, content);
        }
        catch
        {
            // No ADS at all (most files - not downloaded from the internet), a non-NTFS volume
            // (ADS unsupported), or access denied - degrade to "no Mark of the Web found" cleanly,
            // same as every other "quick flag" check in this app. Not finding one is the normal,
            // expected case.
            return ZoneIdentifierInfo.NotFound;
        }
    }

    private static string? DescribeZone(string? zoneId) => zoneId switch
    {
        "0" => "Local computer",
        "1" => "Local intranet",
        "2" => "Trusted sites",
        "3" => "Internet",
        "4" => "Restricted sites",
        _ => null,
    };
}
