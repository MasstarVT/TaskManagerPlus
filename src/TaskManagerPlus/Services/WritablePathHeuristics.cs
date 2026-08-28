namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #849/#850: shared "is this path inside a location an ordinary user can typically write
/// to" check - the same simple path-prefix heuristic AutorunsService.BuildUserWritableRoots already
/// uses for its svchost-service-DLL and per-user-COM-object findings (not a full ACL check - see that
/// class's remarks on why a prefix match is used instead), pulled out here as its own small static
/// helper rather than reaching into AutorunsService's private state. Also includes %ProgramData%,
/// which #849's item text calls out explicitly but AutorunsService's own list omits (none of its
/// existing findings needed it).
/// </summary>
public static class WritablePathHeuristics
{
    public static string[] GetUserWritableRoots()
    {
        var roots = new List<string>();
        try { roots.Add(Environment.GetEnvironmentVariable("TEMP") ?? string.Empty); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); } catch { }
        try { roots.Add(Environment.ExpandEnvironmentVariables("%Public%")); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); } catch { } // %ProgramData%
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsUnderUserWritableRoot(string? path, string[]? roots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        roots ??= GetUserWritableRoots();
        return roots.Any(root => !string.IsNullOrWhiteSpace(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
