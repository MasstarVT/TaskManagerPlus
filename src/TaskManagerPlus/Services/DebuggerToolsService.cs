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
    /// traditional Windows Kits SDK feature (cdb.exe/windbg.exe under
    /// Debuggers\x64) and the Microsoft Store "WinDbg Preview" package (an MSIX app, so it lives
    /// under %LocalAppData%\Microsoft\WindowsApps rather than Program Files).</summary>
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

        if (windbg is null)
        {
            try
            {
                var appsRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var windowsApps = Path.Combine(appsRoot, "Microsoft", "WindowsApps");
                if (Directory.Exists(windowsApps))
                {
                    var direct = Directory.GetFiles(windowsApps, "WinDbgX.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    windbg = direct ?? Directory.GetDirectories(windowsApps, "Microsoft.WinDbg*")
                        .SelectMany(d =>
                        {
                            try { return Directory.GetFiles(d, "*.exe"); }
                            catch { return Array.Empty<string>(); }
                        })
                        .FirstOrDefault(f => Path.GetFileName(f).Contains("windbg", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { /* Store WinDbg not installed / folder not accessible */ }
        }

        return new DebuggerAvailability { CdbPath = cdb, WindbgPath = windbg };
    }

    /// <summary>Item 23: "Open in WinDbg" - launches windbg.exe (preferred, has a UI) or falls
    /// back to cdb.exe, UseShellExecute so it opens as a normal foreground app rather than
    /// attached to this process's own console (this app has none, being a WPF app).</summary>
    public static bool TryOpenInWinDbg(DebuggerAvailability availability, string dumpPath)
    {
        string? exe = availability.WindbgPath ?? availability.CdbPath;
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"-z \"{dumpPath}\"") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Item 24: `cdb -z &lt;dump&gt; -c "!analyze -v; q"` with a hard timeout - killed outright
    /// if it runs past AnalyzeTimeout (an unreachable/misconfigured symbol path can otherwise
    /// hang this for a very long time; see SymbolServerService's reachability check, item 25).
    /// Parses MODULE_NAME/IMAGE_NAME/FAILURE_BUCKET_ID/PROCESS_NAME as single-line "KEY: value"
    /// fields, and STACK_TEXT as the block of lines between its own header and the next all-caps
    /// "SOMETHING:" section header - the same shape !analyze -v's own output uses throughout.
    /// </summary>
    public static async Task<CdbAnalysisResult> RunAnalyzeAsync(string cdbPath, string dumpPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo(cdbPath, $"-z \"{dumpPath}\" -c \"!analyze -v; q\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
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
