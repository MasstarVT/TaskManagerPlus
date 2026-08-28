using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #332: read-only, end-to-end sequential surface scan of one physical drive - opens
/// \\.\PhysicalDriveN with FILE_FLAG_NO_BUFFERING (bypasses the OS cache, so every read genuinely
/// hits the media) and reads it start to finish in fixed-size, sector-aligned chunks, recording any
/// chunk that either errors outright or takes longer than a configurable stall threshold (a slow-
/// but-not-yet-failed read is often the earlier warning sign on a failing HDD, well before SMART
/// itself reports anything). Opened with GENERIC_READ only - there is no write path this could take
/// even by accident. Cancellable via CancellationToken; the caller (StorageViewModel) always runs
/// this via Task.Run, since a full pass over a large HDD can take hours.
/// </summary>
public static class SurfaceScanService
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    private const int ChunkSectors = 2048; // 1 MiB at 512-byte sectors - sector-aligned, cache-friendly chunk size
    private const int SectorBytes = 512;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string filename, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskGeometry
    {
        public long Cylinders;
        public int MediaType;
        public int TracksPerCylinder;
        public int SectorsPerTrack;
        public int BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskGeometryEx
    {
        public DiskGeometry Geometry;
        public long DiskSize;
    }

    // CTL_CODE(IOCTL_DISK_BASE=0x7, 0x0028, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0) - the documented
    // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX code.
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    /// <summary>Total addressable size of the drive, via IOCTL_DISK_GET_DRIVE_GEOMETRY_EX - null
    /// (never a guess) when this controller doesn't answer the IOCTL, in which case the scan below
    /// simply runs until ReadFile itself reports end-of-device rather than a known total.</summary>
    private static long? ReadDiskSizeBytes(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<DiskGeometryEx>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero);
            if (!ok) return null;
            var geo = Marshal.PtrToStructure<DiskGeometryEx>(buffer);
            return geo.DiskSize > 0 ? geo.DiskSize : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public sealed record ScanProgress(long CurrentLba, long TotalLba, int ProblemsFound);

    /// <summary>Runs the scan, invoking <paramref name="onProblem"/> for each error/stall range
    /// found and <paramref name="onProgress"/> periodically. <paramref name="stallThresholdMs"/>
    /// flags any chunk read slower than this as a problem even when it eventually succeeds. Returns
    /// a short summary message and the total LBA count actually scanned; propagates
    /// OperationCanceledException for cooperative cancellation, never any other exception.</summary>
    public static (bool Success, string Message, long TotalLbaScanned) Scan(
        int diskIndex, double stallThresholdMs, Action<SurfaceScanResult> onProblem,
        Action<ScanProgress> onProgress, CancellationToken cancellationToken)
    {
        string path = $@"\\.\PhysicalDrive{diskIndex}";
        SafeFileHandle? handle = null;
        IntPtr raw = IntPtr.Zero;
        try
        {
            handle = CreateFileW(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting,
                FileFlagNoBuffering | FileFlagSequentialScan, IntPtr.Zero);
            if (handle.IsInvalid)
                return (false, $"Could not open {path} for reading (Win32 error {Marshal.GetLastWin32Error()}). Make sure no other tool has it locked exclusively.", 0);

            long? diskSizeBytes = ReadDiskSizeBytes(handle);
            long totalLba = diskSizeBytes.HasValue ? diskSizeBytes.Value / SectorBytes : 0;

            int chunkBytes = ChunkSectors * SectorBytes;
            // FILE_FLAG_NO_BUFFERING requires the buffer address itself to be sector-aligned, not
            // just its size - AllocHGlobal isn't guaranteed aligned, so over-allocate and align up.
            raw = Marshal.AllocHGlobal(chunkBytes + SectorBytes);
            IntPtr aligned = new IntPtr(((raw.ToInt64() + SectorBytes - 1) / SectorBytes) * SectorBytes);

            long lba = 0;
            int problems = 0;
            var sw = Stopwatch.StartNew();
            long lastProgressReportLba = 0;

            while (totalLba == 0 || lba < totalLba)
            {
                cancellationToken.ThrowIfCancellationRequested();

                sw.Restart();
                bool ok = ReadFile(handle, aligned, (uint)chunkBytes, out uint bytesRead, IntPtr.Zero);
                sw.Stop();
                double elapsedMs = sw.Elapsed.TotalMilliseconds;

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 38) break; // ERROR_HANDLE_EOF - the expected end of a drive whose geometry couldn't be read up front

                    problems++;
                    onProblem(new SurfaceScanResult
                    {
                        StartLba = lba,
                        EndLba = lba + ChunkSectors - 1,
                        IsHardError = true,
                        ElapsedMs = elapsedMs,
                        Note = $"Read error (Win32 error {err})",
                    });
                    // Can't safely tell how far the drive actually advanced past a hard error -
                    // skip this whole chunk and keep going rather than looping on it forever.
                    lba += ChunkSectors;
                    continue;
                }

                if (bytesRead == 0) break; // genuine end of device on a drive whose geometry read failed

                if (elapsedMs >= stallThresholdMs)
                {
                    problems++;
                    onProblem(new SurfaceScanResult
                    {
                        StartLba = lba,
                        EndLba = lba + ChunkSectors - 1,
                        IsHardError = false,
                        ElapsedMs = elapsedMs,
                        Note = $"Slow read ({elapsedMs:0} ms, threshold {stallThresholdMs:0} ms)",
                    });
                }

                lba += (int)(bytesRead / SectorBytes);

                if (lba - lastProgressReportLba >= (long)ChunkSectors * 50)
                {
                    lastProgressReportLba = lba;
                    onProgress(new ScanProgress(lba, totalLba, problems));
                }
            }

            onProgress(new ScanProgress(lba, totalLba, problems));
            return (true, problems == 0
                ? $"Scan complete - no errors or stalls across {lba:N0} sectors."
                : $"Scan complete - {problems} problem range(s) found across {lba:N0} sectors.", lba);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Scan failed: {ex.Message}", 0);
        }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.FreeHGlobal(raw);
            handle?.Dispose();
        }
    }
}
