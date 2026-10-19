using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 14, items 23/24: detects an installed debugger (cdb.exe/windbg.exe) and, when cdb.exe
/// is present, runs `cdb -z &lt;dump&gt; -c "!analyze -v; q"` as an explicit, button-gated
/// background task with a hard timeout, then parses the handful of fields this app cares about
/// out of its text output. Per CLAUDE.md's "prefer a known Windows tool over raw interop" -
/// !analyze -v is the real Microsoft-shipped crash analysis, far more capable than this app's
/// own header parsing/address-range blame (MinidumpParserService), used only on demand since it
/// needs symbols and can genuinely run long or hang on a bad network path.
/// </summary>
public static class DebuggerToolsService
{
    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Item 23: probes the usual Debugging Tools for Windows install locations - the
    /// traditional Windows Kits SDK feature (cdb.exe/windbg.exe under Debuggers\x64) and the
    /// Microsoft Store WinDbg package. The Store app is resolved through the package manager to
    /// its admin-owned install folder (Program Files\WindowsApps), never through the per-user
    /// %LocalAppData%\Microsoft\WindowsApps alias folder: that folder is writable by the user, and
    /// whatever is found here is started from this elevated process.</summary>
    public static DebuggerAvailability DetectDebugger()
    {
        string? cdb = null;
        string? windbg = null;

        try
        {
            string[] kitsRoots =
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Windows Kits\10\Debuggers\x64"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Windows Kits\10\Debuggers\x64"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Windows Kits\10\Debuggers\x86"),
            };
            foreach (var root in kitsRoots)
            {
                try
                {
                    var cdbPath = Path.Combine(root, "cdb.exe");
                    var windbgPath = Path.Combine(root, "windbg.exe");
                    if (cdb is null && File.Exists(cdbPath)) cdb = cdbPath;
                    if (windbg is null && File.Exists(windbgPath)) windbg = windbgPath;
                }
                catch { /* this candidate root just doesn't exist/isn't accessible */ }
            }
        }
        catch { /* best-effort */ }

        windbg ??= FindStoreWinDbg();

        return new DebuggerAvailability { CdbPath = cdb, WindbgPath = windbg };
    }

    // Microsoft's Store publisher id; the family name pins both the package name and publisher.
    private const string StoreWinDbgFamilyName = "Microsoft.WinDbg_8wekyb3d8bbwe";

    /// <summary>The Store WinDbg's executable inside its install folder, or null. Only a
    /// Store-signed, non-development package of Microsoft's publisher counts - a sideloaded
    /// package can carry the same name. The exe name comes from the package's own manifest.</summary>
    private static string? FindStoreWinDbg()
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty, StoreWinDbgFamilyName))
            {
                if (package.SignatureKind != global::Windows.ApplicationModel.PackageSignatureKind.Store || package.IsDevelopmentMode) continue;
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.InstalledLocation.Path));
                if (ReadManifestExecutable(root) is { } exe) return exe;
            }
        }
        catch { /* package API unavailable / WinDbg not installed - no Store debugger */ }
        return null;
    }

    private static string? ReadManifestExecutable(string root)
    {
        try
        {
            var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
            var executables = manifest.Descendants()
                .Where(e => e.Name.LocalName == "Application")
                .Select(e => (string?)e.Attribute("Executable"))
                .Where(x => !string.IsNullOrWhiteSpace(x) && x!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var relative in executables.OrderByDescending(x => x!.Contains("DbgX", StringComparison.OrdinalIgnoreCase)))
            {
                string full = Path.GetFullPath(Path.Combine(root, relative!));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    return full;
            }
        }
        catch { /* unreadable manifest - treat as not installed */ }
        return null;
    }

    /// <summary>Item 23: "Open in WinDbg" - starts windbg (preferred, has a UI) or falls back to
    /// cdb.exe in a console of its own, through SafeLaunchService.StartTrustedExecutable (refused
    /// unless the exe and its folders are admin-only). The debugger gets this app's validated
    /// symbol path rather than the inherited _NT_SYMBOL_PATH, which any process running as the
    /// user can set. Null on success, otherwise why it didn't start.</summary>
    public static string? TryOpenInWinDbg(DebuggerAvailability availability, string dumpPath, string? symbolCacheFolder)
    {
        string? exe = availability.WindbgPath ?? availability.CdbPath;
        if (exe is null) return "no debugger was found";
        string symbols = SymbolArguments(exe, symbolCacheFolder);
        return SafeLaunchService.StartTrustedExecutable(exe, $"{symbols} -z \"{dumpPath}\"", stripDebuggerEnvironment: true);
    }

    /// <summary>`-y "srv*&lt;cache&gt;*https://msdl..."` from the validated cache folder (the
    /// default when it isn't valid), plus `-sins` - ignore the symbol/image path environment
    /// variables - for the Debugging Tools' cdb/windbg. The Store WinDbg gets only -y.</summary>
    private static string SymbolArguments(string exe, string? symbolCacheFolder)
    {
        string path = SymbolServerService.BuildSymbolPath(symbolCacheFolder)
            ?? string.Format(SymbolServerService.SuggestedPathTemplate, CrashAnalysisSettings.Defaults.SymbolCacheFolder);
        string name = Path.GetFileName(exe);
        bool classic = name.Equals("cdb.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("windbg.exe", StringComparison.OrdinalIgnoreCase);
        return classic ? $"-sins -y \"{path}\"" : $"-y \"{path}\"";
    }

    /// <summary>
    /// Item 24: `cdb -z &lt;dump&gt; -c "!analyze -v; q"` with a hard timeout - killed outright
    /// if it runs past AnalyzeTimeout (an unreachable/misconfigured symbol path can otherwise
    /// hang this for a very long time; see SymbolServerService's reachability check, item 25).
    /// Parses MODULE_NAME/IMAGE_NAME/FAILURE_BUCKET_ID/PROCESS_NAME as single-line "KEY: value"
    /// fields, and STACK_TEXT as the block of lines between its own header and the next all-caps
    /// "SOMETHING:" section header - the same shape !analyze -v's own output uses throughout.
    /// </summary>
    public static async Task<CdbAnalysisResult> RunAnalyzeAsync(string cdbPath, string dumpPath, string? symbolCacheFolder, CancellationToken cancellationToken = default)
    {
        try
        {
            // cdb runs elevated. Its symbol path must not come from the inherited environment:
            // _NT_SYMBOL_PATH & co. are per-user variables any process running as the user can
            // set, and a symsrv*<dll>* entry makes dbghelp load that DLL. So: -sins (ignore them),
            // an explicit -y from the validated cache folder, and the _NT_* variables stripped.
            var psi = new ProcessStartInfo(cdbPath, $"{SymbolArguments(cdbPath, symbolCacheFolder)} -z \"{dumpPath}\" -c \"!analyze -v; q\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var name in psi.Environment.Keys
                .Where(k => k.StartsWith("_NT_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DBGHELP_", StringComparison.OrdinalIgnoreCase))
                .ToList())
                psi.Environment.Remove(name);
            using var proc = Process.Start(psi);
            if (proc is null)
                return new CdbAnalysisResult { AnalyzedAt = DateTime.Now, Error = "Couldn't start cdb.exe." };

            var outputTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(AnalyzeTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new CdbAnalysisResult
                {
                    AnalyzedAt = DateTime.Now,
                    Error = $"cdb.exe timed out after {AnalyzeTimeout.TotalSeconds:0}s (often a slow/unreachable symbol server - check the symbol settings)."
                };
            }

            string output = (await outputTask) + Environment.NewLine + (await errorTask);
            return ParseAnalyzeOutput(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CdbAnalysisResult { AnalyzedAt = DateTime.Now, Error = $"cdb.exe failed: {ex.Message}" };
        }
    }

    private static CdbAnalysisResult ParseAnalyzeOutput(string output)
    {
        string? Field(string key)
        {
            var m = Regex.Match(output, $@"^{key}:\s*(.+?)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        string? stackText = null;
        var stackMatch = Regex.Match(output, @"^STACK_TEXT:\s*\r?\n(.*?)(?=\r?\n[A-Z][A-Z0-9_]*:\s*\r?\n|\z)", RegexOptions.Singleline | RegexOptions.Multiline);
        if (stackMatch.Success) stackText = stackMatch.Groups[1].Value.Trim();

        return new CdbAnalysisResult
        {
            AnalyzedAt = DateTime.Now,
            ModuleName = Field("MODULE_NAME"),
            ImageName = Field("IMAGE_NAME"),
            FailureBucketId = Field("FAILURE_BUCKET_ID"),
            ProcessName = Field("PROCESS_NAME"),
            StackText = stackText,
            RawOutput = output,
        };
    }
}
