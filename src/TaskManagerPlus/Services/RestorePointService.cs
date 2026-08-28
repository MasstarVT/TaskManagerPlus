using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #899(a): "Create a System Restore point first" - an opt-in checkbox shown before any
/// change-making action in the #899 cleanup workflow, calling `Checkpoint-Computer` via
/// PowerShell. System Restore can be disabled entirely on a given machine (common on SSDs/some
/// OEM images) - this degrades gracefully with a clear message on failure and NEVER blocks the
/// actual action the user asked for, per the item's own explicit instruction.
/// </summary>
public static class RestorePointService
{
    public static async Task<(bool Success, string? Error)> TryCreateRestorePointAsync(string description)
    {
        try
        {
            string safeDescription = description.Replace("\"", "'");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description \\\"{safeDescription}\\\" -RestorePointType MODIFY_SETTINGS\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't start powershell.exe");

            string output = await proc.StandardOutput.ReadToEndAsync();
            string error = await proc.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => proc.WaitForExit(60_000));
            if (!exited)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "Checkpoint-Computer timed out (System Restore may be disabled, or Windows' own once-per-24-hours throttle applies to MODIFY_SETTINGS points).");
            }

            if (proc.ExitCode == 0) return (true, null);

            string combined = (error + " " + output).Trim();
            return (false, combined.Length > 0 ? combined : "Checkpoint-Computer failed - System Restore may be disabled for this drive.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
