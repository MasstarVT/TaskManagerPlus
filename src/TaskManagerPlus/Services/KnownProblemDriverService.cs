using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #500: loads the maintained known-problem-driver list from AppPaths.SettingsDirectory\
/// known-problem-drivers.json, seeding it from the embedded Resources\known-problem-drivers.json
/// the first time no such file exists there yet (same "ship a curated default, copy-on-first-run,
/// then load from - and let the user/a future updater edit - the settings-dir copy afterward"
/// pattern #418's pooltag.txt uses, just for a JSON list instead of a flat text dictionary; unlike
/// pooltag.txt this one is deliberately copied out to a real file rather than read straight from
/// the embedded resource every time, specifically so it stays editable without a rebuild). Matches
/// the loaded list against #424's KernelModuleService (currently-loaded kernel modules) and against
/// StabilityCrashSummaryState (the Stability tab's own FaultingModule data, when that tab has been
/// used this session) - every match is a quick flag, not a verdict; see KnownProblemDriverMatch's
/// remarks.
/// </summary>
public static class KnownProblemDriverService
{
    private const string EmbeddedResourceName = "known-problem-drivers.json";
    private static string SettingsPath => AppPaths.GetPath("known-problem-drivers.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Copies the embedded seed out to AppPaths.SettingsDirectory only when no file exists
    /// there yet - an existing file (including one a user or update mechanism has since edited) is
    /// never overwritten.</summary>
    private static void EnsureSeeded()
    {
        try
        {
            if (File.Exists(SettingsPath)) return;
            Directory.CreateDirectory(AppPaths.SettingsDirectory);

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null) return;
            using var fileStream = File.Create(SettingsPath);
            stream.CopyTo(fileStream);
        }
        catch
        {
            // Best-effort seed - LoadDefinitions below just finds an empty/missing list if this
            // failed, same fail-silently-to-defaults convention every settings file in this app uses.
        }
    }

    public static KnownProblemDriverList LoadDefinitions()
    {
        EnsureSeeded();
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<KnownProblemDriverList>(json, JsonOptions);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt/unreadable settings-dir copy (e.g. a bad hand-edit) - degrade to "no known-
            // problem-driver data available" rather than throwing out of an on-demand scan.
        }
        return new KnownProblemDriverList();
    }

    /// <summary>The button-gated scan itself - reads #424's loaded kernel module list (a real
    /// enumeration, not free) plus whatever the Stability tab has already reported this session, so
    /// this is deliberately not folded into DevicesDriversViewModel's automatic Refresh.</summary>
    public static async Task<List<KnownProblemDriverMatch>> ScanAsync()
    {
        var defs = LoadDefinitions().Drivers;
        if (defs.Count == 0) return new List<KnownProblemDriverMatch>();

        var modules = await KernelModuleService.ListAsync();
        var faultingModules = StabilityCrashSummaryState.HasScanned
            ? StabilityCrashSummaryState.FaultingModuleNames
            : Array.Empty<string>();

        var matches = new List<KnownProblemDriverMatch>();
        foreach (var def in defs)
        {
            if (string.IsNullOrWhiteSpace(def.FileName)) continue;

            var moduleMatch = modules.FirstOrDefault(m =>
                string.Equals(Path.GetFileName(m.FileName), def.FileName, StringComparison.OrdinalIgnoreCase));
            if (moduleMatch is not null)
            {
                var (inRange, version) = CheckVersionRange(moduleMatch, def);
                if (inRange)
                {
                    matches.Add(new KnownProblemDriverMatch
                    {
                        FileName = def.FileName,
                        Category = def.Category,
                        Description = def.Description,
                        EvidenceUrl = def.EvidenceUrl,
                        MatchedVersion = version,
                        MatchSourceText = version is null
                            ? "Currently loaded as a kernel module"
                            : $"Currently loaded as a kernel module (version {version})",
                    });
                    continue; // a loaded-module match is the stronger signal - don't also add a faulting-module row for the same file
                }
            }

            var faultHit = faultingModules.FirstOrDefault(f =>
                string.Equals(f, def.FileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(f), def.FileName, StringComparison.OrdinalIgnoreCase));
            if (faultHit is not null)
            {
                matches.Add(new KnownProblemDriverMatch
                {
                    FileName = def.FileName,
                    Category = def.Category,
                    Description = def.Description,
                    EvidenceUrl = def.EvidenceUrl,
                    MatchSourceText = "Named as a faulting module in a recent crash - see the Stability tab",
                });
            }
        }

        return matches.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Both bounds null means "flag this file name regardless of version" (returns true
    /// with no version read at all - some entries are known-bad at every version, e.g. sptd.sys).
    /// When a bound IS specified, this tries to read the loaded module's actual file version; if
    /// that can't be read, it still reports a match (file-name-only, version genuinely unknown)
    /// rather than silently dropping it - but a version that WAS read and falls outside the
    /// specified range is treated as real negative evidence and excluded.</summary>
    private static (bool Match, string? Version) CheckVersionRange(KernelModuleRow module, KnownProblemDriverDefinition def)
    {
        if (def.MinVersion is null && def.MaxVersion is null) return (true, null);

        string? versionText = ReadFileVersion(module.FullPath);
        if (versionText is null) return (true, null); // can't verify - still a name match, just unqualified

        if (!Version.TryParse(versionText, out var version)) return (true, versionText);

        if (def.MinVersion is not null && Version.TryParse(def.MinVersion, out var min) && version < min) return (false, versionText);
        if (def.MaxVersion is not null && Version.TryParse(def.MaxVersion, out var max) && version > max) return (false, versionText);

        return (true, versionText);
    }

    private static string? ReadFileVersion(string fullPath)
    {
        try
        {
            string resolved = ClassFilterDriverService.ResolveDriverPath(fullPath);
            if (!File.Exists(resolved)) return null;
            var info = FileVersionInfo.GetVersionInfo(resolved);
            return info.FileVersion?.Trim() is { Length: > 0 } v ? v : null;
        }
        catch
        {
            return null;
        }
    }
}
