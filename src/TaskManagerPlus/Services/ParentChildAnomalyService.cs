using System.IO;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #855: a small parent-child rule set, evaluated per-row using data
/// ProcessMonitorService.Sample already collects every tick (ParentName/ParentPid/IntegrityLevel/
/// FilePath) - no new syscalls, safe for the per-tick poll path per CLAUDE.md's "on-demand vs
/// polled" rule.
///
/// (a) Office/PDF/browser processes spawning a scripting/shell interpreter as a DIRECT child - a
///     classic macro/exploit-chain pattern (a malicious document launching PowerShell, etc).
/// (b) services.exe spawning something whose image lives outside System32/SysWOW64 - service host
///     processes should almost always be system binaries.
/// (c) explorer.exe (Medium integrity) as the parent of a System-integrity process - not a normal
///     parent/child relationship; System-integrity processes are normally spawned by other system
///     processes, not the user's shell.
/// (d) a process whose parent PID no longer exists in this same snapshot (the parent already
///     exited) AND whose own image lives in a user-writable location - reuses
///     WritablePathHeuristics, the same shared helper #849/#850 already established.
///
/// "Quick flag, not a verdict" - every rule here is a pattern-match on otherwise-ambiguous data,
/// same tradeoff as every other heuristic flag on ProcessRow. A legitimate automation script, a
/// portable/non-standard service host, or a coincidental timing race can also match any of these.
/// </summary>
public static class ParentChildAnomalyService
{
    private static readonly string[] OfficePdfBrowserNames =
    {
        "winword", "excel", "powerpnt", "outlook", "acrord32", "acrobat", "chrome", "msedge", "firefox",
    };

    private static readonly string[] ScriptingChildNames =
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta",
    };

    // Resolved once rather than per-row - services.exe is the parent of every running NT service
    // (svchost.exe alone can be dozens of processes), so rule (b) below can run for many rows every
    // tick; caching these avoids a repeated Environment.GetFolderPath/Path.GetFullPath per row.
    private static readonly string System32Root = ResolveSystem32Root();
    private static readonly string SysWow64Root = ResolveSysWow64Root();

    /// <summary>Evaluates one already-sampled row - livePids is the full set of pids seen in this
    /// same snapshot (ProcessMonitorService.Sample's seenPids), used for rule (d)'s "parent already
    /// exited" check.</summary>
    public static string? Evaluate(ProcessRow row, HashSet<int> livePids)
    {
        string bareName = StripExeSuffix(row.Name);
        string bareParent = StripExeSuffix(row.ParentName);

        if (OfficePdfBrowserNames.Contains(bareParent, StringComparer.OrdinalIgnoreCase) &&
            ScriptingChildNames.Contains(bareName, StringComparer.OrdinalIgnoreCase))
        {
            return $"Spawned directly by \"{row.ParentName}\" - an office/PDF/browser app launching a script or shell interpreter is an unusual pattern (macro/exploit-chain). Quick flag, not a verdict.";
        }

        if (bareParent.Equals("services", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(row.FilePath) && !IsUnderSystemDirectory(row.FilePath))
        {
            return $"Spawned by services.exe but running from outside System32/SysWOW64 (\"{row.FilePath}\") - worth a manual check. Quick flag, not a verdict.";
        }

        if (bareParent.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.IntegrityLevel, "System", StringComparison.OrdinalIgnoreCase))
        {
            return "Parent is explorer.exe (Medium integrity) but this process runs at System integrity - not a normal parent/child relationship. Quick flag, not a verdict.";
        }

        if (row.ParentPid > 0 && !livePids.Contains(row.ParentPid) &&
            WritablePathHeuristics.IsUnderUserWritableRoot(row.FilePath))
        {
            return "Its parent process has already exited, and this process's image is in a user-writable location - worth a manual check. Quick flag, not a verdict.";
        }

        return null;
    }

    private static string StripExeSuffix(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Deliberately duplicated (not shared with ProcessTrustService.IsUnderSystemDirectory,
    /// which is private to that class) - a small, cheap, self-contained check, same tradeoff every
    /// other small service in this app makes rather than adding cross-file coupling for one shared
    /// helper. The System32/SysWOW64 roots themselves are resolved once (see the static fields
    /// above), so this is just a couple of string comparisons per call.</summary>
    private static bool IsUnderSystemDirectory(string filePath)
    {
        // An empty root (resolution failed at startup - essentially never happens on a real Windows
        // install) would make StartsWith("") match everything below, silently disabling rule (b)
        // rather than false-flagging every service child - degrade to "nothing to compare against",
        // per this app's "degrade, never fabricate" rule, rather than risk a false positive storm.
        if (string.IsNullOrEmpty(System32Root) && string.IsNullOrEmpty(SysWow64Root)) return true;

        try
        {
            var full = Path.GetFullPath(filePath);
            return (!string.IsNullOrEmpty(System32Root) && full.StartsWith(System32Root, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrEmpty(SysWow64Root) && full.StartsWith(SysWow64Root, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSystem32Root()
    {
        try { return Path.GetFullPath(Environment.SystemDirectory); }
        catch { return string.Empty; }
    }

    private static string ResolveSysWow64Root()
    {
        try { return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")); }
        catch { return string.Empty; }
    }
}
