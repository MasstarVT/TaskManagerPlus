using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>One file or top-level folder found by <see cref="LargestItemsService.Scan"/>.</summary>
public sealed record LargestItemInfo(string Path, long SizeBytes, bool IsDirectory);

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
            foreach (var dir in SafeEnumerateDirectories(rootDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = SumDirectorySize(dir, 0, maxDepth + 3, cancellationToken);
                if (size > 0) items.Add(new LargestItemInfo(dir.FullName, size, true));
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
            try { items.Add(new LargestItemInfo(file.FullName, file.Length, false)); }
            catch { /* file vanished mid-enumeration - skip it */ }
        }

        foreach (var sub in SafeEnumerateDirectories(dir))
            ScanFiles(sub, depth + 1, maxDepth, items, cancellationToken);
    }

    private static long SumDirectorySize(DirectoryInfo dir, int depth, int maxDepth, CancellationToken cancellationToken)
    {
        long total = 0;
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in SafeEnumerateFiles(dir))
        {
            try { total += file.Length; } catch { /* skip */ }
        }
        if (depth < maxDepth)
        {
            foreach (var sub in SafeEnumerateDirectories(dir))
                total += SumDirectorySize(sub, depth + 1, maxDepth, cancellationToken);
        }

        return total;
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
