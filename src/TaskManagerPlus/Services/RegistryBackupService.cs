using System.IO;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #971: registry key backup/restore via `reg export`/`reg import` - the same "known Windows tool,
/// not raw registry-API interop" tradeoff every other shelled-out tool in this app takes (see
/// CLAUDE.md). Used only by #967's remediation-review flow, ahead of an action whose
/// RemediationAction.RegistryKeyToBackup is set - never wired into StartupManagerService/etc.
/// themselves, so those stay UI-agnostic and reusable outside the remediation flow, per the task's
/// own guidance.
/// </summary>
public static class RegistryBackupService
{
    private const string AreaName = "Undo";

    /// <summary>Exports `keyPath` (a full, hive-qualified path like
    /// "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run") to
    /// %ProgramData%\TaskManagerPlus\Undo\&lt;timestamp&gt;\&lt;safe-name&gt;.reg (AdminDataPaths'
    /// admin-only area, so neither reg.exe's write nor a later import can be redirected or fed a
    /// swapped file). Returns the saved file's path on success - null on failure (reg.exe missing,
    /// key inaccessible, no admin-only folder, ...), with a human-readable error rather than a
    /// thrown exception, matching every other Services/*.cs mutation method's (bool, string?)
    /// shape in this app.</summary>
    public static async Task<(bool Success, string? Path, string? Error)> BackupKeyAsync(string keyPath)
    {
        try
        {
            string safeName = SanitizeFileName(keyPath);
            string? undoRoot = AdminDataPaths.TryGetArea(AreaName, out string? areaError);
            if (undoRoot is null) return (false, null, $"Couldn't prepare a protected backup folder ({areaError}).");
            string dir = Path.Combine(undoRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            // A same-second folder that already exists must pass the same check. Owner-readable,
            // so the user who owns the .reg reg.exe writes can't re-ACL it and rewrite it.
            var backupDir = QuarantineService.CreateOwnerReadableDirectory(dir);
            if (QuarantineService.CheckAdminOnly(backupDir) is { } problem)
                return (false, null, $"Couldn't create a protected backup folder ({problem}).");
            string filePath = Path.Combine(dir, safeName + ".reg");

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "reg.exe", $"export \"{keyPath}\" \"{filePath}\" /y", timeoutMs: 15000);

            if (exitCode == 0 && File.Exists(filePath))
                return (true, filePath, null);

            return (false, null, string.IsNullOrWhiteSpace(output) ? "reg export failed (unknown error)." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>`reg import "&lt;path&gt;"` - restores a previously exported key from
    /// BackupKeyAsync, used by #973's Undo panel as a secondary restore option alongside a
    /// journal entry's primary same-service-method inverse call.</summary>
    /// <remarks>The backup path comes from change-journal.jsonl, which any unelevated process can
    /// write, while reg import runs elevated. So the entry must carry this app's MAC
    /// (JournalIntegrityService), the file must be in the admin-only Undo area, every key it names
    /// must be one this app backs up, and what reg.exe imports is a copy of the checked text.
    /// Backups an earlier version wrote under the settings folder (user-writable) are refused
    /// with the folder named, for the user to import by hand.</remarks>
    public static async Task<(bool Success, string? Error)> RestoreKeyAsync(ChangeJournalEntry entry)
    {
        string? importDir = null;
        FileStream? pin = null;
        try
        {
            if (ReadVerifiedBackup(entry, out _, out string content) is { } refusal)
                return (false, refusal);
            string undoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AdminDataPaths.RootPath, AreaName)));

            importDir = Path.Combine(undoRoot, "import-" + Guid.NewGuid().ToString("N"));
            var dir = QuarantineService.CreateOwnerReadableDirectory(importDir);
            pin = new FileStream(Path.Combine(importDir, "in-use"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (QuarantineService.CheckAdminOnly(dir) is { } problem)
                return (false, $"Couldn't prepare a protected copy to import ({problem}).");

            string copyPath = Path.Combine(importDir, "restore.reg");
            await File.WriteAllTextAsync(copyPath, content, Encoding.Unicode);

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "reg.exe", $"import \"{copyPath}\"", timeoutMs: 15000);

            return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "reg import failed (unknown error)." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            pin?.Dispose();
            if (importDir is not null)
            {
                try { Directory.Delete(importDir, recursive: true); } catch { /* best-effort cleanup of the app's own copy */ }
            }
        }
    }

    /// <summary>For the confirmation before RestoreKeyAsync: the backup's path and every key and
    /// value line reg import would write, after the same checks the restore runs. Null
    /// Description (with the reason) when the restore would refuse.</summary>
    public static (string? Description, string? Error) DescribeBackup(ChangeJournalEntry entry)
    {
        try
        {
            if (ReadVerifiedBackup(entry, out string fullPath, out string content) is { } refusal)
                return (null, refusal);
            var lines = content.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[') || l.StartsWith('"') || l.StartsWith('@')).ToList();
            var shown = lines.Take(25).Select(l => l.Length > 160 ? l[..160] + "…" : l);
            string more = lines.Count > 25 ? $"\n(+{lines.Count - 25} more line(s))" : string.Empty;
            return ($"Backup: {fullPath}\n\n{string.Join("\n", shown)}{more}", null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Null with the backup's verified path and text, or the reason it won't be used.</summary>
    private static string? ReadVerifiedBackup(ChangeJournalEntry entry, out string fullPath, out string content)
    {
        fullPath = string.Empty;
        content = string.Empty;
        if (entry.RegistryBackupPath is not { Length: > 0 } regFilePath)
            return "This entry has no registry backup.";
        if (!ChangeJournalService.Verify(entry))
            return JournalIntegrityService.UnverifiedMessage;

        if (!Path.IsPathFullyQualified(regFilePath) || !regFilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            return "That isn't a backup in this app's Undo folder - not importing it.";
        fullPath = Path.GetFullPath(regFilePath);

        string legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.GetPath(AreaName)));
        if (fullPath.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"This backup was written by an earlier version into {legacyRoot}, a folder other programs running as you can change, so it won't be imported automatically. If you trust it, import \"{fullPath}\" by hand.";

        string undoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AdminDataPaths.RootPath, AreaName)));
        if (!fullPath.StartsWith(undoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "That isn't a backup in this app's Undo folder - not importing it.";
        if (AdminDataPaths.TryGetArea(AreaName, out string? areaError) is null)
            return $"Not importing: {areaError}.";

        if (!File.Exists(fullPath))
            return "That backup file no longer exists.";
        string backupFolder = Path.GetDirectoryName(fullPath)!;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(backupFolder) & FileAttributes.ReparsePoint) != 0)
            return "Not importing: the backup is a junction or symbolic link.";
        if (QuarantineService.CheckAdminOnly(new DirectoryInfo(backupFolder)) is { } unsafeReason)
            return $"Not importing: {unsafeReason}.";

        content = File.ReadAllText(fullPath);
        return ValidateBackupContent(content);
    }

    // BackupKeyAsync's only caller is RemediationActionCatalog.DisableStartupItem, which backs up
    // one of these StartupApproved keys - nothing else is ever imported back.
    private static readonly string[] RestorableKeys =
    {
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
        @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
    };

    /// <summary>Null when the .reg text is a regedit-5 file whose every key header is exactly one
    /// of <see cref="RestorableKeys"/> (no key deletions); otherwise the refusal text. Any line
    /// starting with '[' is treated as a header, so a header can't hide as a continuation line.</summary>
    private static string? ValidateBackupContent(string content)
    {
        var lines = content.Split('\n').Select(l => l.Trim()).ToList();
        string? first = lines.FirstOrDefault(l => l.Length > 0);
        if (first is null || !first.TrimStart('﻿').Equals("Windows Registry Editor Version 5.00", StringComparison.Ordinal))
            return "That file isn't a registry export - not importing it.";

        bool sawKey = false;
        foreach (string line in lines)
        {
            if (!line.StartsWith('[')) continue;
            string key = line.EndsWith(']') ? line[1..^1] : string.Empty;
            if (!RestorableKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return $"That backup touches a registry key this app doesn't back up ({line}) - not importing it.";
            sawKey = true;
        }
        return sawKey ? null : "That backup names no registry key - nothing to import.";
    }

    private static string SanitizeFileName(string raw)
    {
        var sb = new StringBuilder();
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "key" : s;
    }
}
