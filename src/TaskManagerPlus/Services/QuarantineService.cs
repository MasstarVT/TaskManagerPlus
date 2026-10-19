using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #899(c): "move (never shred) files the user chooses to remove into a timestamped
/// quarantine folder." Quarantine always File.Move's (never File.Delete's) into
/// AppPaths.SettingsDirectory\Quarantine\&lt;yyyyMMdd-HHmmss&gt;\&lt;originalFileName&gt; - Restore
/// reverses that exact move. Deliberately separate from DefenderService's own quarantine browser
/// (#863, which reads/restores/purges Windows Defender's OWN quarantine store) - this is this
/// app's own, much simpler "get a suspicious file out of its running location without destroying
/// evidence" mechanism for files the Persistence grid flags, not a Defender feature.
///
/// The settings folder and the action journal are writable by any unelevated process running as
/// the user, while Restore moves files as Administrator. So each timestamp folder is created
/// admin-only and holds a record of where its file came from; Restore trusts only that record,
/// never the journal's paths on their own.
/// </summary>
public static class QuarantineService
{
    private const string OriginRecordName = "quarantine-origin.txt";

    public static (bool Success, string? QuarantinePath, string? Error) Quarantine(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return (false, null, "File not found - it may have already been moved or deleted.");

            string root = AppPaths.GetPath("Quarantine");
            Directory.CreateDirectory(root);
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
            // Re-check: a lookalike folder could have been planted between the Exists test and Create.
            if ((FindReparsePoint(dir.FullName) ?? CheckAdminOnly(dir)) is { } problem)
                return (false, null, $"Couldn't create a protected quarantine folder ({problem}).");

            string name = Path.GetFileName(filePath);
            if (name.Equals(OriginRecordName, StringComparison.OrdinalIgnoreCase)) name = "_" + name;
            string finalDestination = Path.Combine(folder, name);
            File.WriteAllText(Path.Combine(folder, OriginRecordName), $"{name}\n{Path.GetFullPath(filePath)}\n");

            File.Move(filePath, finalDestination);

            // Across volumes File.Move is copy-then-delete, and Windows reports success even when
            // the delete fails (a running or locked binary) - the original would still be in place.
            if (File.Exists(filePath))
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
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.GetPath("Quarantine")));
            if (folder is null || !string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
                return (false, "That file isn't in this app's Quarantine folder - not restoring.");

            if (!File.Exists(fullQuarantine)) return (false, "Quarantined file not found - it may have already been restored or removed.");

            string recordPath = Path.Combine(folder, OriginRecordName);
            if (!File.Exists(recordPath))
                return (false, "This file was quarantined before restores were tied to a protected record, so it won't be moved back automatically. " + byHand);

            // Windows won't rename a folder (or any parent of it) while a file inside is open, so
            // holding the record open pins the checked folder until the move is done.
            using var record = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if ((FindReparsePoint(fullQuarantine) ?? FindReparsePoint(recordPath) ?? CheckAdminOnly(new DirectoryInfo(folder))) is { } problem)
                return (false, $"Not restoring: {problem}. " + byHand);

            using var reader = new StreamReader(record, leaveOpen: true);
            string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2
                || !lines[0].Equals(Path.GetFileName(fullQuarantine), StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(lines[1])
                || !string.Equals(Path.GetFullPath(lines[1]), Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
                return (false, "The journal entry doesn't match this file's quarantine record - not restoring.");

            if (File.Exists(originalPath)) return (false, "A file already exists at the original path - not overwriting it.");

            string? originalDir = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(originalDir)) Directory.CreateDirectory(originalDir);

            File.Move(fullQuarantine, originalPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- admin-only folder helpers (also used by RegistryBackupService's restore) ----

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

    /// <summary>Null when neither <paramref name="path"/> nor any folder above it, up to and
    /// including AppPaths.SettingsDirectory, is a junction/symlink; otherwise a short reason.
    /// A path outside the settings folder is a reason too.</summary>
    internal static string? FindReparsePoint(string path)
    {
        string settings = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.SettingsDirectory));
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!current.StartsWith(settings + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !current.Equals(settings, StringComparison.OrdinalIgnoreCase))
            return $"{path} is outside this app's settings folder";

        while (true)
        {
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return $"{current} is a junction or symbolic link";
            }
            if (current.Equals(settings, StringComparison.OrdinalIgnoreCase)) return null;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null) return null;
            current = parent;
        }
    }
}
