using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #899(c): "move (never shred) files the user chooses to remove into a timestamped
/// quarantine folder." Quarantine always File.Move's (never File.Delete's) into
/// %ProgramData%\TaskManagerPlus\Quarantine\&lt;yyyyMMdd-HHmmss&gt;\&lt;originalFileName&gt; - Restore
/// reverses that exact move. Deliberately separate from DefenderService's own quarantine browser
/// (#863, which reads/restores/purges Windows Defender's OWN quarantine store) - this is this
/// app's own, much simpler "get a suspicious file out of its running location without destroying
/// evidence" mechanism for files the Persistence grid flags, not a Defender feature.
///
/// The action journal is writable by any unelevated process running as the user, while Restore
/// moves files as Administrator. So the quarantine lives in AdminDataPaths' admin-only area (a
/// folder in the user's own %AppData% could be renamed and replaced with a junction between this
/// app's check and its move), each timestamp folder holds a record of where its file came from,
/// and Restore trusts only that record, never the journal's paths on their own. Both ends of each
/// move that sit in user-writable folders are held open and checked to resolve to the path the
/// user saw. Files an earlier version quarantined under the settings folder are never moved back
/// automatically.
/// </summary>
public static class QuarantineService
{
    private const string OriginRecordName = "quarantine-origin.txt";
    private const string AreaName = "Quarantine";

    public static (bool Success, string? QuarantinePath, string? Error) Quarantine(string filePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(filePath)) return (false, null, "The file's path isn't absolute - not quarantining it.");
            if (!File.Exists(filePath)) return (false, null, "File not found - it may have already been moved or deleted.");

            string? root = AdminDataPaths.TryGetArea(AreaName, out string? areaError);
            if (root is null)
                return (false, null, $"Couldn't prepare the protected quarantine folder - this needs the app running as Administrator ({areaError}).");

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string folder = Path.Combine(root, stamp);
            for (int n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
                folder = Path.Combine(root, $"{stamp}-{n}");

            DirectoryInfo dir;
            try { dir = CreateAdminOnlyDirectory(folder); }
            catch (Exception ex)
            {
                return (false, null, $"Couldn't create a protected quarantine folder - this needs the app running as Administrator ({ex.Message}).");
            }
            if (CheckAdminOnly(dir) is { } problem)
                return (false, null, $"Couldn't create a protected quarantine folder ({problem}).");

            // The source sits in a folder the user can write to: while this handle is open no folder
            // above it can be renamed, and it must resolve to the very path the user confirmed (a
            // junction swapped in after the scan would point it at some other file).
            string fullSource = Path.GetFullPath(filePath);
            using var source = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (CheckResolvesTo(source.SafeFileHandle, fullSource) is { } redirected)
                return (false, null, $"Not quarantining: {redirected}.");

            string name = Path.GetFileName(fullSource);
            if (name.Equals(OriginRecordName, StringComparison.OrdinalIgnoreCase)) name = "_" + name;
            string finalDestination = Path.Combine(folder, name);
            File.WriteAllText(Path.Combine(folder, OriginRecordName), $"{name}\n{fullSource}\n");

            File.Move(fullSource, finalDestination);

            // Across volumes File.Move is copy-then-delete, and Windows reports success even when
            // the delete fails (a running or locked binary) - the original would still be in place.
            if (File.Exists(fullSource))
            {
                try { File.Delete(finalDestination); } catch { /* best-effort - leave the copy rather than fail differently */ }
                return (false, null, "The file was copied but the original couldn't be removed (it may be running or locked), so nothing was quarantined.");
            }
            return (true, finalDestination, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Undo for a FileQuarantine journal entry - moves the file back to its original
    /// path. Fails cleanly (doesn't overwrite) if something new already exists at the original
    /// path, and refuses unless both paths match the admin-only origin record written at
    /// quarantine time.</summary>
    public static (bool Success, string? Error) Restore(string quarantinePath, string originalPath)
    {
        string byHand = $"If you trust this entry, move \"{quarantinePath}\" back to \"{originalPath}\" by hand.";
        try
        {
            if (!Path.IsPathFullyQualified(quarantinePath) || !Path.IsPathFullyQualified(originalPath))
                return (false, "The journal entry's paths aren't absolute - not restoring.");

            string fullQuarantine = Path.GetFullPath(quarantinePath);
            string? folder = Path.GetDirectoryName(fullQuarantine);

            // Earlier versions quarantined into the settings folder, which other programs running
            // as this user can change - those files are listed but only restored by hand.
            string legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.GetPath(AreaName)));
            if (folder is not null && string.Equals(Path.GetDirectoryName(folder), legacyRoot, StringComparison.OrdinalIgnoreCase))
                return (false, $"This file was quarantined by an earlier version into {legacyRoot}, a folder other programs running as you can change, so it won't be moved back automatically. " + byHand);

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AdminDataPaths.RootPath, AreaName)));
            if (folder is null || !string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
                return (false, "That file isn't in this app's Quarantine folder - not restoring.");
            if (AdminDataPaths.TryGetArea(AreaName, out string? areaError) is null)
                return (false, $"Not restoring: {areaError}. " + byHand);

            if (!File.Exists(fullQuarantine)) return (false, "Quarantined file not found - it may have already been restored or removed.");

            string recordPath = Path.Combine(folder, OriginRecordName);
            if (!File.Exists(recordPath))
                return (false, "This file was quarantined before restores were tied to a protected record, so it won't be moved back automatically. " + byHand);

            using var record = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((FindReparseAttribute(fullQuarantine) ?? FindReparseAttribute(recordPath) ?? FindReparseAttribute(folder)
                 ?? CheckAdminOnly(new DirectoryInfo(folder))) is { } problem)
                return (false, $"Not restoring: {problem}. " + byHand);

            using var reader = new StreamReader(record, leaveOpen: true);
            string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2
                || !lines[0].Equals(Path.GetFileName(fullQuarantine), StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(lines[1])
                || !string.Equals(Path.GetFullPath(lines[1]), Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
                return (false, "The journal entry doesn't match this file's quarantine record - not restoring.");

            string fullOriginal = Path.GetFullPath(originalPath);
            if (File.Exists(fullOriginal)) return (false, "A file already exists at the original path - not overwriting it.");

            string? originalDir = Path.GetDirectoryName(fullOriginal);
            if (string.IsNullOrEmpty(originalDir)) return (false, "The original path has no folder - not restoring.");

            // The original folder is usually user-writable and now empty, so it could have been
            // replaced with a junction to a protected folder: hold it open (so it can't be swapped
            // again) and check it still resolves to itself before moving the file in.
            using var destination = PinDirectory(originalDir, out string? pinError);
            if (destination is null) return (false, $"Not restoring: {pinError}. " + byHand);

            File.Move(fullQuarantine, fullOriginal);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- admin-only folder helpers (also used by RegistryBackupService and AdminDataPaths) ----

    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const FileSystemRights DirectoryWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrustedSid(IdentityReference? id) =>
        id is SecurityIdentifier sid && (sid == AdministratorsSid || sid == LocalSystemSid || sid.Value == TrustedInstallerSid);

    /// <summary>Creates a folder owned by Administrators that only SYSTEM/Administrators can
    /// modify (Authenticated Users may read). Throws when the process isn't elevated - callers
    /// treat that as a refusal.</summary>
    internal static DirectoryInfo CreateAdminOnlyDirectory(string path)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var dir = new DirectoryInfo(path);
        dir.Create(security);
        return dir;
    }

    /// <summary>Null when the folder is owned by, and modifiable only by, SYSTEM/Administrators/
    /// TrustedInstaller - i.e. an unelevated process can't have created it or planted files in it.
    /// Otherwise a short reason. Fails closed on an unreadable ACL.</summary>
    internal static string? CheckAdminOnly(DirectoryInfo dir)
    {
        try
        {
            var security = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!IsTrustedSid(security.GetOwner(typeof(SecurityIdentifier))))
                return $"{dir.FullName} isn't owned by Administrators";
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((rule.FileSystemRights & DirectoryWriteRights) == 0) continue;
                if (!IsTrustedSid(rule.IdentityReference)) return $"{dir.FullName} is writable by a non-administrator";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"the permissions on {dir.FullName} couldn't be read ({ex.Message})";
        }
    }

    /// <summary>Null when <paramref name="path"/> (a file or folder) itself isn't a junction/symlink.</summary>
    private static string? FindReparseAttribute(string path)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        return exists && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ? $"{path} is a junction or symbolic link" : null;
    }

    // ---- pinning a user-writable path for the length of one move ----

    /// <summary>Opens <paramref name="directory"/> (creating it if missing) so it can't be renamed,
    /// deleted or replaced while the handle is open, and checks it resolves to that exact path -
    /// no junction, symbolic link or mount point anywhere in it. Null with a reason otherwise.</summary>
    private static SafeFileHandle? PinDirectory(string directory, out string? error)
    {
        error = null;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full))
        {
            // Pin the nearest existing ancestor first, so the folders created below it can only
            // land where that ancestor really is.
            string? existing = Path.GetDirectoryName(full);
            while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
            if (existing is null) { error = $"{full} has no existing parent folder"; return null; }
            using var ancestor = OpenDirectory(existing, out error);
            if (ancestor is null) return null;
            Directory.CreateDirectory(full);
        }
        return OpenDirectory(full, out error);
    }

    private static SafeFileHandle? OpenDirectory(string directory, out string? error)
    {
        // No FILE_SHARE_DELETE: nobody can rename or delete it (or, by NTFS rules, any folder
        // above it) while this is open.
        var handle = CreateFileW(directory, FileReadAttributes, FileShare.Read | FileShare.Write, IntPtr.Zero,
            FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            error = $"{directory} couldn't be opened (error {code})";
            return null;
        }
        error = CheckResolvesTo(handle, directory);
        if (error is null) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Null when the open <paramref name="handle"/> is the object at <paramref name="expectedPath"/>
    /// (compared in long-name form, ignoring case); otherwise a short reason.</summary>
    private static string? CheckResolvesTo(SafeFileHandle handle, string expectedPath)
    {
        string? actual = FinalPathOf(handle);
        if (actual is null) return $"where {expectedPath} really points couldn't be read";
        string expected = Path.TrimEndingDirectorySeparator(LongPathOf(Path.GetFullPath(expectedPath)));
        return string.Equals(Path.TrimEndingDirectorySeparator(actual), expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{expectedPath} actually resolves to {actual} (a junction, symbolic link or mount point is in the way)";
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (len >= buffer.Length)
        {
            buffer = new char[len + 1];
            len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (len == 0 || len >= buffer.Length) return null;
        string path = new(buffer, 0, (int)len);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    // GetFinalPathNameByHandle reports long names, so an 8.3 path the user saw is expanded first.
    private static string LongPathOf(string path)
    {
        var buffer = new char[1024];
        uint len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (len >= buffer.Length)
        {
            buffer = new char[len + 1];
            len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }
        return len == 0 || len >= buffer.Length ? path : new string(buffer, 0, (int)len);
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);
}
