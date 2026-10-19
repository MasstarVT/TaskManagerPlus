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
///
/// A same-volume move keeps the file's own permissions, which usually let the user (and so the
/// program being quarantined) rewrite or delete it. So the quarantined file is re-owned by
/// Administrators and made to inherit the folder's admin-only permissions; its original owner and
/// permissions go in the origin record and are put back on Restore. Restore refuses a file whose
/// permissions aren't admin-only, since it may no longer be what was quarantined.
/// </summary>
public static class QuarantineService
{
    private const string OriginRecordName = "quarantine-origin.txt";
    private const string AreaName = "Quarantine";
    private const AccessControlSections OwnerAndAccess = AccessControlSections.Owner | AccessControlSections.Access;

    /// <summary>On success Error may still carry a note for the user (the permissions couldn't be
    /// locked down, or a file has appeared at the original path since the move).</summary>
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
            try { dir = CreateOwnerReadableDirectory(folder); }
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
            // Read through the open handle, so it's this file's owner and permissions.
            string originalSecurity = string.Empty;
            try { originalSecurity = source.GetAccessControl().GetSecurityDescriptorSddlForm(OwnerAndAccess); }
            catch { /* Restore then leaves the permissions as they are */ }
            string recordPath = Path.Combine(folder, OriginRecordName);
            File.WriteAllText(recordPath, $"{name}\n{fullSource}\n{originalSecurity}\n");

            try { File.Move(fullSource, finalDestination); }
            catch
            {
                try { File.Delete(recordPath); } catch { /* best-effort */ }
                throw;
            }

            // A same-volume move is a rename: the open handle follows the file into the folder.
            // Across volumes File.Move is copy-then-delete, and Windows reports success even when
            // the delete fails (a running or locked binary) - the handle then still names the
            // original, in place and not being deleted, and the quarantined file is only a copy.
            // Anything else at the original path afterwards is a new file, never a reason to
            // delete the quarantined original.
            bool renamed = CheckResolvesTo(source.SafeFileHandle, finalDestination) is null;
            if (!renamed && IsStillInPlace(source.SafeFileHandle, fullSource))
            {
                try { File.Delete(finalDestination); File.Delete(recordPath); } catch { /* best-effort - leave the copy rather than fail differently */ }
                return (false, null, "The file was copied but the original couldn't be removed (it may be running or locked), so nothing was quarantined.");
            }
            // Closed before the check below: a deleted original keeps its name until then on some
            // file systems.
            source.Dispose();

            var notes = new List<string>();
            if (LockDown(finalDestination) is { } lockProblem)
                notes.Add($"Its permissions couldn't be locked down ({lockProblem}), so programs running as you may still be able to change or remove it - it won't be moved back automatically.");
            if (File.Exists(fullSource))
                notes.Add(renamed
                    ? "A new file has appeared at the original path since the move (something re-created it); it was left alone."
                    : "A file is still at the original path after the move (something may have re-created it, or the original couldn't be fully removed); it was left alone.");
            return (true, finalDestination, notes.Count > 0 ? string.Join(" ", notes) : null);
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
            // A file whose permissions let other programs change it (quarantined by an earlier
            // version, or the lock-down failed) may not be what was quarantined.
            if (CheckAdminOnly(new FileInfo(fullQuarantine)) is { } fileProblem)
                return (false, $"Not restoring: {fileProblem}, so it may have been changed since it was quarantined. " + byHand);

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

            // Put the original owner and permissions back while the file is still in the
            // admin-only folder (not by path once it's back in a folder the user can change).
            // Inherited entries are kept as explicit ones, so the effective access is the original.
            if (lines.Length > 2) RestoreSecurity(fullQuarantine, lines[2]);

            try { File.Move(fullQuarantine, fullOriginal); }
            catch
            {
                if (File.Exists(fullQuarantine)) LockDown(fullQuarantine);
                throw;
            }
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
        // OWNER RIGHTS (S-1-3-4): files the elevated app creates in here are owned by the user
        // account under the default "object creator" owner policy, and an owner implicitly holds
        // WRITE_DAC - which would let an unelevated same-user process re-ACL and rewrite them.
        // An explicit OWNER RIGHTS entry replaces that implicit grant with read-only.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var dir = new DirectoryInfo(path);
        dir.Create(security);
        return dir;
    }

    /// <summary>Like <see cref="CreateAdminOnlyDirectory"/>, but readable only by the account
    /// running this app rather than every Authenticated User - for data copied out of admin-only
    /// places (minidumps, event logs, quarantined files) or not yet scrubbed, which used to sit in
    /// that user's own profile. The OWNER RIGHTS entry takes away the implicit WRITE_DAC the user
    /// would otherwise hold on files the elevated app creates inside (they're owned by the user
    /// under Windows' default "object creator" owner policy). Passes CheckAdminOnly.</summary>
    /// <remarks><paramref name="alsoReadableBy"/>: one more account given read access - the
    /// signed-in user, for a file handed to their (unelevated) shell when an administrator's
    /// credentials were used to elevate this app.</remarks>
    internal static DirectoryInfo CreateOwnerReadableDirectory(string path, SecurityIdentifier? alsoReadableBy = null)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (alsoReadableBy is not null && alsoReadableBy != identity.User)
                security.AddAccessRule(new FileSystemAccessRule(alsoReadableBy, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        var dir = new DirectoryInfo(path);
        dir.Create(security);
        return dir;
    }

    /// <summary>Null when the folder is owned by, and modifiable only by, SYSTEM/Administrators/
    /// TrustedInstaller - i.e. an unelevated process can't have created it or planted files in it.
    /// Otherwise a short reason. Fails closed on an unreadable ACL.</summary>
    internal static string? CheckAdminOnly(DirectoryInfo dir) => CheckAdminOnly(dir, () => dir.GetAccessControl(OwnerAndAccess));

    /// <summary>The same check for one file (the write rights are the same bits).</summary>
    private static string? CheckAdminOnly(FileInfo file) => CheckAdminOnly(file, () => file.GetAccessControl(OwnerAndAccess));

    private static string? CheckAdminOnly(FileSystemInfo item, Func<FileSystemSecurity> read)
    {
        try
        {
            var security = read();
            if (!IsTrustedSid(security.GetOwner(typeof(SecurityIdentifier))))
                return $"{item.FullName} isn't owned by Administrators";
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((rule.FileSystemRights & DirectoryWriteRights) == 0) continue;
                if (!IsTrustedSid(rule.IdentityReference)) return $"{item.FullName} is writable by a non-administrator";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"the permissions on {item.FullName} couldn't be read ({ex.Message})";
        }
    }

    // ---- the quarantined file's own permissions ----

    /// <summary>Drops the permissions <paramref name="path"/> brought with it (an empty, inheriting
    /// list, so it takes the admin-only folder's), then makes Administrators its owner. Null when
    /// the result passes CheckAdminOnly; otherwise a short reason.</summary>
    private static string? LockDown(string path)
    {
        var file = new FileInfo(path);
        try
        {
            var inherit = new FileSecurity();
            inherit.SetSecurityDescriptorSddlForm("D:", AccessControlSections.Access);
            file.SetAccessControl(inherit);
            // After the reset Administrators hold full control, so the owner change is allowed.
            var owner = new FileSecurity();
            owner.SetOwner(AdministratorsSid);
            file.SetAccessControl(owner);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        return CheckAdminOnly(file);
    }

    /// <summary>Best-effort: puts back the owner and permissions recorded at quarantine time.
    /// Owner first (Administrators still hold full control then); an owner Windows won't let this
    /// app assign is left as Administrators. Entries the file had inherited become explicit, so
    /// its effective access matches the original wherever it lands.</summary>
    private static void RestoreSecurity(string path, string sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return;
        var file = new FileInfo(path);
        FileSecurity original;
        try
        {
            original = new FileSecurity();
            original.SetSecurityDescriptorSddlForm(sddl, OwnerAndAccess);
        }
        catch
        {
            return;
        }
        try
        {
            if (original.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier ownerSid)
            {
                var owner = new FileSecurity();
                owner.SetOwner(ownerSid);
                file.SetAccessControl(owner);
            }
        }
        catch { /* keep Administrators as owner */ }
        try
        {
            var access = new FileSecurity();
            access.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            file.SetAccessControl(access);
        }
        catch { /* the file keeps the quarantine folder's permissions */ }
    }

    /// <summary>True when the open <paramref name="handle"/> still names <paramref name="path"/>
    /// and the file isn't being deleted - i.e. a cross-volume move left the original in place.
    /// False whenever that can't be shown.</summary>
    private static bool IsStillInPlace(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo info, (uint)Marshal.SizeOf<FileStandardInfo>()))
            return false;
        return info.DeletePending == 0 && info.NumberOfLinks > 0 && CheckResolvesTo(handle, path) is null;
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
        if (Directory.Exists(full)) return OpenDirectory(full, out error);

        // Missing folders are created one level at a time inside a pinned, checked parent, and
        // each new one is pinned and checked before the next is made inside it - a fresh folder
        // swapped for a junction is caught before anything is created through it.
        var missing = new Stack<string>();
        string? existing = full;
        while (existing is not null && !Directory.Exists(existing))
        {
            missing.Push(existing);
            existing = Path.GetDirectoryName(existing);
        }
        if (existing is null) { error = $"{full} has no existing parent folder"; return null; }

        var parent = OpenDirectory(existing, out error);
        if (parent is null) return null;
        while (missing.Count > 0)
        {
            string level = missing.Pop();
            try { Directory.CreateDirectory(level); }
            catch (Exception ex)
            {
                parent.Dispose();
                error = $"{level} couldn't be created ({ex.Message})";
                return null;
            }
            var child = OpenDirectory(level, out error);
            parent.Dispose();
            if (child is null) return null;
            parent = child;
        }
        return parent;
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
    private const int FileStandardInfoClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        out FileStandardInfo lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);
}
