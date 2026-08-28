using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 14, #840: "expected Microsoft binary" check. For the handful of well-known Windows
/// system process names (svchost, lsass, csrss, services, winlogon, explorer, spoolsv, dllhost,
/// taskhostw), flags a RUNNING process claiming one of these names if its image path isn't under
/// System32/SysWOW64 or isn't Microsoft-signed - a classic process-masquerading trick (malware
/// naming itself "svchost.exe" and running from, say, %AppData%). Separately flags near-miss/
/// typo-squat names against that same list (scvhost, svch0st, lsass32, ...) even when the exact
/// name doesn't match at all.
///
/// "Quick flag, not a verdict" - both checks are pattern-matches on otherwise-ambiguous data, the
/// same tradeoff as every other heuristic flag on ProcessRow (duplicate-instance/leak-suspect/
/// spawn-group). A legitimate third-party tool that happens to share one of these names, or a
/// deliberately-renamed-but-harmless utility, would also match.
///
/// Kept cheap for a per-tick poll (see ProcessMonitorService.Sample, which calls this once per
/// process every tick): the near-miss check is a bare string comparison run against every process
/// name; the path+signature check only runs for a name that's an EXACT match to the watch list,
/// and reuses SignatureCheckService's shared per-path cache (already being consulted for the row's
/// own SignatureStatus column, so this adds no extra disk/native work beyond what
/// ProcessMonitorService already does per process).
/// </summary>
public static class ProcessTrustService
{
    private static readonly string[] ExpectedSystemNames =
    {
        "svchost", "lsass", "csrss", "services", "winlogon", "explorer", "spoolsv", "dllhost", "taskhostw",
    };

    /// <summary>Evaluates one already-sampled process row's name/path and returns a short warning
    /// string, or null when there's nothing to flag.</summary>
    public static string? Evaluate(string processName, string? filePath)
    {
        var bareName = StripExeSuffix(processName);

        foreach (var expected in ExpectedSystemNames)
        {
            if (!bareName.Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;

            // Exact match to a watch-list name - check location + signature. Couldn't resolve a
            // path at all (protected process, race with exit) - nothing to compare, don't guess.
            if (string.IsNullOrEmpty(filePath)) return null;

            bool underSystemDir = IsUnderSystemDirectory(filePath);
            var signer = SignatureCheckService.GetSignerInfo(filePath);
            bool microsoftSigned = ContainsMicrosoft(signer.SubjectCn) || ContainsMicrosoft(signer.IssuerCn);

            if (underSystemDir && microsoftSigned) return null; // right place, Microsoft-signed - clean

            return underSystemDir
                ? $"\"{processName}\" is in System32/SysWOW64 but isn't Microsoft-signed - worth a manual check."
                : $"\"{processName}\" is running from outside System32/SysWOW64 ({filePath}) - worth a manual check.";
        }

        // Not an exact match to any watch-list name - check for a near-miss/typo-squat instead
        // (cheap, string-only; runs for every process every tick).
        foreach (var expected in ExpectedSystemNames)
        {
            if (IsNearMissTypo(bareName, expected))
                return $"\"{processName}\" looks like a near-miss of the system process name \"{expected}.exe\" - worth a manual check.";
        }

        return null;
    }

    private static string StripExeSuffix(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool ContainsMicrosoft(string? s) => s is not null && s.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderSystemDirectory(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var system32 = Path.GetFullPath(Environment.SystemDirectory); // %SystemRoot%\System32
            var sysWow64 = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64"));
            return full.StartsWith(system32, StringComparison.OrdinalIgnoreCase) || full.StartsWith(sysWow64, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deliberately simple, non-exhaustive typo-squat heuristic (#840's own scope note -
    /// "keep this heuristic simple, it doesn't need to be exhaustive"): a same-length name that
    /// differs from the expected name by exactly one character (catches digit-for-letter
    /// substitution like "svch0st", and ordinary single-letter typos like "lsasa"), or a name one
    /// character shorter/longer that's a single insertion/deletion away (catches a dropped or
    /// doubled letter, and incidentally most adjacent-character-swap typos too, e.g. "csrsss").
    /// Never matches the expected name itself - that's the exact-match branch above, not a
    /// near-miss.</summary>
    private static bool IsNearMissTypo(string candidate, string expected)
    {
        if (candidate.Length == 0) return false;
        if (candidate.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;

        if (candidate.Length == expected.Length)
        {
            int diffs = 0;
            for (int i = 0; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(expected[i]))
                    diffs++;
                if (diffs > 1) break;
            }
            return diffs == 1;
        }

        if (Math.Abs(candidate.Length - expected.Length) == 1)
            return IsSingleInsertDeleteAway(candidate, expected);

        return false;
    }

    private static bool IsSingleInsertDeleteAway(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a); // a is now the shorter string
        int i = 0, j = 0;
        bool usedSkip = false;
        while (i < a.Length && j < b.Length)
        {
            if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j]))
            {
                i++;
                j++;
            }
            else
            {
                if (usedSkip) return false;
                usedSkip = true;
                j++;
            }
        }
        return true;
    }
}
