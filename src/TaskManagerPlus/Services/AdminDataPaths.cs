using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// A data folder no unelevated process can touch: %ProgramData%\TaskManagerPlus, owned by
/// Administrators with an admin-only DACL. This app always runs elevated, but %TEMP%,
/// %AppData% and the settings folder belong to the user, so anything running as that user
/// without elevation can rename a folder there and drop a junction in its place between this
/// app's check and its use (turning an elevated write/move/delete into one aimed anywhere), or
/// plant the file an elevated tool is about to read back. ProgramData doesn't give ordinary users
/// delete-child, so once this folder exists and is admin-only, nothing below it can be swapped out.
///
/// Anyone can still CREATE a folder in ProgramData, so the root is re-checked on every use (owner,
/// DACL, not a reparse point); a folder somebody else pre-created is moved aside and replaced. Every
/// method fails closed: a null result means "couldn't establish a safe folder" and the caller refuses
/// the operation rather than falling back to a user-writable path.
/// </summary>
public static class AdminDataPaths
{
    private static readonly object Gate = new();

    /// <summary>%ProgramData%\TaskManagerPlus - the path only; use TryGetRoot before writing to it.</summary>
    public static string RootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaskManagerPlus");

    /// <summary>The verified admin-only root, created (or a squatted one replaced) as needed. Null
    /// with a short reason when that isn't possible - e.g. this process isn't elevated.</summary>
    public static string? TryGetRoot(out string? error)
    {
        lock (Gate)
        {
            error = null;
            string root = RootPath;
            try
            {
                var dir = new DirectoryInfo(root);
                if (dir.Exists)
                {
                    if (CheckFolder(dir) is null) return root;
                    // Pre-created by someone else (or tampered with): administrators have delete-child
                    // on ProgramData, so move it aside rather than trusting or deleting its contents.
                    Directory.Move(root, $"{root}.untrusted-{DateTime.Now:yyyyMMddHHmmss}");
                }
                else if (File.Exists(root))
                {
                    File.Move(root, $"{root}.untrusted-{DateTime.Now:yyyyMMddHHmmss}");
                }

                QuarantineService.CreateAdminOnlyDirectory(root);
                // Re-check: if another process re-created the name in the gap, Create() kept its
                // folder and ACL, and this fails closed.
                error = CheckFolder(new DirectoryInfo(root));
                return error is null ? root : null;
            }
            catch (Exception ex)
            {
                error = $"couldn't prepare {root} ({ex.Message})";
                return null;
            }
        }
    }

    /// <summary>A persistent admin-only subfolder (e.g. "Quarantine", "Undo", "Backups"), created
    /// on first use. Inherits the root's admin-only DACL. Null with a reason on failure.</summary>
    public static string? TryGetArea(string area, out string? error)
    {
        string? root = TryGetRoot(out error);
        if (root is null) return null;
        try
        {
            // Explicit owner + DACL rather than inheritance: with the "objects created by
            // administrators are owned by the creator" policy, an inherited folder would be owned
            // by the user account and fail the check.
            var dir = QuarantineService.CreateAdminOnlyDirectory(Path.Combine(root, area));
            error = CheckFolder(dir);
            return error is null ? dir.FullName : null;
        }
        catch (Exception ex)
        {
            error = $"couldn't prepare {area} ({ex.Message})";
            return null;
        }
    }

    /// <summary>A fresh, uniquely named admin-only folder for one operation - an elevated tool's
    /// output file, a staging folder, an extracted bundle. Delete it with TryDelete when done;
    /// leftovers from a crash are swept by PurgeStaleTemp. Null with a reason on failure.</summary>
    public static DirectoryInfo? CreatePrivateTempDirectory(string purpose, out string? error)
    {
        string? temp = TryGetArea("Temp", out error);
        if (temp is null) return null;
        try
        {
            string safePurpose = string.Concat(purpose.Where(char.IsLetterOrDigit));
            if (safePurpose.Length == 0) safePurpose = "op";
            return QuarantineService.CreateAdminOnlyDirectory(Path.Combine(temp, $"{safePurpose}-{Guid.NewGuid():N}"));
        }
        catch (Exception ex)
        {
            error = $"couldn't create a private folder ({ex.Message})";
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> resolves to somewhere under the admin-only root.</summary>
    public static bool IsUnderRoot(string path)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
            string full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Best-effort recursive delete of a folder this class handed out.</summary>
    public static void TryDelete(DirectoryInfo? dir)
    {
        if (dir is null || !IsUnderRoot(dir.FullName)) return;
        try { dir.Delete(recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Removes per-operation folders older than <paramref name="olderThan"/> (a crash
    /// mid-operation leaves one behind). Never creates the root.</summary>
    public static void PurgeStaleTemp(TimeSpan olderThan)
    {
        try
        {
            var temp = new DirectoryInfo(Path.Combine(RootPath, "Temp"));
            if (!temp.Exists || CheckFolder(new DirectoryInfo(RootPath)) is not null) return;
            DateTime cutoff = DateTime.UtcNow - olderThan;
            foreach (var child in temp.EnumerateDirectories())
            {
                if (child.CreationTimeUtc < cutoff) TryDelete(child);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string? CheckFolder(DirectoryInfo dir)
    {
        dir.Refresh();
        if (!dir.Exists) return $"{dir.FullName} doesn't exist";
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return $"{dir.FullName} is a junction or symbolic link";
        return QuarantineService.CheckAdminOnly(dir);
    }
}
