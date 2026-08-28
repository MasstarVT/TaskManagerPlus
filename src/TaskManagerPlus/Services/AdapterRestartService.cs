using System.Diagnostics;

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
        string disableOutput = await RunNetshAsync($"interface set interface \"{adapterName}\" admin=disable");

        // A brief pause so the adapter actually finishes going down before the immediate re-enable -
        // without it, some drivers appear to just ignore the second command entirely.
        await Task.Delay(1500);

        string enableOutput = await RunNetshAsync($"interface set interface \"{adapterName}\" admin=enable");

        string combined = $"{disableOutput}\n{enableOutput}".Trim();
        return combined.Length == 0 ? "Adapter restarted." : combined;
    }

    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start netsh.exe.";

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "netsh.exe timed out.";
            }

            string output = ((await outputTask) + (await errorTask)).Trim();
            return output;
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }
}
