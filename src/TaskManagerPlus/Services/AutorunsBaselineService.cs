using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 13, #803: "Save baseline" writes the full persistence enumeration to
/// autoruns-baseline.json under AppPaths.SettingsDirectory; "Compare to baseline" then reports
/// Added/Removed/Changed rows since the machine was last known-good. Diffing a known-good snapshot
/// is far more informative than staring at a couple hundred legitimate entries every scan, so this
/// is the single highest-value thing the Security tab's Persistence section offers.
///
/// Same "small JSON file under AppPaths.SettingsDirectory, fails silently to defaults on a
/// missing/corrupt file" shape as every other *SettingsService in this app (see
/// LoggingSettingsService) - a missing or unreadable baseline just means Diff() reports
/// HasBaseline = false rather than throwing.
/// </summary>
public static class AutorunsBaselineService
{
    private static string BaselinePath => AppPaths.GetPath("autoruns-baseline.json");

    public static bool HasBaseline()
    {
        try
        {
            return File.Exists(BaselinePath);
        }
        catch
        {
            return false;
        }
    }

    public static void SaveBaseline(IEnumerable<AutorunEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(BaselinePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(BaselinePath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, "Compare to baseline" will just report
            // "no baseline" next time rather than silently comparing against stale data.
        }
    }

    /// <summary>Compares <paramref name="current"/> against the last saved baseline, keyed on
    /// (Category, Location, Name) - the combination that identifies "the same persistence slot"
    /// across two scans, regardless of what command/DLL it currently points at.</summary>
    public static AutorunsDiffResult Diff(IEnumerable<AutorunEntry> current)
    {
        var result = new AutorunsDiffResult();

        List<AutorunEntry>? baseline;
        try
        {
            if (!File.Exists(BaselinePath))
            {
                result.HasBaseline = false;
                return result;
            }

            var json = File.ReadAllText(BaselinePath);
            baseline = JsonSerializer.Deserialize<List<AutorunEntry>>(json);
            if (baseline is null)
            {
                result.HasBaseline = false;
                return result;
            }
        }
        catch
        {
            // Corrupt/unreadable baseline file - treat exactly like "no baseline yet".
            result.HasBaseline = false;
            return result;
        }

        result.HasBaseline = true;

        var baselineByKey = new Dictionary<string, AutorunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in baseline) baselineByKey[KeyOf(e)] = e; // last write wins on a duplicate key

        var currentByKey = new Dictionary<string, AutorunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in current) currentByKey[KeyOf(e)] = e;

        foreach (var (key, entry) in currentByKey)
        {
            if (!baselineByKey.TryGetValue(key, out var baseEntry))
            {
                result.Added.Add(entry);
            }
            else if (!string.Equals(baseEntry.RawCommand, entry.RawCommand, StringComparison.Ordinal)
                     || !string.Equals(baseEntry.ResolvedPath, entry.ResolvedPath, StringComparison.Ordinal)
                     || baseEntry.Enabled != entry.Enabled)
            {
                result.Changed.Add(entry);
            }
        }

        foreach (var (key, entry) in baselineByKey)
        {
            if (!currentByKey.ContainsKey(key))
                result.Removed.Add(entry);
        }

        return result;
    }

    private static string KeyOf(AutorunEntry e) => $"{e.Category}␟{e.Location}␟{e.Name}";
}
