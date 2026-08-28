using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #970: offers a System Restore checkpoint before a risky remediation action - shells out to
/// PowerShell's Checkpoint-Computer cmdlet (there's no plain .exe for this, and the underlying
/// SystemRestore WMI class's CreateRestorePoint method is what Checkpoint-Computer itself calls, so
/// shelling to the cmdlet is the "known tool" version of the same call rather than a second,
/// redundant WMI path). System Protection being off is Checkpoint-Computer's single most common
/// failure - detected here from its own reported error text, so the review dialog can report it
/// honestly (CLAUDE.md's "degrade to Unknown/hidden - never fabricate": a failed restore point
/// attempt must never be presented as if one was created) and offer a button straight to the
/// Windows System Protection settings page instead of a dead end.
/// </summary>
public static class RestorePointService
{
    /// <summary>True when Checkpoint-Computer's own error output matches its well-known "System
    /// Restore is disabled" phrasing - used to swap in a clearer, actionable message instead of the
    /// raw PowerShell exception text.</summary>
    private static bool LooksLikeSystemProtectionDisabled(string output) =>
        output.Contains("System Restore is disabled", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("disabled on this drive", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("0x81045303", StringComparison.OrdinalIgnoreCase);

    public static async Task<(bool Success, string Message, bool SystemProtectionLikelyDisabled)> CreateRestorePointAsync(string description)
    {
        try
        {
            // Checkpoint-Computer throttles to one checkpoint per 24h by default for
            // MODIFY_SETTINGS-type points on client SKUs - reported the same way as any other
            // failure below rather than special-cased, since the message it returns already says
            // so plainly.
            string safeDescription = description.Replace("'", "''");
            string psCommand =
                $"$ErrorActionPreference='Stop'; try {{ Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS; Write-Output 'RESTORE_POINT_OK' }} catch {{ Write-Output $_.Exception.Message }}";

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "powershell.exe", $"-NoProfile -NonInteractive -Command \"{psCommand.Replace("\"", "\\\"")}\"", timeoutMs: 60000);

            if (exitCode == 0 && output.Contains("RESTORE_POINT_OK", StringComparison.Ordinal))
                return (true, "Restore point created.", false);

            bool disabled = LooksLikeSystemProtectionDisabled(output);
            string message = disabled
                ? "Couldn't create a restore point - System Protection looks disabled for this drive."
                : $"Couldn't create a restore point: {(string.IsNullOrWhiteSpace(output) ? "unknown error" : output.Trim())}";
            return (false, message, disabled);
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't create a restore point: {ex.Message}", false);
        }
    }

    /// <summary>Opens the Windows "System Protection" settings tab directly - the fallback offered
    /// when CreateRestorePointAsync reports System Protection is disabled.</summary>
    public static void OpenSystemProtectionSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - if even this can't launch, there's nothing more this app can do here.
        }
    }
}
