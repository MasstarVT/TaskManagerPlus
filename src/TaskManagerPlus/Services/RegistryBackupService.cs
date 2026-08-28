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
    public static async Task<(bool Success, string? Error)> RestoreKeyAsync(string regFilePath)
    {
        try
        {
            if (!File.Exists(regFilePath))
                return (false, "That backup file no longer exists.");

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "reg.exe", $"import \"{regFilePath}\"", timeoutMs: 15000);

            return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "reg import failed (unknown error)." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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
