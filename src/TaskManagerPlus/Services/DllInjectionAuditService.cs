using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #746: global DLL injection audit - the handful of registry locations that load a DLL into
/// every (or nearly every) process on the system, rather than into one specific app the way a
/// Run-key/Startup-folder entry does:
///
/// - AppInit_DLLs (+ LoadAppInit_DLLs) under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\
///   Windows loads into every process that links user32.dll - read from both the 64-bit and
///   32-bit registry views (RegistryView.Registry64/Registry32), since they're independent lists
///   for independent process bitness, not a single value with a Wow6432Node mirror.
/// - AppCertDlls under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager loads into every
///   process on every CreateProcess call - normally empty on a clean system, so any entry here is
///   flagged unconditionally.
/// - Security Packages / Authentication Packages under HKLM\SYSTEM\CurrentControlSet\Control\Lsa
///   load into lsass.exe.
/// - KnownDLLs under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager is a different kind of
///   check: real per-version "which DLL names does this Windows build protect" lists aren't
///   published anywhere this app could read reliably, so rather than diff against a hardcoded
///   list this app has no way to keep correct across Windows versions/editions, this instead
///   checks the one thing that's always true regardless of version: every KnownDLLs value should
///   be a bare filename (Windows resolves it into System32 itself, and the \KnownDlls kernel
///   object namespace protects the actual mapping at runtime). A value that instead carries a
///   path is not the normal shape and is flagged.
///
/// Quick flag, not a verdict throughout (see CLAUDE.md's cross-cutting conventions) - AppInit_DLLs
/// and the LSA package lists have real legitimate uses (accessibility tools, third-party
/// credential/SSP providers); each entry's signature status (via SignatureCheckService) is shown
/// alongside the flag rather than as a separate verdict of its own.
/// </summary>
public static class DllInjectionAuditService
{
    private const string WindowsSubPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string AppCertDllsPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";
    private const string LsaPath = @"SYSTEM\CurrentControlSet\Control\Lsa";
    private const string KnownDllsPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs";

    public static DllInjectionAuditResult Read()
    {
        var entries = new List<DllInjectionEntry>();

        var (enabled64, dlls64) = ReadAppInit(RegistryView.Registry64);
        foreach (var path in SplitDllList(dlls64))
            entries.Add(BuildEntry("AppInit_DLLs (64-bit)", Path.GetFileName(path), path, alwaysFlagged: enabled64,
                note: enabled64 ? "LoadAppInit_DLLs is on - this DLL loads into every 64-bit process that links user32.dll." : "Listed but LoadAppInit_DLLs is off, so this DLL is not currently being loaded."));

        var (enabled32, dlls32) = ReadAppInit(RegistryView.Registry32);
        foreach (var path in SplitDllList(dlls32))
            entries.Add(BuildEntry("AppInit_DLLs (32-bit)", Path.GetFileName(path), path, alwaysFlagged: enabled32,
                note: enabled32 ? "LoadAppInit_DLLs is on - this DLL loads into every 32-bit process that links user32.dll." : "Listed but LoadAppInit_DLLs is off, so this DLL is not currently being loaded."));

        foreach (var (name, path) in ReadAppCertDlls())
            entries.Add(BuildEntry("AppCertDlls", name, path, alwaysFlagged: true,
                note: "AppCertDlls is normally empty - every entry here loads into every process on every CreateProcess call."));

        foreach (var name in ReadLsaMultiString("Security Packages"))
            entries.Add(BuildEntry("Security Package (LSA)", name, ResolvePackagePath(name), alwaysFlagged: false, note: "Loads into lsass.exe."));

        foreach (var name in ReadLsaMultiString("Authentication Packages"))
            entries.Add(BuildEntry("Authentication Package (LSA)", name, ResolvePackagePath(name), alwaysFlagged: false, note: "Loads into lsass.exe."));

        foreach (var (name, data) in ReadKnownDlls())
        {
            bool looksBare = !data.Contains('\\') && !data.Contains('/');
            if (looksBare) continue; // the expected shape - a bare filename Windows resolves into System32 itself, not an anomaly

            entries.Add(BuildEntry("KnownDLLs (unexpected entry)", name, Environment.ExpandEnvironmentVariables(data), alwaysFlagged: true,
                note: "A KnownDLLs entry should be a bare filename, not a path - this one isn't, which is not the normal shape."));
        }

        return new DllInjectionAuditResult
        {
            AppInitEnabled64 = enabled64,
            AppInitDlls64 = dlls64,
            AppInitEnabled32 = enabled32,
            AppInitDlls32 = dlls32,
            Entries = entries.OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static (bool Enabled, string Dlls) ReadAppInit(RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(WindowsSubPath);
            if (key is null) return (false, string.Empty);

            var dlls = key.GetValue("AppInit_DLLs") as string ?? string.Empty;
            bool enabled = key.GetValue("LoadAppInit_DLLs") is int flag && flag != 0;
            return (enabled, dlls);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>AppInit_DLLs is documented as space-separated, though some tooling/guidance treats
    /// it as comma-separated too - split on either so a comma-separated list (or a quoted path
    /// containing neither) still parses.</summary>
    private static IEnumerable<string> SplitDllList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim('"');
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private static List<(string Name, string Path)> ReadAppCertDlls()
    {
        var result = new List<(string, string)>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppCertDllsPath);
            if (key is null) return result;

            foreach (var name in key.GetValueNames())
            {
                var path = key.GetValue(name) as string ?? string.Empty;
                if (path.Length > 0) result.Add((name, path));
            }
        }
        catch { /* key inaccessible - degrade to no rows */ }
        return result;
    }

    private static string[] ReadLsaMultiString(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LsaPath);
            if (key?.GetValue(valueName) is string[] arr)
                return arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        }
        catch { /* key inaccessible - degrade to none */ }
        return Array.Empty<string>();
    }

    /// <summary>LSA package values are usually a bare package name ("kerberos", "msv1_0", ...)
    /// that Windows itself resolves into a System32 DLL, rather than a path - resolve the same way
    /// for the signature check below. A value that already looks like a path is used as-is.</summary>
    private static string ResolvePackagePath(string name)
    {
        if (name.Contains('\\') || name.Contains('/')) return Environment.ExpandEnvironmentVariables(name);

        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        return Path.Combine(sys32, fileName);
    }

    private static List<(string Name, string Data)> ReadKnownDlls()
    {
        var result = new List<(string, string)>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KnownDllsPath);
            if (key is null) return result;

            foreach (var name in key.GetValueNames())
            {
                // DllDirectory/DllDirectory32 hold a search directory by design, not a DLL name -
                // not a per-DLL entry, so they're not part of this list at all.
                if (name.Equals("DllDirectory", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DllDirectory32", StringComparison.OrdinalIgnoreCase)) continue;

                var data = key.GetValue(name) as string ?? string.Empty;
                if (data.Length > 0) result.Add((name, data));
            }
        }
        catch { /* key inaccessible - degrade to no rows */ }
        return result;
    }

    private static DllInjectionEntry BuildEntry(string category, string name, string resolvedPath, bool alwaysFlagged, string note)
    {
        string sig = resolvedPath.Length > 0 && File.Exists(resolvedPath) ? SignatureCheckService.GetStatus(resolvedPath) : "Unknown";
        bool flagged = alwaysFlagged || sig == "Unsigned";
        return new DllInjectionEntry
        {
            Category = category,
            Name = name,
            ResolvedPath = resolvedPath,
            SignatureStatus = sig,
            IsFlagged = flagged,
            Note = note,
        };
    }
}
