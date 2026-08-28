using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #709: "Capture a boot log" - a two-step, opt-in workflow. Step one (<see cref="ArmAsync"/>)
/// runs `bcdedit /set {current} bootlog yes` and records a small pending-state JSON file under
/// AppPaths.SettingsDirectory (same shape as PollIntervalSettingsService) so the app knows to look
/// for a freshly captured log on its next launch, after the user reboots. Step two
/// (<see cref="ReadAndParseLog"/>, called from StartupViewModel at startup when the pending flag
/// is set) parses %windir%\ntbtlog.txt for every "Loaded driver"/"Did not load driver" line, in
/// order, then offers <see cref="DisarmAsync"/> to flip the bcdedit flag back off. Never armed
/// silently - only ever set by an explicit button click on the Startup tab, and the pending state
/// is always visible (and reversible via Disarm) before the user reboots.
/// </summary>
public static class BootLogCaptureService
{
    private static string StatePath => AppPaths.GetPath("boot-log-capture.json");

    public static BootLogCaptureState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var state = JsonSerializer.Deserialize<BootLogCaptureState>(json);
                if (state is not null) return state;
            }
        }
        catch
        {
            // Corrupt/unreadable state file - treat as "not armed" rather than blocking the tab.
        }
        return new BootLogCaptureState();
    }

    private static void SaveState(BootLogCaptureState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best-effort - if we can't persist the pending flag, the workflow just won't
            // auto-detect the log on next launch; nothing destructive happened.
        }
    }

    /// <summary>Step one: turns on boot logging and records the pending state. The caller is
    /// responsible for prompting the user to restart - this only arms the flag.</summary>
    public static async Task<(bool Success, string? Error)> ArmAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", "/set {current} bootlog yes");
        if (exitCode != 0)
            return (false, string.IsNullOrWhiteSpace(output) ? "bcdedit failed." : output.Trim());

        SaveState(new BootLogCaptureState { IsArmed = true, ArmedAtUtc = DateTime.UtcNow });
        return (true, null);
    }

    /// <summary>Turns boot logging back off and clears the pending state - offered both as an
    /// explicit "disarm before rebooting" escape hatch and as the "turn the flag back off" step
    /// after a capture has been parsed.</summary>
    public static async Task<(bool Success, string? Error)> DisarmAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", "/set {current} bootlog no");
        SaveState(new BootLogCaptureState());
        return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "bcdedit failed." : output.Trim());
    }

    /// <summary>Step two: parses %windir%\ntbtlog.txt, only when it was written after the capture
    /// was armed (an older, stale log from a previous unrelated capture shouldn't be reported as
    /// "this boot's" result). Returns null when the file doesn't exist yet or is stale - both mean
    /// "nothing to show yet," not an error.</summary>
    public static NtbtlogResult? ReadAndParseLog(DateTime armedAtUtc)
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ntbtlog.txt");
            if (!File.Exists(path)) return null;

            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc < armedAtUtc) return null;

            var entries = new List<NtbtlogEntry>();
            int order = 0;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Did not load driver", StringComparison.OrdinalIgnoreCase))
                {
                    order++;
                    entries.Add(new NtbtlogEntry { Order = order, Loaded = false, DriverPath = trimmed["Did not load driver".Length..].Trim() });
                }
                else if (trimmed.StartsWith("Loaded driver", StringComparison.OrdinalIgnoreCase))
                {
                    order++;
                    entries.Add(new NtbtlogEntry { Order = order, Loaded = true, DriverPath = trimmed["Loaded driver".Length..].Trim() });
                }
            }

            return new NtbtlogResult { CapturedAtUtc = info.LastWriteTimeUtc, Entries = entries };
        }
        catch
        {
            // File locked/unreadable/vanished mid-read - "nothing parsed yet" rather than an error.
            return null;
        }
    }

    /// <summary>Same concurrent-read/bounded-wait/kill-on-timeout shelling-out pattern
    /// ScheduledTaskService.RunCapturedAsync already establishes - copied locally, the same
    /// "each shelling-out service owns its own small helper" convention used throughout this
    /// codebase (see ScheduledTaskService, TracerouteService, PowerPlanService).</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
