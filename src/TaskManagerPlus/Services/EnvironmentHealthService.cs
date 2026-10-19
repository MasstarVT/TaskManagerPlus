using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #797-798: the Windows Health tab's "Environment" card - a PATH doctor that flags common
/// machine/user PATH problems (#797) and a full environment-variable inspector/editor with
/// sanity checks and a WM_SETTINGCHANGE broadcast after any edit (#798). Both read the two
/// registry locations Windows itself treats as the source of truth for a *new* process's
/// environment - HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment (machine scope)
/// and HKCU\Environment (user scope) - not this already-running process's own (possibly stale)
/// Environment.GetEnvironmentVariable copy, which is exactly the gap #799's drift check is about.
/// </summary>
public static class EnvironmentHealthService
{
    private const string MachineEnvKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserEnvKeyPath = @"Environment";

    #region #797 - PATH doctor

    public static PathDoctorResult ReadPathDoctorResult()
    {
        var (machineRaw, machineIsExpand, _) = ReadRawValue(Registry.LocalMachine, MachineEnvKeyPath, "Path");
        var (userRaw, userIsExpand, _) = ReadRawValue(Registry.CurrentUser, UserEnvKeyPath, "Path");

        var issues = new List<PathIssue>();
        var machineSegments = SplitSegments(machineRaw);
        var userSegments = SplitSegments(userRaw);
        var vars = ReadFreshVariables();

        AnalyzeScope("Machine", machineRaw, machineIsExpand, machineSegments, issues, vars);
        AnalyzeScope("User", userRaw, userIsExpand, userSegments, issues, vars);

        // Cross-scope duplicates: the same directory listed in both Machine and User PATH is
        // harmless but pointless - it's on the effective PATH twice either way.
        var machineExpanded = machineSegments.Select(s => Expand(s, vars).TrimEnd('\\').ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet();
        foreach (var seg in userSegments)
        {
            string expanded = Expand(seg, vars).TrimEnd('\\').ToLowerInvariant();
            if (expanded.Length > 0 && machineExpanded.Contains(expanded))
                issues.Add(new PathIssue { Scope = "User", Segment = seg, IssueType = "Duplicated across scopes", Detail = "This directory is already present in the Machine PATH." });
        }

        string combinedExpanded = Expand(machineRaw, vars) + ";" + Expand(userRaw, vars);
        int totalLength = combinedExpanded.Length;
        if (totalLength > PathDoctorResult.MaxExpandedLength)
            issues.Add(new PathIssue { Scope = "Both", Segment = "(combined)", IssueType = "Length limit exceeded", Detail = $"The combined, fully expanded Machine+User PATH is {totalLength:N0} characters - over the practical {PathDoctorResult.MaxExpandedLength:N0}-character CreateProcess environment-block limit. New processes may silently see a truncated PATH." });

        return new PathDoctorResult
        {
            MachinePathRaw = machineRaw,
            UserPathRaw = userRaw,
            MachineIsExpandSz = machineIsExpand,
            UserIsExpandSz = userIsExpand,
            TotalExpandedLength = totalLength,
            Issues = issues,
        };
    }

    private static void AnalyzeScope(string scope, string raw, bool isExpandSz, List<string> segments, List<PathIssue> issues,
        IReadOnlyDictionary<string, string> vars)
    {
        if (raw.Length == 0) return;

        if (raw.StartsWith(';') || raw.EndsWith(';') || raw.Contains(";;"))
            issues.Add(new PathIssue { Scope = scope, Segment = "(whole value)", IssueType = "Empty / trailing semicolon segment", Detail = "One or more segments between semicolons is empty - a process reading PATH may treat an empty segment as \"the current directory\"." });

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            string trimmed = seg.Trim();
            if (trimmed.Length == 0) continue;

            seen[trimmed] = seen.GetValueOrDefault(trimmed) + 1;

            if (trimmed.Contains('%'))
            {
                string expanded = Expand(trimmed, vars);
                if (expanded.Contains('%'))
                    issues.Add(new PathIssue { Scope = scope, Segment = trimmed, IssueType = "Unexpanded %VAR%", Detail = $"\"{trimmed}\" contains a %VARIABLE% reference that doesn't resolve to anything - the referenced variable isn't set." });
                else if (!isExpandSz)
                    issues.Add(new PathIssue { Scope = scope, Segment = trimmed, IssueType = "REG_SZ needs REG_EXPAND_SZ", Detail = $"This PATH value is stored as REG_SZ but contains \"%...%\" references - it should be REG_EXPAND_SZ, or the literal text \"{trimmed}\" is used as-is instead of being expanded." });
            }

            string expandedPath = Expand(trimmed, vars);
            if (!expandedPath.Contains('%') && !Directory.Exists(expandedPath))
                issues.Add(new PathIssue { Scope = scope, Segment = trimmed, IssueType = "Directory doesn't exist", Detail = $"\"{expandedPath}\" doesn't exist - likely a leftover from an uninstalled program." });
        }

        foreach (var (seg, count) in seen)
        {
            if (count > 1)
                issues.Add(new PathIssue { Scope = scope, Segment = seg, IssueType = "Exact duplicate", Detail = $"Appears {count} times in the {scope} PATH." });
        }
    }

    private static List<string> SplitSegments(string raw) =>
        raw.Length == 0 ? new List<string>() : raw.Split(';').ToList();

    // Per-session variables Windows sets for a new process without storing them in either
    // Environment key - taken from this process (ProcessEnvironmentHardening resets the system-folder
    // ones to their real values) and from HKCU\Volatile Environment.
    private static readonly string[] SessionVariableNames =
    {
        "SystemRoot", "SystemDrive", "windir", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ProgramData",
        "ALLUSERSPROFILE", "PUBLIC", "COMPUTERNAME", "USERPROFILE", "USERNAME", "USERDOMAIN",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
    };

    private static readonly Regex VariableReferenceRegex = new(@"%([^%;\r\n]+)%", RegexOptions.Compiled);

    /// <summary>The variables a process started now would see: machine then user values freshly
    /// read from the registry (user wins), over the per-session ones above. Not this process's own
    /// environment - it's stale for anything set after the app started, and ProcessEnvironmentHardening
    /// rewrites parts of it (TEMP/TMP point at an admin-only folder), so expanding against it would
    /// misread a new %VAR% entry and the user's real TEMP.</summary>
    private static Dictionary<string, string> ReadFreshVariables()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SessionVariableNames)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) map[name] = value;
        AddRegistryVariables(map, Registry.LocalMachine, MachineEnvKeyPath);
        AddRegistryVariables(map, Registry.CurrentUser, UserEnvKeyPath);
        AddRegistryVariables(map, Registry.CurrentUser, "Volatile Environment");
        // Stored values are unexpanded (REG_EXPAND_SZ) - resolve them against each other.
        foreach (var name in map.Keys.ToList()) map[name] = Expand(map[name], map);
        return map;
    }

    private static void AddRegistryVariables(Dictionary<string, string> into, RegistryKey hive, string subKeyPath)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyPath);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0 || name.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue; // PATH is analyzed, not referenced
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value) into[name] = value;
            }
        }
        catch { /* access denied - that scope contributes nothing */ }
    }

    /// <summary>%VAR% expansion against <paramref name="vars"/>; an unknown reference is left as-is,
    /// like ExpandEnvironmentStrings. A few passes resolve nested references (TEMP -> %USERPROFILE%).</summary>
    private static string Expand(string s, IReadOnlyDictionary<string, string> vars)
    {
        for (int pass = 0; pass < 8 && s.Contains('%'); pass++)
        {
            string next = VariableReferenceRegex.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            if (next == s) break;
            s = next;
        }
        return s;
    }

    #endregion

    #region #798 - Environment variable inspector/editor

    public static List<EnvironmentVariableEntry> ReadAllVariables()
    {
        var result = new List<EnvironmentVariableEntry>();
        AppendScope(result, "Machine", Registry.LocalMachine, MachineEnvKeyPath);
        AppendScope(result, "User", Registry.CurrentUser, UserEnvKeyPath);
        return result.OrderBy(e => e.Scope).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AppendScope(List<EnvironmentVariableEntry> into, string scope, RegistryKey hive, string subKeyPath)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyPath);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                var (raw, isExpand, _) = ReadRawValue(hive, subKeyPath, name);
                into.Add(new EnvironmentVariableEntry { Scope = scope, Name = name, Value = raw, IsExpandString = isExpand });
            }
        }
        catch { /* access denied - degrade to "nothing from this scope" */ }
    }

    /// <summary>#798: add or edit one variable - captures the old value first (for #796's journal
    /// and for the confirmation dialog's "exact effect" text), writes as REG_EXPAND_SZ when the new
    /// value itself contains a %VAR% reference (matching what `setx`/System Properties' own editor
    /// does), REG_SZ otherwise. Broadcasts WM_SETTINGCHANGE afterward so already-running processes
    /// that listen for it (Explorer chief among them) pick it up without a logoff.</summary>
    public static (bool Success, string? Error) SetVariable(string scope, string name, string value)
    {
        try
        {
            var (hive, subKeyPath, hiveName) = ResolveScope(scope);
            using var key = hive.CreateSubKey(subKeyPath, writable: true);
            if (key is null) return (false, "Couldn't open the environment registry key (needs Administrator for Machine scope).");

            var (oldRaw, oldIsExpand, existedBefore) = ReadRawValue(hive, subKeyPath, name);
            var kind = value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
            key.SetValue(name, value, kind);

            // Both kinds are journaled: the new value's kind (used by the entry's .reg export) and
            // the old value's (used by Undo), so undoing an edit of a REG_EXPAND_SZ value (PATH,
            // TEMP, ...) restores its %VAR% text as REG_EXPAND_SZ even when the new value was
            // written as REG_SZ.
            RegistryChangeJournalService.Record("Environment", $"Set {scope} variable \"{name}\"",
                hiveName, subKeyPath, name, kind, existedBefore ? oldRaw : null, value,
                oldKind: existedBefore ? (oldIsExpand ? RegistryValueKind.ExpandString : RegistryValueKind.String) : null);

            BroadcastEnvironmentChange();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string? Error) DeleteVariable(string scope, string name)
    {
        try
        {
            var (hive, subKeyPath, hiveName) = ResolveScope(scope);
            using var key = hive.OpenSubKey(subKeyPath, writable: true);
            if (key is null) return (false, "Couldn't open the environment registry key.");

            var (oldRaw, kindKnown, existedBefore) = ReadRawValue(hive, subKeyPath, name);
            var kind = kindKnown ? RegistryValueKind.ExpandString : RegistryValueKind.String;
            key.DeleteValue(name, throwOnMissingValue: false);

            RegistryChangeJournalService.Record("Environment", $"Deleted {scope} variable \"{name}\"",
                hiveName, subKeyPath, name, kind, existedBefore ? oldRaw : null, null);

            BroadcastEnvironmentChange();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (RegistryKey Hive, string SubKeyPath, string HiveName) ResolveScope(string scope) =>
        scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
            ? (Registry.LocalMachine, MachineEnvKeyPath, "HKLM")
            : (Registry.CurrentUser, UserEnvKeyPath, "HKCU");

    private static (string Raw, bool IsExpandString, bool Found) ReadRawValue(RegistryKey hive, string subKeyPath, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyPath);
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return (string.Empty, false, false);
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
            return (raw, kind == RegistryValueKind.ExpandString, true);
        }
        catch
        {
            return (string.Empty, false, false);
        }
    }

    #region #798 - WM_SETTINGCHANGE broadcast

    private const int WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const int SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);

    /// <summary>Broadcasts WM_SETTINGCHANGE with lParam "Environment" - the documented notification
    /// Explorer/System Properties themselves send after an environment-variable edit, so already-
    /// running listeners (Explorer's own environment cache, other apps that handle this message)
    /// pick up the change without the user needing to log off. Does NOT reach an already-running
    /// process's own inherited environment block - see #799's drift check for that gap.</summary>
    private static void BroadcastEnvironmentChange()
    {
        try { SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 5000, out _); }
        catch { /* best-effort notification - the registry write itself already succeeded */ }
    }

    #endregion

    #region #798 - Sanity checks

    /// <summary>#798: TEMP/TMP/ComSpec/windir/SystemRoot/PATHEXT/NUMBER_OF_PROCESSORS - the small
    /// set of variables Windows itself and most native tooling assume are always sane.
    /// NUMBER_OF_PROCESSORS is compared against Environment.ProcessorCount, the same logical-
    /// processor-count source CpuTopologyService/HardwareMonitorService already use for the CPU
    /// tab, so a mismatch here really does mean "this session's copy is stale relative to what
    /// Windows reports right now" rather than two independently-guessed numbers disagreeing.</summary>
    public static List<EnvironmentSanityCheck> RunSanityChecks(List<EnvironmentVariableEntry> variables)
    {
        var checks = new List<EnvironmentSanityCheck>();
        string? Find(string scope, string name) => variables.FirstOrDefault(v => v.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        var vars = ReadFreshVariables();

        CheckTempLike(checks, "TEMP", variables, vars);
        CheckTempLike(checks, "TMP", variables, vars);

        string? comSpec = Find("Machine", "ComSpec") ?? Find("User", "ComSpec");
        string expandedComSpec = comSpec is null ? string.Empty : Expand(comSpec, vars);
        checks.Add(new EnvironmentSanityCheck
        {
            Title = "ComSpec",
            Passed = comSpec is not null && File.Exists(expandedComSpec),
            Detail = comSpec is null ? "ComSpec isn't set." : File.Exists(expandedComSpec) ? expandedComSpec : $"\"{expandedComSpec}\" doesn't exist.",
        });

        foreach (var name in new[] { "windir", "SystemRoot" })
        {
            // SystemRoot isn't stored in the Environment key - Windows sets it per process.
            string? raw = Find("Machine", name) ?? (name == "SystemRoot" ? vars.GetValueOrDefault(name) : null);
            string expanded = raw is null ? string.Empty : Expand(raw, vars);
            checks.Add(new EnvironmentSanityCheck
            {
                Title = name,
                Passed = raw is not null && Directory.Exists(expanded),
                Detail = raw is null ? $"{name} isn't set." : Directory.Exists(expanded) ? expanded : $"\"{expanded}\" doesn't exist.",
            });
        }

        string? pathExt = Find("Machine", "PATHEXT") ?? Find("User", "PATHEXT");
        bool hasExe = pathExt?.Split(';').Any(e => e.Trim().Equals(".EXE", StringComparison.OrdinalIgnoreCase)) ?? false;
        checks.Add(new EnvironmentSanityCheck
        {
            Title = "PATHEXT",
            Passed = hasExe,
            Detail = pathExt is null ? "PATHEXT isn't set - Windows falls back to a built-in default." : hasExe ? pathExt : $"\"{pathExt}\" is missing \".EXE\" - typing a bare command name for an .exe may stop working from a shell that relies on PATHEXT.",
        });

        string? procCountRaw = Find("Machine", "NUMBER_OF_PROCESSORS");
        int liveCount = Environment.ProcessorCount;
        bool procMatches = procCountRaw is not null && int.TryParse(procCountRaw, out int declared) && declared == liveCount;
        checks.Add(new EnvironmentSanityCheck
        {
            Title = "NUMBER_OF_PROCESSORS",
            Passed = procMatches,
            Detail = procCountRaw is null
                ? "NUMBER_OF_PROCESSORS isn't set."
                : procMatches
                    ? $"{procCountRaw} (matches the CPU tab's {liveCount} logical processors)"
                    : $"Registry says {procCountRaw}, but the CPU tab reports {liveCount} logical processors right now - this value is set once at boot and won't reflect a CPU swap or a BIOS core-count change until the next restart.",
        });

        return checks;
    }

    private static void CheckTempLike(List<EnvironmentSanityCheck> checks, string name, List<EnvironmentVariableEntry> variables,
        IReadOnlyDictionary<string, string> vars)
    {
        string? raw = variables.FirstOrDefault(v => v.Scope == "User" && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value
            ?? variables.FirstOrDefault(v => v.Scope == "Machine" && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        if (raw is null)
        {
            checks.Add(new EnvironmentSanityCheck { Title = name, Passed = false, Detail = $"{name} isn't set." });
            return;
        }

        string expanded = Expand(raw, vars);
        if (!Directory.Exists(expanded))
        {
            checks.Add(new EnvironmentSanityCheck { Title = name, Passed = false, Detail = $"\"{expanded}\" doesn't exist." });
            return;
        }

        bool? writable = CanStandardUserCreateFiles(expanded, out string? aclError);
        checks.Add(new EnvironmentSanityCheck
        {
            Title = name,
            Passed = writable,
            Detail = writable switch
            {
                true => expanded,
                false => $"\"{expanded}\" exists but its permissions don't let this account (without admin rights) create files in it.",
                null => $"\"{expanded}\" exists, but its permissions couldn't be read ({aclError}) - writability unknown.",
            },
        });
    }

    // BUILTIN\Administrators and "Local account and member of Administrators group" are deny-only
    // in the user's unelevated token, so they don't count toward what the user's own programs can do.
    private static readonly HashSet<string> ElevatedOnlyGroupSids = new(StringComparer.OrdinalIgnoreCase) { "S-1-5-32-544", "S-1-5-114" };

    // FILE_ADD_FILE (WriteData on a folder), plus the generic rights an ACE can carry unmapped.
    private const int CreateFilesMask = (int)FileSystemRights.CreateFiles | 0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;

    /// <summary>Whether the signed-in user's own (unelevated) programs can create files in
    /// <paramref name="directory"/>, judged from its DACL. This used to write and delete a probe
    /// file, but that ran with this process's admin token - it passed for an admin-only folder, and
    /// it was an elevated write into whatever folder HKCU\Environment (user-writable) named. Null
    /// when the ACL can't be read.</summary>
    private static bool? CanStandardUserCreateFiles(string directory, out string? error)
    {
        error = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (identity.User is { } user) sids.Add(user.Value);
            foreach (var group in identity.Groups ?? new IdentityReferenceCollection())
                if (!ElevatedOnlyGroupSids.Contains(group.Value)) sids.Add(group.Value);

            var rules = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            bool allowed = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // applies to children only
                if (((int)rule.FileSystemRights & CreateFilesMask) == 0) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || !sids.Contains(sid.Value)) continue;
                if (rule.AccessControlType == AccessControlType.Deny) return false; // a matching deny wins
                allowed = true;
            }
            return allowed;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    #endregion

    #endregion

    /// <summary>#799: the current, freshly-read (not this process's own cached copy) effective
    /// PATH/TEMP - machine value first, user value appended/preferred where Windows itself prefers
    /// the user value (TEMP/TMP). Shared by both the PATH doctor above and #799's process-drift
    /// scan so "what does 'current' mean" is answered in exactly one place. %VAR% references are
    /// expanded against the freshly read variables too (see ReadFreshVariables).</summary>
    public static (string EffectivePath, string EffectiveTemp) ReadEffectivePathAndTemp()
    {
        var vars = ReadFreshVariables();
        var (machinePath, _, _) = ReadRawValue(Registry.LocalMachine, MachineEnvKeyPath, "Path");
        var (userPath, _, _) = ReadRawValue(Registry.CurrentUser, UserEnvKeyPath, "Path");
        string effectivePath = Expand(
            userPath.Length == 0 ? machinePath : machinePath.TrimEnd(';') + ";" + userPath, vars);

        var (userTemp, _, _) = ReadRawValue(Registry.CurrentUser, UserEnvKeyPath, "TEMP");
        var (machineTemp, _, _) = ReadRawValue(Registry.LocalMachine, MachineEnvKeyPath, "TEMP");
        string rawTemp = userTemp.Length > 0 ? userTemp : machineTemp;
        string effectiveTemp = Expand(rawTemp, vars);

        return (effectivePath, effectiveTemp);
    }
}
