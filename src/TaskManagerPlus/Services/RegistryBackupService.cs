using System.IO;
using System.Text;

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
    /// <summary>Exports `keyPath` (a full, hive-qualified path like
    /// "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run") to
    /// AppPaths.SettingsDirectory\Undo\&lt;timestamp&gt;\&lt;safe-name&gt;.reg. Returns the saved
    /// file's path on success - null on failure (reg.exe missing, key inaccessible, ...), with a
    /// human-readable error rather than a thrown exception, matching every other Services/*.cs
    /// mutation method's (bool, string?) shape in this app.</summary>
    public static async Task<(bool Success, string? Path, string? Error)> BackupKeyAsync(string keyPath)
    {
        try
        {
            string safeName = SanitizeFileName(keyPath);
            string dir = AppPaths.GetPath("Undo", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(dir);
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
    /// <remarks>The backup path comes from change-journal.jsonl and the file sits in the user's
    /// own %AppData%, both writable by any unelevated process, while reg import runs elevated. So
    /// the file must be under Undo\ with no junctions, every key it names must be one this app
    /// backs up, and what reg.exe imports is a copy of the checked text in an admin-only folder
    /// (it re-reads by path, so importing the original would let it change after the check).</remarks>
    public static async Task<(bool Success, string? Error)> RestoreKeyAsync(string regFilePath)
    {
        string? importDir = null;
        FileStream? pin = null;
        try
        {
            string undoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.GetPath("Undo")));
            if (!Path.IsPathFullyQualified(regFilePath) || !regFilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(regFilePath).StartsWith(undoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (false, "That isn't a backup in this app's Undo folder - not importing it.");
            string fullPath = Path.GetFullPath(regFilePath);

            if (!File.Exists(fullPath))
                return (false, "That backup file no longer exists.");
            if (QuarantineService.FindReparsePoint(fullPath) is { } reparse)
                return (false, $"Not importing: {reparse}.");

            string content = await File.ReadAllTextAsync(fullPath);
            if (ValidateBackupContent(content) is { } refusal)
                return (false, refusal);

            importDir = Path.Combine(undoRoot, "import-" + Guid.NewGuid().ToString("N"));
            var dir = QuarantineService.CreateAdminOnlyDirectory(importDir);
            // An open file inside stops the folder being renamed and swapped for a lookalike.
            pin = new FileStream(Path.Combine(importDir, "in-use"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if ((QuarantineService.FindReparsePoint(importDir) ?? QuarantineService.CheckAdminOnly(dir)) is { } problem)
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
