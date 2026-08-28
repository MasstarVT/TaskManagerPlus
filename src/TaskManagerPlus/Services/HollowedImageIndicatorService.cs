using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #847: a lightweight process-hollowing/image-replacement indicator for a process's main
/// module, comparing two genuinely different sources of "what's this process actually running":
///
/// 1. The process's own *reported* image path (Process.MainModule.FileName - reads the loader's
///    record of what it thinks it loaded, ultimately backed by the PEB).
/// 2. What's *actually mapped* into memory at that module's base address, read directly off the
///    page's VAD via GetMappedFileNameW (psapi.dll) - independent of anything the loader/PEB claims.
///
/// In real process hollowing, an attacker replaces the mapped pages after the loader has already
/// recorded the original path, so these two can diverge - the classic tell. GetMappedFileNameW
/// returns paths in NT device form (e.g. "\Device\HarddiskVolume3\Windows\System32\x.exe"), so
/// TryConvertDeviceToDrivePath resolves that back to a drive-letter path via QueryDosDevice (the
/// same technique documented in Microsoft's own "Retrieving a File Name From a File Handle" sample)
/// before comparing.
///
/// Also flags "image file deleted while running" - File.Exists false on the reported path is a
/// second, independent hollowing/replacement tell (the disk file is gone but the process is still
/// executing from whatever was mapped when it launched).
///
/// Deliberately skips the file-size cross-check the parent suggestion mentions as optional:
/// ProcessModule.ModuleMemorySize is the in-memory image size (section-aligned, includes headers),
/// not the on-disk file size Process.MainModule/FileInfo would report - the two don't reliably match
/// even for a completely legitimate, unmodified binary, so comparing them would produce false
/// positives rather than a meaningful signal. Existence + path-mismatch are the two checks kept.
///
/// Only inspects the main module (not a full module walk), so this stays comparatively cheap - still
/// run via Task.Run from the ViewModel since it's still a handful of syscalls, but without 846/848's
/// aggressive per-item timeout/capping (there's exactly one module to check here).
/// </summary>
public static class HollowedImageIndicatorService
{
    public sealed record HollowingCheckResult(
        string? ReportedPath,
        string? MappedPath,
        bool FileExists,
        bool PathMismatch,
        string? Note);

    public static HollowingCheckResult CheckMainModule(int pid)
    {
        string? reportedPath = null;
        try
        {
            using var proc = Process.GetProcessById(pid);

            ProcessModule? mainModule;
            try { mainModule = proc.MainModule; }
            catch (Exception ex)
            {
                return new HollowingCheckResult(null, null, false, false, $"Couldn't read the main module: {ex.Message}");
            }

            if (mainModule is null)
                return new HollowingCheckResult(null, null, false, false, "Couldn't read the main module (access denied, or it has already exited).");

            reportedPath = mainModule.FileName;
            IntPtr baseAddress = mainModule.BaseAddress;

            bool fileExists = false;
            try { fileExists = !string.IsNullOrEmpty(reportedPath) && File.Exists(reportedPath); }
            catch { /* leave false - a stat failure here is itself worth surfacing as "can't confirm it exists" but not worth throwing over */ }

            string? mappedPath = null;
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
                if (hProcess != IntPtr.Zero && baseAddress != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024);
                    uint len = GetMappedFileNameW(hProcess, baseAddress, sb, (uint)sb.Capacity);
                    if (len > 0)
                    {
                        string devicePath = sb.ToString();
                        mappedPath = TryConvertDeviceToDrivePath(devicePath) ?? devicePath;
                    }
                }
            }
            catch { /* best-effort - leave mappedPath null, degrade below */ }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }

            bool pathMismatch = mappedPath is not null && reportedPath is not null &&
                !string.Equals(NormalizeForCompare(mappedPath), NormalizeForCompare(reportedPath), StringComparison.OrdinalIgnoreCase);

            string? note = !fileExists
                ? "The reported image file no longer exists on disk while the process is still running - a classic process-hollowing/replacement tell."
                : pathMismatch
                    ? "The actually-mapped file path differs from the process's own reported path - a classic process-hollowing/replacement tell."
                    : mappedPath is null
                        ? "Couldn't read the actually-mapped file path (access denied) - only the on-disk existence check above could be completed."
                        : null;

            return new HollowingCheckResult(reportedPath, mappedPath, fileExists, pathMismatch, note);
        }
        catch (Exception ex)
        {
            return new HollowingCheckResult(reportedPath, null, false, false, $"Couldn't complete the check: {ex.Message}");
        }
    }

    private static string NormalizeForCompare(string path) => path.TrimEnd('\\', '/');

    /// <summary>Resolves an NT device-form path (as returned by GetMappedFileNameW) back to a
    /// drive-letter path by matching against each mounted drive's QueryDosDevice target - the
    /// standard technique for this conversion; a drive that can't be resolved (a network share, a
    /// volume with no drive letter, ...) leaves the raw device path as the fallback rather than
    /// failing the whole check.</summary>
    private static string? TryConvertDeviceToDrivePath(string devicePath)
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string driveLetter = drive.Name.TrimEnd('\\'); // e.g. "C:"
                var target = new StringBuilder(512);
                if (QueryDosDevice(driveLetter, target, target.Capacity) == 0) continue;

                string devicePrefix = target.ToString();
                if (devicePath.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
                    return driveLetter + devicePath[devicePrefix.Length..];
            }
        }
        catch
        {
            // Best-effort - leave unresolved, caller falls back to the raw device path.
        }
        return null;
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetMappedFileNameW(IntPtr hProcess, IntPtr lpv, StringBuilder lpFilename, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
