using System.IO;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>One file or top-level folder found by <see cref="LargestItemsService.Scan"/>. Round
/// 17, #361 adds "size on disk" (SizeOnDiskBytes, via GetCompressedFileSizeW - null when it
/// couldn't be read) alongside the logical SizeBytes, plus whether this item is (or, for a folder,
/// contains) a reparse point or cloud-storage placeholder - a sparse/compressed file or an
/// OneDrive "files on demand" placeholder can report a large logical size while occupying a small
/// fraction of that on disk, which otherwise makes it look like the wrong culprit in this scan.
/// </summary>
public sealed record LargestItemInfo(
    string Path,
    long SizeBytes,
    bool IsDirectory,
    long? SizeOnDiskBytes = null,
    bool IsReparsePoint = false,
    bool IsCloudPlaceholder = false);

/// <summary>
/// Largest files/folders scanner (round 9, #39) - finds what's eating a nearly-full volume.
/// Deliberately on-demand only (called from a button, never automatically or on a timer): a full
/// recursive walk of a large volume is genuinely expensive, the same "expensive, so make it
/// explicit" tradeoff this app already takes for the event-log queries, the modules list, and the
/// HDD fragmentation analysis. Depth-capped (not just "stop after N files") so a volume with a
/// pathologically deep directory tree can't turn one scan into a multi-minute walk, and every
/// subtree is scanned independently so one access-denied folder (a common case - System Volume
/// Information, another user's profile, ...) just gets skipped rather than failing the whole scan.
/// </summary>
public static class LargestItemsService
{
    /// <summary>
    /// Walks <paramref name="rootPath"/> up to <paramref name="maxDepth"/> directory levels deep,
    /// summing each top-level-under-root folder's total size (recursively, ignoring the depth cap
    /// for the purpose of the sum itself - only *enumeration* stops at the cap, so a folder's
    /// reported size is never truncated) and listing individual files found along the way,
    /// returning the largest <paramref name="topN"/> items (files and folders mixed, sorted by
    /// size) found before the cap.
    /// </summary>
    public static List<LargestItemInfo> Scan(string rootPath, int maxDepth, int topN, CancellationToken cancellationToken = default)
    {
        var items = new List<LargestItemInfo>();
        if (!Directory.Exists(rootPath)) return items;

        try
        {
            var rootDir = new DirectoryInfo(rootPath);

            // Top-level folders get their own aggregated size - capped to the same depth as the
            // file listing below, so a pathologically deep subtree can't turn one folder's total
            // into an unbounded walk (its reported size may then be a lower bound rather than
            // exact, a deliberate safety/speed tradeoff for an on-demand, user-triggered scan).
            // #361: the same walk also sums each file's on-disk size and notes whether any file
            // under the folder is a reparse point/cloud placeholder, so a folder that's mostly
            // OneDrive placeholders doesn't read as "the" large item on disk.
            foreach (var dir in SafeEnumerateDirectories(rootDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (size, sizeOnDisk, hasPlaceholder) = SumDirectorySize(dir, 0, maxDepth + 3, cancellationToken);
                if (size > 0) items.Add(new LargestItemInfo(dir.FullName, size, true, sizeOnDisk, false, hasPlaceholder));
            }

            // Individual files, depth-capped.
            ScanFiles(rootDir, 0, maxDepth, items, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Root itself became inaccessible mid-scan - return whatever was gathered.
        }

        return items.OrderByDescending(i => i.SizeBytes).Take(topN).ToList();
    }

    private static void ScanFiles(DirectoryInfo dir, int depth, int maxDepth, List<LargestItemInfo> items, CancellationToken cancellationToken)
    {
        if (depth > maxDepth) return;
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in SafeEnumerateFiles(dir))
        {
            try
            {
                var (isReparse, isCloud) = ClassifyAttributes(file.Attributes);
                items.Add(new LargestItemInfo(file.FullName, file.Length, false, GetSizeOnDiskBytes(file.FullName), isReparse, isCloud));
            }
            catch { /* file vanished mid-enumeration - skip it */ }
        }

        foreach (var sub in SafeEnumerateDirectories(dir))
            ScanFiles(sub, depth + 1, maxDepth, items, cancellationToken);
    }

    /// <summary>#361: same recursive walk as before, now also summing each file's on-disk size
    /// (GetCompressedFileSizeW) and tracking whether any reparse point/cloud placeholder was seen
    /// under this folder.</summary>
    private static (long SizeBytes, long SizeOnDiskBytes, bool HasPlaceholder) SumDirectorySize(DirectoryInfo dir, int depth, int maxDepth, CancellationToken cancellationToken)
    {
        long total = 0;
        long totalOnDisk = 0;
        bool hasPlaceholder = false;
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in SafeEnumerateFiles(dir))
        {
            try
            {
                total += file.Length;
                totalOnDisk += GetSizeOnDiskBytes(file.FullName) ?? file.Length;
                var (isReparse, isCloud) = ClassifyAttributes(file.Attributes);
                if (isReparse || isCloud) hasPlaceholder = true;
            }
            catch { /* skip */ }
        }
        if (depth < maxDepth)
        {
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                var (subSize, subOnDisk, subPlaceholder) = SumDirectorySize(sub, depth + 1, maxDepth, cancellationToken);
                total += subSize;
                totalOnDisk += subOnDisk;
                hasPlaceholder |= subPlaceholder;
            }
        }

        return (total, totalOnDisk, hasPlaceholder);
    }

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS aren't named in
    // System.IO.FileAttributes, but that enum's underlying values are the raw Win32
    // FILE_ATTRIBUTE_* bits, so they can still be tested for with a plain bitmask.
    private const int FileAttributeRecallOnOpen = 0x40000;
    private const int FileAttributeRecallOnDataAccess = 0x400000;

    private static (bool IsReparsePoint, bool IsCloudPlaceholder) ClassifyAttributes(FileAttributes attrs)
    {
        bool isReparse = attrs.HasFlag(FileAttributes.ReparsePoint);
        bool isCloud = ((int)attrs & (FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0;
        return (isReparse, isCloud);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetCompressedFileSizeW")]
    private static extern uint GetCompressedFileSizeNative(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>#361: actual on-disk allocation for one file, via the documented
    /// GetCompressedFileSizeW Win32 API (no higher-level .NET wrapper exists for this - the same
    /// "P/Invoke a documented API with no managed equivalent" tradeoff this app already takes for
    /// a handful of other facts). Differs from FileInfo.Length for sparse files, NTFS-compressed
    /// files, and cloud-storage placeholders (a placeholder's logical size is the full remote file
    /// size; its on-disk size is only whatever's actually been downloaded locally). Null on any
    /// failure - never fabricated as equal to the logical size.</summary>
    private static long? GetSizeOnDiskBytes(string path)
    {
        try
        {
            uint low = GetCompressedFileSizeNative(path, out uint high);
            if (low == 0xFFFFFFFF)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0) return null; // genuine failure, not just a low-dword value that happens to equal 0xFFFFFFFF
            }
            return ((long)high << 32) | low;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#333: unbounded-depth, cancellable recursive file enumeration reusing the same
    /// access-denied-skips-the-subtree safe enumeration this class's own depth-capped scan uses -
    /// exposed (internal, not private) for FileVerificationService's full-tree read-verification
    /// walk, which (unlike the largest-items scan above) has no reason to cap depth.</summary>
    internal static IEnumerable<FileInfo> SafeEnumerateFilesRecursive(DirectoryInfo dir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in SafeEnumerateFiles(dir))
            yield return file;

        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            foreach (var file in SafeEnumerateFilesRecursive(sub, cancellationToken))
                yield return file;
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles(); }
        catch { return Enumerable.Empty<FileInfo>(); } // access denied / reparse point cycle / IO error
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch { return Enumerable.Empty<DirectoryInfo>(); }
    }
}
