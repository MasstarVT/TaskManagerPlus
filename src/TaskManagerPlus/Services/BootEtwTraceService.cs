using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #710: one-click boot ETW trace via the Windows Performance Recorder (wpr.exe). Hidden entirely
/// from the Startup tab when wpr.exe isn't present (<see cref="IsAvailable"/>) - it ships with the
/// Windows Performance Toolkit / Windows ADK, not every Windows install. A two-step, opt-in
/// workflow, same shape as BootLogCaptureService: <see cref="ArmAsync"/> runs
/// `wpr -boottrace -addboot GeneralProfile -filemode` and records pending state (including the
/// output path decided up front) under AppPaths.SettingsDirectory; after the reboot,
/// <see cref="CollectIfPendingAsync"/> (called from StartupViewModel at startup) runs
/// `wpr -boottrace -stopboot &lt;out.etl&gt;` to collect the trace, then offers to open it in WPA.
/// Never armed silently - only ever set by an explicit button click, with <see cref="DisarmAsync"/>
/// as the always-available "cancel before rebooting" escape hatch.
/// </summary>
public static class BootEtwTraceService
{
    private static string StatePath => AppPaths.GetPath("boot-etw-trace.json");

    // wpr.exe lives in System32 when the Windows Performance Toolkit component is installed -
    // checking PATH via `where` would work too, but a direct file check is simpler and doesn't
    // need a process spawn just to answer "is this feature available".
    private static string WprPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");

    public static bool IsAvailable => File.Exists(WprPath);

    public static BootEtwTraceState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var state = JsonSerializer.Deserialize<BootEtwTraceState>(json);
                if (state is not null) return state;
            }
        }
        catch
        {
            // Corrupt/unreadable state file - treat as "not armed".
        }
        return new BootEtwTraceState();
    }

    private static void SaveState(BootEtwTraceState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best-effort - if this can't persist, the collection step next launch just won't
            // find a pending trace; nothing destructive happened.
        }
    }

    public static async Task<(bool Success, string? Error)> ArmAsync()
    {
        if (!IsAvailable) return (false, "wpr.exe not found.");

        var (output, exitCode) = await RunCapturedAsync(WprPath, "-boottrace -addboot GeneralProfile -filemode");
        if (exitCode != 0)
            return (false, string.IsNullOrWhiteSpace(output) ? "wpr failed to arm the boot trace." : output.Trim());

        string outputPath = AppPaths.GetPath("BootTraces", $"boot-trace-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
        SaveState(new BootEtwTraceState { IsArmed = true, ArmedAtUtc = DateTime.UtcNow, OutputPath = outputPath });
        return (true, null);
    }

    /// <summary>Cancels a still-pending (not yet collected) boot trace via `wpr -cancel` and
    /// clears the pending state - the "disarm before rebooting" escape hatch.</summary>
    public static async Task<(bool Success, string? Error)> DisarmAsync()
    {
        string? error = null;
        if (IsAvailable)
        {
            var (output, exitCode) = await RunCapturedAsync(WprPath, "-cancel");
            if (exitCode != 0) error = string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        SaveState(new BootEtwTraceState());
        return (true, error);
    }

    /// <summary>Called once at startup: if a trace is pending, runs `wpr -boottrace -stopboot` to
    /// collect it. Clears the pending state regardless of outcome - a failed collection attempt
    /// shouldn't retry forever on every future launch.</summary>
    public static async Task<(bool Collected, string? Error, string? OutputPath)> CollectIfPendingAsync()
    {
        var state = LoadState();
        if (!state.IsArmed || string.IsNullOrEmpty(state.OutputPath)) return (false, null, null);

        if (!IsAvailable)
        {
            SaveState(new BootEtwTraceState());
            return (false, "wpr.exe not found.", null);
        }

        try
        {
            var dir = Path.GetDirectoryName(state.OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* best-effort - wpr itself will report a real path failure below */ }

        var (output, exitCode) = await RunCapturedAsync(WprPath, $"-boottrace -stopboot \"{state.OutputPath}\"", timeoutMs: 60000);
        SaveState(new BootEtwTraceState());

        bool collected = exitCode == 0 && File.Exists(state.OutputPath);
        return collected
            ? (true, null, state.OutputPath)
            : (false, string.IsNullOrWhiteSpace(output) ? "wpr failed to collect the boot trace." : output.Trim(), null);
    }

    /// <summary>Best-effort "open in WPA" - falls back to selecting the file in Explorer when the
    /// Windows Performance Analyzer isn't on PATH, rather than failing silently or throwing.</summary>
    public static void OpenInWpa(string etlPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wpa.exe", $"\"{etlPath}\"") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{etlPath}\"") { UseShellExecute = true }); }
            catch { /* best-effort - nothing more to do if even Explorer can't be launched */ }
        }
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 20000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
