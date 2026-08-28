using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #242: one-click full user-mode process dump for a hung window's owning process, ready to load
/// in WinDbg. CLAUDE.md prefers a known Windows tool/API over raw interop where one exists - for a
/// full-process minidump, that's `rundll32.exe comsvcs.dll,MiniDump &lt;pid&gt; &lt;path&gt; full`,
/// the same built-in DLL export (every Windows install's comsvcs.dll ships a MiniDump entry point)
/// that real Task Manager's own "Create dump file" action and many support scripts already shell
/// out to, rather than a fresh MiniDumpWriteDump P/Invoke with its own SeDebugPrivilege/
/// PROCESS_ALL_ACCESS handle plumbing. Note: despite its name, Services/CliDumpService.cs is
/// actually the `--dump-json` CLI flag's JSON metrics-snapshot writer (see its own remarks) - there
/// is no existing MiniDumpWriteDump plumbing anywhere in this codebase to reuse, so this is new.
/// </summary>
public static class ProcessDumpService
{
    public static async Task<(bool Success, string Message)> CreateDumpAsync(int pid, string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string comsvcs = Path.Combine(Environment.SystemDirectory, "comsvcs.dll");
            if (!File.Exists(comsvcs))
                return (false, "comsvcs.dll wasn't found on this system - can't create a dump this way.");

            var psi = new ProcessStartInfo("rundll32.exe", $"\"{comsvcs}\", MiniDump {pid} \"{outputPath}\" full")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Couldn't start rundll32.exe.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "Dump timed out after 30 seconds.");
            }

            if (!File.Exists(outputPath))
                return (false, proc.ExitCode == 0
                    ? "rundll32 reported success but no dump file was written - the target process may have exited."
                    : $"Dump failed - rundll32 exited with code {proc.ExitCode} (access denied is common for a protected process).");

            return (true, $"Dump written: {outputPath}");
        }
        catch (Exception ex)
        {
            return (false, $"Dump failed: {ex.Message}");
        }
    }
}
