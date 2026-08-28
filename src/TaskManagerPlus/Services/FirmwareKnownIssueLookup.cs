namespace TaskManagerPlus.Services;

/// <summary>#384: one shipped model-line entry mentioning that some of its firmware revisions have
/// had publicly-documented issues - deliberately generic ("check the vendor's site"), not a
/// fabricated revision-to-bug mapping this app can't verify. Matched by Win32_DiskDrive.Model
/// prefix, same StartsWith-ordered-by-specificity scheme as SmartVendorProfiles.</summary>
public sealed record FirmwareKnownIssueEntry(string ModelPrefix, string Note);

/// <summary>#384's lookup result for one disk - a starter, non-exhaustive list.</summary>
public sealed record FirmwareLookupResult(bool Matched, string DisplayText);

/// <summary>
/// Round 19, #384: a small, shipped static list of model *lines* (not specific firmware revisions -
/// this app has no way to verify a real revision-to-bug mapping with confidence, and CLAUDE.md's
/// "degrade rather than fabricate" rule applies just as much to data tables as to live reads) that
/// have had publicly-documented firmware issues at some point in their history. A match is framed as
/// "worth checking the vendor's site for a firmware update", never "your drive will fail" or a claim
/// about the specific revision currently installed - this app can't confirm which exact revision fix
/// a system is running, only that the model line has a documented history worth a manual check. Same
/// "small in-code data table, not a settings file" tier as SmartVendorProfiles/#304.
/// </summary>
public static class FirmwareKnownIssueLookup
{
    // Kept deliberately small and generic - a handful of well-known, widely-reported cases rather
    // than an attempt at a comprehensive database. Ordered most-specific prefix first, same
    // rationale as SmartVendorProfiles.Profiles.
    private static readonly FirmwareKnownIssueEntry[] Entries =
    {
        new("Samsung SSD 840 EVO", "Some early firmware revisions of the 840 EVO line had a well-documented read-performance-degradation issue on old, infrequently-rewritten data (Samsung shipped multiple firmware/Magician fixes for this). Worth checking Samsung Magician for the latest firmware if this drive feels slow on old files."),
        new("Samsung SSD 850 EVO", "The 850 EVO line also saw a variant of the same old-data read-slowdown issue as the 840 EVO on some early firmware. Worth checking Samsung Magician for the latest firmware."),
        new("CT", "Some early Crucial (Micron-controller) consumer SSD firmware revisions have had publicly-documented BSOD/disconnect issues fixed in later updates. Worth checking Crucial Storage Executive for the latest firmware."),
        new("Crucial", "Some early Crucial consumer SSD firmware revisions have had publicly-documented BSOD/disconnect issues fixed in later updates. Worth checking Crucial Storage Executive for the latest firmware."),
        new("INTEL SSDSC", "Some Intel SATA SSD firmware revisions have had publicly-documented issues (including a widely-reported 8MB/unresponsive-after-power-cycle bug on certain 2013-era models) fixed in later firmware. Worth checking Intel's SSD Toolbox / support site."),
        new("SanDisk SD", "Some SanDisk consumer SSD firmware revisions have had publicly-documented BSOD/compatibility issues (particularly alongside certain Windows updates) fixed in later firmware. Worth checking SanDisk SSD Dashboard."),
        new("HGST", "Some HGST/WD enterprise HDD firmware lines have had publicly-documented issues specific to certain capacity points and RAID controller combinations. Worth checking WD/HGST's support site for this exact model."),
        new("ST", "Seagate has shipped firmware updates for specific model lines addressing publicly-documented issues (including a widely-reported early-life failure pattern on some 2016-era desktop HDDs). Worth checking SeaTools / Seagate's support site for this exact model."),
    };

    /// <summary>This is explicitly a starter list, not exhaustive - shown as a caption alongside
    /// every result so a "no match" never reads as "this drive has a clean bill of health".</summary>
    public const string CoverageCaption = "Checked against a small, starter list of publicly-documented model-line firmware issues - not exhaustive. No match here doesn't mean no firmware issues exist for this drive; check the vendor's site directly if you have concerns.";

    public static FirmwareLookupResult Match(string model)
    {
        var trimmed = model?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return new FirmwareLookupResult(false, "Unknown model.");

        foreach (var entry in Entries)
        {
            if (trimmed.StartsWith(entry.ModelPrefix, StringComparison.OrdinalIgnoreCase))
                return new FirmwareLookupResult(true, entry.Note);
        }
        return new FirmwareLookupResult(false, "No known-issue entry matched this drive's model line.");
    }
}
