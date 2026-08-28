using System.IO;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #333: walks a chosen folder tree reading every byte of every file and lists which ones fail -
/// turns an abstract bad-sector count into "these four photos are already gone" rather than a raw
/// LBA range. Reuses LargestItemsService's safe directory/file enumeration (an access-denied
/// subtree is simply skipped, same as that scan) instead of duplicating the same walk logic.
/// On-demand only, and cancellable - reading every byte of a large tree is genuinely slow,
/// especially on a drive that's already struggling.
/// </summary>
public static class FileVerificationService
{
    private const int BufferSize = 1 << 20; // 1 MiB read buffer

    public static (int FilesChecked, List<FileVerificationFailure> Failures) Verify(
        string rootPath, Action<int>? onFileChecked, CancellationToken cancellationToken)
    {
        var failures = new List<FileVerificationFailure>();
        int checkedCount = 0;
        if (!Directory.Exists(rootPath)) return (0, failures);

        var buffer = new byte[BufferSize];
        foreach (var file in LargestItemsService.SafeEnumerateFilesRecursive(new DirectoryInfo(rootPath), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new FileVerificationFailure(file.FullName, ex.Message));
            }
            finally
            {
                checkedCount++;
                onFileChecked?.Invoke(checkedCount);
            }
        }
        return (checkedCount, failures);
    }
}
