using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>suggestions.md #995: everything BundleReviewViewModel loaded from one previously
/// exported evidence bundle .zip - read-only, no live poller behind any of it.</summary>
public sealed class LoadedBundle
{
    public EvidenceBundleManifest Manifest { get; init; } = new();
    public List<HealthIssue> Findings { get; init; } = new();
    public List<TimelineEvent> TimelineEvents { get; init; } = new();
    public List<PerformanceBaseline> Baselines { get; init; } = new();
}

/// <summary>
/// suggestions.md #995: "Open bundle" - reads a previously exported evidence bundle .zip
/// (EvidenceBundleService's own output shape: manifest.json + index.html at the root, plus
/// AppData\findings.json / AppData\timeline.json / AppData\Baselines\*.json for this app's own
/// data) and loads its JSON contents into a read-only view. A bundle collected from an older build
/// without one of those AppData files just degrades to an empty list for that section (JSON parse
/// failures are swallowed the same way SnapshotService.Load/BaselineService.LoadAll already degrade
/// on a malformed/missing file) rather than refusing to open the bundle at all - a partial bundle is
/// still worth reviewing.
///
/// A bundle is someone else's file, opened by an elevated process: only those few JSON entries are
/// read, straight out of the archive and each under a size cap - nothing is extracted to disk, so
/// a crafted entry name or a zip bomb has nowhere to write.
/// </summary>
public static class BundleReviewService
{
    // findings/timeline JSON from a real bundle is a few MB at most.
    private const long MaxEntryBytes = 64L * 1024 * 1024;

    public static LoadedBundle Load(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var manifest = ReadJson<EvidenceBundleManifest>(zip, "manifest.json") ?? new EvidenceBundleManifest();
        var findings = ReadJson<List<HealthIssue>>(zip, "AppData/findings.json") ?? new List<HealthIssue>();
        var timeline = ReadJson<List<TimelineEvent>>(zip, "AppData/timeline.json") ?? new List<TimelineEvent>();

        var baselines = new List<PerformanceBaseline>();
        try
        {
            foreach (var entry in zip.Entries)
            {
                string name = NormalizeName(entry.FullName);
                if (!name.StartsWith("AppData/Baselines/", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf('/', "AppData/Baselines/".Length) >= 0)
                    continue;
                var b = ReadJson<PerformanceBaseline>(entry);
                if (b is not null) baselines.Add(BaselineService.Sanitize(b));
            }
        }
        catch { /* best-effort - see class remarks */ }

        // A bundle is foreign data: null array elements would throw in the sort below or reach the
        // review grids as null rows.
        if (manifest.Entries is null || manifest.Entries.Any(e => e is null))
        {
            manifest = new EvidenceBundleManifest
            {
                GeneratedAtUtc = manifest.GeneratedAtUtc,
                MachineName = manifest.MachineName,
                AppVersion = manifest.AppVersion,
                WasScrubbed = manifest.WasScrubbed,
                Entries = manifest.Entries?.Where(e => e is not null).ToList() ?? new List<EvidenceBundleManifestEntry>(),
            };
        }

        return new LoadedBundle
        {
            Manifest = manifest,
            Findings = findings.Where(f => f is not null).ToList(),
            TimelineEvents = timeline.Where(e => e is not null).OrderByDescending(e => e.Timestamp).ToList(),
            Baselines = baselines.OrderBy(b => b.CapturedAt).ToList(),
        };
    }

    // Bundles written on Windows may use either separator in entry names.
    private static string NormalizeName(string entryName) => entryName.Replace('\\', '/').TrimStart('/');

    private static T? ReadJson<T>(ZipArchive zip, string name) where T : class
    {
        var entry = zip.Entries.FirstOrDefault(e => NormalizeName(e.FullName).Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ReadJson<T>(entry);
    }

    private static T? ReadJson<T>(ZipArchiveEntry entry) where T : class
    {
        try
        {
            if (entry.Length > MaxEntryBytes) return null;
            using var stream = entry.Open();
            // Length is the archive's own claim - cap what is actually decompressed as well.
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxEntryBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return JsonSerializer.Deserialize<T>(buffer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
