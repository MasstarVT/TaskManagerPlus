using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #239: parses Windows Error Reporting's Report.wer files for AppHangB1/AppHangXProcB1/
/// AppHangTransient reports - %ProgramData%\Microsoft\Windows\WER\ReportArchive and \ReportQueue,
/// each report living in its own subfolder. Report.wer is a plain-text (UTF-16) key=value file, but
/// its exact key names have drifted across Windows versions and aren't a documented, versioned
/// contract - the same "search leniently rather than match one exact key" approach
/// EventLogService.ExtractBugcheckCode already takes for a similarly undocumented layout - so this
/// searches AppPath/App/Module-prefixed keys plus the Sig[n].Name/Sig[n].Value and DynamicSig[n]
/// pairs (which carry friendly names like "Application Name"/"Hang Type" pointing at their own
/// indexed value) rather than one fixed key. On-demand only (StabilityViewModel's RefreshCommand) -
/// a directory walk plus a parse per report is more than this app's per-tick timers do.
/// </summary>
public static class AppHangReportService
{
    private static readonly string[] EventTypesOfInterest = { "AppHangB1", "AppHangXProcB1", "AppHangTransient" };
    private static readonly Regex SigNameKeyRegex = new(@"^(Dynamic)?Sig\[(\d+)\]\.Name$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<AppHangReportEntry> Read()
    {
        var results = new List<AppHangReportEntry>();
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER");
            foreach (var subfolder in new[] { "ReportArchive", "ReportQueue" })
            {
                string dir = Path.Combine(root, subfolder);
                if (!Directory.Exists(dir)) continue;

                List<string> reportFolders;
                try { reportFolders = Directory.GetDirectories(dir).ToList(); }
                catch { continue; } // access denied to this subfolder - the other one may still work

                foreach (var folder in reportFolders)
                {
                    try
                    {
                        string werPath = Path.Combine(folder, "Report.wer");
                        if (!File.Exists(werPath)) continue;

                        var entry = ParseReport(werPath);
                        if (entry is not null) results.Add(entry);
                    }
                    catch { /* one bad/locked report folder shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // WER root unavailable - degrade to "no reports found", same as every other on-demand
            // scan in this app.
        }
        return results.OrderByDescending(e => e.Timestamp).ToList();
    }

    private static AppHangReportEntry? ParseReport(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); } // Report.wer carries a BOM - ReadAllLines auto-detects UTF-16 vs UTF-8 from it
        catch { return null; }

        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            if (key.Length == 0) continue;
            raw[key] = line[(eq + 1)..].Trim();
        }

        string eventType = raw.GetValueOrDefault("EventType") ?? string.Empty;
        if (!EventTypesOfInterest.Contains(eventType, StringComparer.OrdinalIgnoreCase)) return null;

        var named = ExtractSigNamedPairs(raw);

        string appPath = FindFirst(raw, "AppPath", "AppName")
            ?? named.GetValueOrDefault("Application Name")
            ?? FindByPrefix(raw, "Module")
            ?? FindByPrefix(raw, "App")
            ?? "(unknown)";

        string version = FindFirst(raw, "AppVer", "AppVersion")
            ?? named.GetValueOrDefault("Application Version")
            ?? string.Empty;

        string hangSignature = named.GetValueOrDefault("Hang Signature")
            ?? named.GetValueOrDefault("Hang Type")
            ?? string.Empty;

        // A hang report frequently has no single faulting module the way a crash does (nothing
        // threw - the UI thread just stopped pumping messages), so this degrades to blank rather
        // than guessing when none of these keys are present.
        string faultingModule = FindFirst(raw, "Module1", "FaultingModule", "FaultModName") ?? string.Empty;

        return new AppHangReportEntry
        {
            AppPath = appPath,
            AppVersion = version,
            EventType = eventType,
            HangSignature = hangSignature,
            FaultingModule = faultingModule,
            Timestamp = File.GetLastWriteTime(path),
            ReportFolder = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
        };
    }

    /// <summary>Sig[n].Name/Sig[n].Value and DynamicSig[n].Name/DynamicSig[n].Value are stored as
    /// separate indexed keys - this re-pairs them into friendly-name -> value (e.g. "Application
    /// Name" -> "notepad.exe"), regardless of which index each pair happened to land on.</summary>
    private static Dictionary<string, string> ExtractSigNamedPairs(Dictionary<string, string> raw)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw)
        {
            var m = SigNameKeyRegex.Match(kv.Key);
            if (!m.Success) continue;

            string prefix = m.Groups[1].Success ? "DynamicSig" : "Sig";
            string idx = m.Groups[2].Value;
            if (raw.TryGetValue($"{prefix}[{idx}].Value", out var value) && !string.IsNullOrWhiteSpace(kv.Value))
                named[kv.Value] = value;
        }
        return named;
    }

    private static string? FindFirst(Dictionary<string, string> raw, params string[] exactKeys)
    {
        foreach (var k in exactKeys)
            if (raw.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static string? FindByPrefix(Dictionary<string, string> raw, string prefix)
    {
        foreach (var kv in raw)
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                return kv.Value;
        return null;
    }
}
