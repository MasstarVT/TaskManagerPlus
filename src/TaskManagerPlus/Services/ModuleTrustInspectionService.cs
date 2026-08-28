using System.Diagnostics;
using System.IO;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #849/#850: extends the existing per-process "View modules" feature (previously a plain
/// ModuleName/FileName string list - see the original LoadSelectedProcessModules in
/// ProcessesViewModel) with trust columns:
///
/// #849 - signature status + publisher (both via SignatureCheckService, the same cached WinVerifyTrust
/// check every other tab in this app already uses) and a "loaded from a user-writable location" flag
/// (WritablePathHeuristics). This is a per-process view, same scope as the original modules list -
/// "sortable so 'show me every unsigned DLL loaded anywhere on this machine' is one click" is
/// satisfied per-process here; a machine-wide aggregator across every running process's modules is
/// explicitly out of scope for this item.
///
/// #850 - DLL side-loading: flags a loaded module whose file name matches a curated list of
/// well-known side-loading targets (version.dll, dbghelp.dll, winmm.dll, dwmapi.dll, uxtheme.dll,
/// wtsapi32.dll, and a few similarly-storied others) AND a same-named file exists directly under
/// System32 AND this module was actually loaded from somewhere else - the classic "app ships/expects
/// its own copy of a well-known system DLL name in its own directory, and a real System32 copy also
/// exists, so which one loads depends on search-order tricks an attacker can exploit" pattern. Also
/// separately flags when the process's own application directory is itself user-writable (a much
/// broader, cheaper-to-check version of the same "something could plant a file here" concern).
///
/// Iterating Process.Modules is itself cheap (same as the original feature), but checking every
/// module's signature is not free (each is a WinVerifyTrust chain call, cached per path but still a
/// real disk/crypto operation on a first-time miss) - a process with 100+ modules could take a
/// noticeable moment, so this is called from the ViewModel via Task.Run rather than synchronously
/// like the original single-string-list version was.
/// </summary>
public static class ModuleTrustInspectionService
{
    /// <summary>A reasonable curated list of well-known DLL side-loading targets - doesn't need to be
    /// exhaustive (see #850's own text), just names that are both commonly present in System32 and
    /// commonly missing from an application's own import-resolution allowlist thinking.</summary>
    private static readonly HashSet<string> SideLoadWatchlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "dbghelp.dll", "winmm.dll", "dwmapi.dll", "uxtheme.dll", "wtsapi32.dll",
        "propsys.dll", "dwrite.dll", "cryptbase.dll", "profapi.dll", "userenv.dll", "ntmarta.dll",
        "wininet.dll", "winspool.drv", "shfolder.dll", "secur32.dll",
    };

    public sealed record ModuleInspectionResult(
        List<ProcessModuleInfo> Modules,
        bool AppDirectoryIsUserWritable,
        string? ApplicationDirectory,
        string? Error);

    public static ModuleInspectionResult Inspect(int pid)
    {
        var results = new List<ProcessModuleInfo>();
        string system32;
        try { system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"); }
        catch { system32 = string.Empty; }

        var writableRoots = WritablePathHeuristics.GetUserWritableRoots();

        try
        {
            using var proc = Process.GetProcessById(pid);

            foreach (ProcessModule module in proc.Modules)
            {
                string moduleName = module.ModuleName ?? "(unknown)";
                string? path = null;
                try { path = module.FileName; } catch { /* a module this app can't resolve a path for - leave null, degrade below */ }

                string sigStatus = SignatureCheckService.GetStatus(path);
                var signer = SignatureCheckService.GetSignerInfo(path);
                string publisher = signer.SubjectCn ?? signer.IssuerCn ?? "Unknown";
                bool userWritable = WritablePathHeuristics.IsUnderUserWritableRoot(path, writableRoots);

                bool sideLoadSuspect = false;
                string? system32Counterpart = null;
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(system32) && SideLoadWatchlist.Contains(moduleName))
                {
                    string candidate = Path.Combine(system32, moduleName);
                    bool existsInSystem32 = false;
                    try { existsInSystem32 = File.Exists(candidate); } catch { /* treat as "doesn't exist" - no finding */ }

                    bool loadedFromSystem32 = path.StartsWith(system32, StringComparison.OrdinalIgnoreCase);
                    if (existsInSystem32 && !loadedFromSystem32)
                    {
                        sideLoadSuspect = true;
                        system32Counterpart = candidate;
                    }
                }

                results.Add(new ProcessModuleInfo
                {
                    ModuleName = moduleName,
                    FilePath = path ?? "(unknown)",
                    SignatureStatus = sigStatus,
                    Publisher = publisher,
                    IsUserWritableLocation = userWritable,
                    IsSideLoadSuspect = sideLoadSuspect,
                    System32CounterpartPath = system32Counterpart,
                });
            }

            string? appDir = null;
            bool appDirWritable = false;
            try
            {
                string? mainModulePath = proc.MainModule?.FileName;
                appDir = string.IsNullOrEmpty(mainModulePath) ? null : Path.GetDirectoryName(mainModulePath);
                appDirWritable = WritablePathHeuristics.IsUnderUserWritableRoot(appDir, writableRoots);
            }
            catch { /* leave appDir null / not flagged */ }

            return new ModuleInspectionResult(results, appDirWritable, appDir, null);
        }
        catch (Exception ex)
        {
            return new ModuleInspectionResult(results, false, null, $"couldn't read modules: {ex.Message}");
        }
    }
}
