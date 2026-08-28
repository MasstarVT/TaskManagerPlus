using System.IO;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #854: flags a RUNNING PROCESS whose image path sits in a location malware commonly
/// drops files into. This is the running-process side of #854 - the AUTORUN-TARGET side was already
/// covered by AutorunsService.BuildUserWritableRoots in an earlier round, so it isn't touched here.
///
/// Not proof of anything malicious - a great many legitimate installers/updaters/portable apps also
/// run from Downloads or Temp, and %ProgramData%/WinSxS-style paths legitimately contain
/// GUID/hex-looking folder names. "Quick flag, not a verdict" - same framing as every other
/// heuristic in this app.
///
/// Cheap: every check here is a plain string/path comparison (plus a couple of small regexes) over
/// FilePath, which ProcessMonitorService.Sample already resolves every tick with no extra syscall -
/// safe for the per-tick poll path per CLAUDE.md's "on-demand vs polled" rule. The root-prefix list
/// is resolved once and cached (environment/special-folder lookups aren't free enough to repeat for
/// every process on every tick).
/// </summary>
public static class SuspiciousLocationHeuristicService
{
    // #854: %Temp%, %LocalAppData%\Temp, Downloads, %Public% - any subfolder counts (prefix match).
    private static readonly (string Prefix, string Label)[] SubtreeRoots = BuildSubtreeRoots();

    // #854: the ROOT of the system drive and the ROOT of %ProgramData% - only a file placed
    // DIRECTLY there counts, not one in any subfolder (a subfolder there is entirely normal).
    private static readonly string SystemDriveRoot = ResolveSystemDriveRoot();
    private static readonly string ProgramDataRoot = ResolveProgramDataRoot();

    private static readonly Regex HexLikeSegment =
        new(@"^[0-9a-f]{8,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidLikeSegment =
        new(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Evaluate(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        foreach (var (prefix, label) in SubtreeRoots)
        {
            if (!string.IsNullOrEmpty(prefix) && filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"Running from {label} - a common malware drop location. Quick flag, not a verdict.";
        }

        if (filePath.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            return "Running from a Recycle Bin folder - a common malware drop location. Quick flag, not a verdict.";

        string? dir;
        try { dir = Path.GetDirectoryName(filePath); }
        catch { return null; }
        if (string.IsNullOrEmpty(dir)) return null;

        if (!string.IsNullOrEmpty(SystemDriveRoot) && string.Equals(dir, SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
            return $"Running directly from the root of {SystemDriveRoot} (not a subfolder) - a common malware drop location. Quick flag, not a verdict.";
        if (!string.IsNullOrEmpty(ProgramDataRoot) && string.Equals(dir, ProgramDataRoot, StringComparison.OrdinalIgnoreCase))
            return "Running directly from the ProgramData root (not a subfolder) - a common malware drop location. Quick flag, not a verdict.";

        // A randomly-generated-looking path segment - a simple proxy for "malware drop folder",
        // not a real entropy calculation (deliberately, per #854's own scope note).
        foreach (var segment in dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length < 8) continue;
            if (HexLikeSegment.IsMatch(segment) || GuidLikeSegment.IsMatch(segment))
                return $"Path contains a randomly-generated-looking folder name (\"{segment}\") - worth a second look. Quick flag, not a verdict.";
            if (segment.Length >= 10 && DigitDensity(segment) > 0.4)
                return $"Path contains a folder name with unusually high digit density (\"{segment}\") - worth a second look. Quick flag, not a verdict.";
        }

        return null;
    }

    private static double DigitDensity(string s) => (double)s.Count(char.IsDigit) / s.Length;

    private static (string, string)[] BuildSubtreeRoots()
    {
        var roots = new List<(string Prefix, string Label)>();
        TryAdd(roots, () => Environment.GetEnvironmentVariable("TEMP"), "%Temp%");
        TryAdd(roots, () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), "%LocalAppData%\\Temp");
        TryAdd(roots, () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "the Downloads folder");
        TryAdd(roots, () => Environment.ExpandEnvironmentVariables("%Public%"), "%Public%");
        return roots.ToArray();
    }

    private static void TryAdd(List<(string, string)> roots, Func<string?> resolve, string label)
    {
        try
        {
            var value = resolve();
            if (!string.IsNullOrWhiteSpace(value)) roots.Add((value!, label));
        }
        catch { /* best-effort - just skip this root if it can't be resolved */ }
    }

    private static string ResolveSystemDriveRoot()
    {
        try { return Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string ResolveProgramDataRoot()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
        catch { return string.Empty; }
    }
}
