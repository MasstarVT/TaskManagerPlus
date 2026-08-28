using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #915: persists every finished Troubleshoot run to
/// <c>AppPaths.SettingsDirectory\Runs\&lt;timestamp&gt;-&lt;symptom&gt;.json</c> as its own small
/// file, and lists/reloads them for the tab's "Past runs" panel. Follows the same
/// fail-silently-to-defaults convention as every other persisted-settings file in this app (see
/// CLAUDE.md) - a missing/corrupt run file is skipped from the list rather than surfaced as an
/// error, and a save that throws (disk full, permissions) is swallowed rather than interrupting the
/// run that just finished.
/// </summary>
public static class TroubleshootRunHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string RunsDirectory => AppPaths.GetPath("Runs");

    public static TroubleshootRunRecord ToRecord(TroubleshootRun run) => new()
    {
        SymptomId = run.SymptomId,
        DisplayName = run.DisplayName,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        VerdictText = run.VerdictText,
        Steps = run.Steps.Select(s => new TroubleshootStepRecord
        {
            Id = s.Id,
            Label = s.Label,
            Description = s.Description,
            Status = s.Status,
            ResultText = s.ResultText,
            Evidence = s.Evidence.ToList(),
            DurationMs = s.Duration?.TotalMilliseconds,
        }).ToList(),
    };

    /// <summary>Rebuilds a read-only <see cref="TroubleshootRun"/> from a saved record for the
    /// "open a past run" view - each step's Check is a no-op stand-in (never invoked; the run view
    /// only reads the already-populated Status/ResultText/Evidence back, it doesn't re-execute).</summary>
    public static TroubleshootRun ToRun(TroubleshootRunRecord record)
    {
        var run = new TroubleshootRun
        {
            SymptomId = record.SymptomId,
            DisplayName = record.DisplayName,
            StartedAt = record.StartedAt,
            FinishedAt = record.FinishedAt,
            IsRunning = false,
            VerdictText = record.VerdictText,
        };

        foreach (var s in record.Steps)
        {
            var step = new DiagnosticStep
            {
                Id = s.Id,
                Label = s.Label,
                Description = s.Description,
                Check = _ => Task.FromResult(DiagnosticStepResult.Skip("(replayed from a saved run - not re-executed)")),
            };
            step.Status = s.Status;
            step.ResultText = s.ResultText;
            step.Evidence = s.Evidence;
            step.Duration = s.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
            run.Steps.Add(step);
        }

        return run;
    }

    public static void Save(TroubleshootRun run)
    {
        try
        {
            Directory.CreateDirectory(RunsDirectory);
            string safeSymptom = new string(run.SymptomId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            string fileName = $"{run.StartedAt:yyyyMMdd-HHmmss}-{safeSymptom}.json";
            string path = Path.Combine(RunsDirectory, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(ToRecord(run), JsonOptions));
        }
        catch
        {
            // Best-effort - a run that just finished should never fail because its transcript
            // couldn't be written (disk full, permissions, ...).
        }
    }

    /// <summary>Newest-first. Skips (rather than throws on) any file that fails to parse.</summary>
    public static List<TroubleshootRunRecord> ListSaved()
    {
        var results = new List<TroubleshootRunRecord>();
        try
        {
            if (!Directory.Exists(RunsDirectory)) return results;
            foreach (var file in Directory.EnumerateFiles(RunsDirectory, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<TroubleshootRunRecord>(File.ReadAllText(file));
                    if (record is null) continue;
                    record.FilePath = file;
                    results.Add(record);
                }
                catch
                {
                    // Corrupt/partial file - skip it, same as ThemeService/theme.json.
                }
            }
        }
        catch
        {
            // Runs directory unreadable - degrade to "no saved runs".
        }
        return results;
    }
}
