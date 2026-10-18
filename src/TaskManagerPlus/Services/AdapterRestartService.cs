namespace TaskManagerPlus.Services;

/// <summary>
/// Item #555: "Restart this adapter" - `netsh interface set interface "&lt;name&gt;" admin=disable`
/// immediately followed by `admin=enable`, the fastest way to bounce a NIC short of the full Device
/// Manager disable/enable round trip, useful mid-troubleshooting. Always called from behind an
/// explicit confirm dialog in the ViewModel (same MessageBox.Show confirm-first pattern
/// ReleaseSelectedAdapter/RenewAdapter already use for their own disruptive actions) since it drops
/// the connection for a few seconds - this service itself has no confirmation of its own, by design,
/// so it can't accidentally be called from anywhere that skips the prompt.
/// </summary>
public static class AdapterRestartService
{
    private const int TimeoutMs = 15000;

    public static async Task<string> RestartAsync(string adapterName)
    {
        // The re-enable always runs, even after a failed or timed-out disable - a disable that timed
        // out may still have taken effect, and enabling an already-enabled adapter is harmless.
        var (disableOk, disableOutput) = await RunNetshAsync($"interface set interface \"{adapterName}\" admin=disable");

        // A brief pause so the adapter actually finishes going down before the immediate re-enable -
        // without it, some drivers appear to just ignore the second command entirely.
        await Task.Delay(1500);

        var (enableOk, enableOutput) = await RunNetshAsync($"interface set interface \"{adapterName}\" admin=enable");
        if (!enableOk)
        {
            // A failed re-enable can leave the adapter OFF - say so plainly and give the exact
            // command, rather than a bare tool error the user can't act on.
            string disablePart = disableOk ? "The adapter was disabled, but" : $"Disabling reported: {disableOutput}. Then";
            return $"{disablePart} re-enabling it failed ({enableOutput}) - \"{adapterName}\" may still be disabled. " +
                   $"Re-enable it in Network Connections, or run: netsh interface set interface \"{adapterName}\" admin=enable";
        }
        if (!disableOk)
            return $"Couldn't disable the adapter ({disableOutput}), so it wasn't actually restarted; it is enabled.";

        string combined = $"{disableOutput}\n{enableOutput}".Trim();
        return combined.Length == 0 ? "Adapter restarted." : combined;
    }

    /// <summary>Ok is false on a timeout, a start failure or a non-zero netsh exit code; Output is
    /// then the reason. Exit code 0 with empty output is netsh's normal success.</summary>
    private static async Task<(bool Ok, string Output)> RunNetshAsync(string arguments)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("netsh.exe", arguments, TimeoutMs, timeoutOutput: "netsh.exe timed out");
            output = output.Trim();
            if (exitCode is null) return (false, output);
            if (exitCode != 0) return (false, output.Length > 0 ? output : $"netsh.exe exited with code {exitCode}");
            return (true, output);
        }
        catch (Exception ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
    }
}
