using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
/// the comparison is done in that same form: the reported path is opened and its own NT name read
/// back with GetFinalPathNameByHandleW, which resolves SUBST drives and junctions the same way the
/// loader's own open did (network images aren't compared - redirector device names can differ per
/// logon session). A drive-letter conversion of the device
/// path (TryConvertDeviceToDrivePath, via QueryDosDevice - the technique in Microsoft's
/// "Retrieving a File Name From a File Handle" sample) is still shown, but comparing drive-letter
/// strings falsely flagged every exe run from a SUBST drive, a mapped drive or a UNC path. When
/// the reported path can't be opened from this process (a mapped drive or SUBST that only exists
/// in the user's non-elevated logon session, for instance) the comparison is reported as not done.
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
    /// <summary>FileExists/PathMismatch are null when that check couldn't be made - an unreadable
    /// or unresolvable path is reported as such, never as a "Yes"/"No" verdict.</summary>
    public sealed record HollowingCheckResult(
        string? ReportedPath,
        string? MappedPath,
        bool? FileExists,
        bool? PathMismatch,
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
                return new HollowingCheckResult(null, null, null, null, $"Couldn't read the main module: {ex.Message}");
            }

            if (mainModule is null)
                return new HollowingCheckResult(null, null, null, null, "Couldn't read the main module (access denied, or it has already exited).");

            reportedPath = mainModule.FileName;
            IntPtr baseAddress = mainModule.BaseAddress;

            string? reportedNtPath = TryGetNtPath(reportedPath);

            // "No" only when this process could have seen the file: a path on a network share, or
            // on a drive letter that doesn't exist in this (elevated) logon session, reads as
            // missing here even though the process is running it fine.
            bool? fileExists = null;
            try
            {
                if (reportedNtPath is not null || (!string.IsNullOrEmpty(reportedPath) && File.Exists(reportedPath)))
                    fileExists = true;
                else if (IsOnLocalDriveVisibleHere(reportedPath))
                    fileExists = false;
            }
            catch { /* leave null - can't confirm either way */ }

            string? mappedPath = null;
            string? mappedDevicePath = null;
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
                        mappedDevicePath = sb.ToString();
                        mappedPath = TryConvertDeviceToDrivePath(mappedDevicePath) ?? mappedDevicePath;
                    }
                }
            }
            catch { /* best-effort - leave mappedPath null, degrade below */ }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }

            // A redirector path can carry a per-logon-session segment (e.g. ";Z:<logon id>"), so
            // this elevated session's name for the same network file may differ from the
            // loader's - treated as not comparable rather than as a mismatch.
            bool comparable = mappedDevicePath is not null && reportedNtPath is not null
                && !IsNetworkDevicePath(mappedDevicePath) && !IsNetworkDevicePath(reportedNtPath);
            bool? pathMismatch = comparable
                ? !string.Equals(NormalizeForCompare(mappedDevicePath!), NormalizeForCompare(reportedNtPath!), StringComparison.OrdinalIgnoreCase)
                : null;

            string? note = fileExists == false
                ? "The reported image file no longer exists on disk while the process is still running - a classic process-hollowing/replacement tell."
                : pathMismatch == true
                    ? "The actually-mapped file path differs from the process's own reported path - a classic process-hollowing/replacement tell."
                    : mappedPath is null
                        ? "Couldn't read the actually-mapped file path (access denied) - the mapped-path comparison couldn't be made."
                        : pathMismatch is null
                            ? "The mapped-path comparison was skipped rather than guessed - the image is on a network share (whose device names differ per logon session), or the reported path couldn't be opened from this app (a mapped/SUBST drive that only exists in your normal non-elevated session, or access denied)."
                            : null;

            return new HollowingCheckResult(reportedPath, mappedPath, fileExists, pathMismatch, note);
        }
        catch (Exception ex)
        {
            return new HollowingCheckResult(reportedPath, null, null, null, $"Couldn't complete the check: {ex.Message}");
        }
    }

    private static string NormalizeForCompare(string path) => path.TrimEnd('\\', '/');

    private static bool IsNetworkDevicePath(string path) =>
        path.StartsWith(@"\Device\Mup", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Redirector", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reported path's NT device-form name as this process resolves it (SUBST,
    /// junction and redirector included) - directly comparable with GetMappedFileNameW's output.
    /// FILE_NAME_OPENED rather than normalized, so a short (8.3) name the loader opened by stays
    /// in the same form GetMappedFileNameW reports it in. Null when the file can't be opened.</summary>
    private static string? TryGetNtPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var sb = new StringBuilder(1024);
            uint len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, FileNameOpened | VolumeNameNt);
            if (len == 0) return null;
            if (len >= sb.Capacity)
            {
                sb = new StringBuilder((int)len + 1);
                len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, FileNameOpened | VolumeNameNt);
                if (len == 0 || len >= sb.Capacity) return null;
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True for a drive-letter path whose drive exists in this process's own logon
    /// session and isn't a network drive - the only case where "File.Exists is false" really
    /// means the file is gone rather than out of this process's sight.</summary>
    private static bool IsOnLocalDriveVisibleHere(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':' || !char.IsLetter(path[0])) return false;
        try
        {
            string drive = path[..2];
            var target = new StringBuilder(512);
            if (QueryDosDevice(drive, target, target.Capacity) == 0) return false;
            return new DriveInfo(drive).DriveType != DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

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
                // The match must end on a path-segment boundary: "\Device\HarddiskVolume1" is a
                // string prefix of "\Device\HarddiskVolume10\..." too, and matching it would
                // splice the wrong drive letter onto a mangled remainder ("D:2\apps\x.exe") -
                // which then never equals the reported path, i.e. a false hollowing flag on an
                // innocent process on any machine with 10+ volumes.
                if (!devicePath.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (devicePath.Length == devicePrefix.Length || devicePath[devicePrefix.Length] == '\\')
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

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // FILE_SHARE_READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameOpened = 0x8;
    private const uint VolumeNameNt = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

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
