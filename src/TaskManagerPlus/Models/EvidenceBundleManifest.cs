namespace TaskManagerPlus.Models;

/// <summary>#986: one file (or one attempted-but-failed collector) recorded in a bundle's
/// manifest.json - source command, exact collection timestamp, size, and a SHA-256 hash for a
/// file that was actually produced; a reason instead when the collector failed or timed out
/// without producing anything. Kept as a plain DTO (System.Text.Json serializes it directly),
/// mirroring every other persisted-record shape in this app (e.g. PerformanceBaseline,
/// ChangeJournalEntry).</summary>
public sealed class EvidenceBundleManifestEntry
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>The exact command/API this collector ran - e.g. "msinfo32.exe /nfo <path>",
    /// "wevtutil.exe epl System <path>", or "This app's own rules engine" for the built-in
    /// findings/timeline/baseline exports - so a recipient can see exactly how each file was
    /// produced, not just that it exists.</summary>
    public string SourceCommand { get; init; } = string.Empty;

    public DateTime CollectedAtUtc { get; init; }

    public bool Success { get; init; }

    /// <summary>Relative path within the bundle (e.g. "systeminfo.txt", "AppData\findings.json")
    /// - empty when Success is false.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Settable (not init-only) - #984's scrub pass rewrites a text file's content after
    /// this entry was first built, which changes both its size and hash; EvidenceBundleViewModel
    /// re-stamps both post-scrub rather than leaving the manifest describing the pre-scrub file.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Empty when Success is false (nothing to hash) or when hashing itself failed.
    /// Settable - see SizeBytes' remarks.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Why this collector produced nothing - a timeout, "no battery found", "log
    /// unavailable", etc. Empty when Success is true.</summary>
    public string FailureReason { get; init; } = string.Empty;
}

/// <summary>#986: the whole manifest.json written into every evidence bundle - a header plus one
/// entry per collector, success or failure, so a recipient can see both what's included and
/// what's missing and why (rendered into index.html too - see EvidenceBundleService.BuildIndexHtml).</summary>
public sealed class EvidenceBundleManifest
{
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string MachineName { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>#984: true when the "Scrub personal info" pass ran over this bundle's text
    /// artifacts before zipping - recorded here so a recipient (or a later "was this scrubbed?"
    /// check) doesn't have to guess. Settable - only known for certain once the (optional) scrub
    /// review step actually completes, after the manifest itself was first built.</summary>
    public bool WasScrubbed { get; set; }

    public List<EvidenceBundleManifestEntry> Entries { get; init; } = new();
}
