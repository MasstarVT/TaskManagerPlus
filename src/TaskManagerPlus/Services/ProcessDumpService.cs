using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Item 65: MiniDumpWriteDump (dbghelp.dll) - the same call Task Manager's own "Create dump file"
/// context-menu action makes, exposed here as an on-demand Processes-tab action so a freeze can be
/// captured while it's actually happening rather than reconstructed afterwards from whatever
/// LocalDumps/WER happened to catch. No documented tool wraps this either - see
/// WaitChainAnalysisService's own remarks on why raw P/Invoke is the right call per CLAUDE.md.
///
/// Wrapped defensively (never throws past this class) since dumping another - possibly hung -
/// process can be slow, especially a Full dump of a large working set; unlike
/// WaitChainAnalysisService/HandleInspectionService this isn't run on an abandoned background
/// thread with its own timeout, since MiniDumpWriteDump reads the target's memory directly rather
/// than waiting on anything that's known to hang indefinitely, and an artificial timeout would
/// just abandon an otherwise-successful large dump partway through. Callers (ProcessesViewModel)
/// still run this via Task.Run to keep the UI thread responsive while it completes.
///
/// #242: this file also carries a second, independently-added entry point - CreateDumpAsync,
/// a one-click full user-mode dump for a hung window's owning process (ResponsivenessViewModel).
/// It deliberately does NOT call WriteDump above: CLAUDE.md prefers a known Windows tool/API over
/// raw interop where one exists, and for a full-process minidump that's
/// `rundll32.exe comsvcs.dll,MiniDump &lt;pid&gt; &lt;path&gt; full` - the same built-in DLL export
/// (every Windows install's comsvcs.dll ships a MiniDump entry point) that real Task Manager's own
/// "Create dump file" action and many support scripts already shell out to. Two call paths to the
/// "same" underlying capability, kept side by side rather than unified, since WriteDump's raw
/// MiniDumpWriteDump P/Invoke and CreateDumpAsync's rundll32 shell-out have different failure
/// modes/timeout behavior and each is already wired to a working caller.
/// </summary>
public static class ProcessDumpService
{
    public enum DumpKind { Mini, Full }

    // MINIDUMP_TYPE flags (dbghelp.h) - only the handful this feature actually combines.
    private const int MiniDumpWithDataSegs = 0x00000001;
    private const int MiniDumpWithFullMemory = 0x00000002;
    private const int MiniDumpWithHandleData = 0x00000004;
    private const int MiniDumpWithThreadInfo = 0x00001000;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    /// <summary>Writes a dump of the given process to filePath. Mini includes data segments,
    /// handle data and thread info (a small but genuinely useful dump, more than the bare
    /// MiniDumpNormal Windows itself defaults to); Full additionally includes the process' entire
    /// address space (MiniDumpWithFullMemory) - large, but captures everything a debugger could
    /// need to inspect heap state at the moment of the freeze.</summary>
    public static (bool Success, string? Error) WriteDump(int pid, string filePath, DumpKind kind)
    {
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (processHandle == IntPtr.Zero)
                return (false, "Couldn't open the process (access denied, or it already exited).");

            int dumpType = kind == DumpKind.Full
                ? MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithThreadInfo
                : MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithThreadInfo;

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                // SafeFileHandle (not DangerousGetHandle()'s raw IntPtr) so the CLR marshaler keeps
                // the handle alive for the duration of the native call - a raw IntPtr captured
                // ahead of time has no such guarantee and could, in principle, be finalized out
                // from under a long-running call like a Full dump.
                bool ok = MiniDumpWriteDump(processHandle, (uint)pid, stream.SafeFileHandle, dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    return (false, $"MiniDumpWriteDump failed (Win32 error {err}).");
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
    }

    /// <summary>#242: one-click full user-mode process dump for a hung window's owning process,
    /// ready to load in WinDbg - see the class remarks above for why this shells out to rundll32
    /// instead of calling WriteDump above.</summary>
    public static async Task<(bool Success, string Message)> CreateDumpAsync(int pid, string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string comsvcs = Path.Combine(Environment.SystemDirectory, "comsvcs.dll");
            if (!File.Exists(comsvcs))
                return (false, "comsvcs.dll wasn't found on this system - can't create a dump this way.");

            var (_, exitCode) = await ToolRunner.RunCapturedAsync(
                "rundll32.exe", $"\"{comsvcs}\", MiniDump {pid} \"{outputPath}\" full", 30_000);
            if (exitCode is null) return (false, "Dump timed out after 30 seconds.");

            if (!File.Exists(outputPath))
                return (false, exitCode == 0
                    ? "rundll32 reported success but no dump file was written - the target process may have exited."
                    : $"Dump failed - rundll32 exited with code {exitCode} (access denied is common for a protected process).");

            return (true, $"Dump written: {outputPath}");
        }
        catch (Exception ex)
        {
            return (false, $"Dump failed: {ex.Message}");
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle hFile, int dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
