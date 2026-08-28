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
    public string ExtractedDirectory { get; init; } = string.Empty;
}

/// <summary>
/// suggestions.md #995: "Open bundle" - extracts a previously exported evidence bundle .zip
/// (EvidenceBundleService's own output shape: manifest.json + index.html at the root, plus
/// AppData\findings.json / AppData\timeline.json / AppData\Baselines\*.json for this app's own
/// data) into a temp folder and loads its JSON contents into a read-only view. A bundle collected
/// from an older build without one of those AppData files just degrades to an empty list for that
/// section (JSON parse failures are swallowed the same way SnapshotService.Load/BaselineService.
/// LoadAll already degrade on a malformed/missing file) rather than refusing to open the bundle at
/// all - a partial bundle is still worth reviewing.
/// </summary>
public static class BundleReviewService
{
    public static LoadedBundle Extract(string zipPath)
    {
        string extractDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-BundleReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var manifest = ReadJson<EvidenceBundleManifest>(Path.Combine(extractDir, "manifest.json")) ?? new EvidenceBundleManifest();
        var findings = ReadJson<List<HealthIssue>>(Path.Combine(extractDir, "AppData", "findings.json")) ?? new List<HealthIssue>();
        var timeline = ReadJson<List<TimelineEvent>>(Path.Combine(extractDir, "AppData", "timeline.json")) ?? new List<TimelineEvent>();

        var baselines = new List<PerformanceBaseline>();
        try
        {
            string baselinesDir = Path.Combine(extractDir, "AppData", "Baselines");
            if (Directory.Exists(baselinesDir))
            {
                foreach (var file in Directory.GetFiles(baselinesDir, "*.json"))
                {
                    var b = ReadJson<PerformanceBaseline>(file);
                    if (b is not null) baselines.Add(b);
                }
            }
        }
        catch { /* best-effort - see class remarks */ }

        return new LoadedBundle
        {
            Manifest = manifest,
            Findings = findings,
            TimelineEvents = timeline.OrderByDescending(e => e.Timestamp).ToList(),
            Baselines = baselines.OrderBy(b => b.CapturedAt).ToList(),
            ExtractedDirectory = extractDir,
        };
    }

    /// <summary>Deletes the temp extraction folder from a previously loaded bundle - called when
    /// the review panel closes or a new bundle is opened over an existing one.</summary>
    public static void Cleanup(string? extractedDirectory)
    {
        if (string.IsNullOrEmpty(extractedDirectory)) return;
        try { if (Directory.Exists(extractedDirectory)) Directory.Delete(extractedDirectory, recursive: true); }
        catch { /* best-effort - a locked file just leaves a stray temp folder, same tradeoff EvidenceBundleViewModel's own cleanup already accepts */ }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
