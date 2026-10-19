namespace TaskManagerPlus.Services;

/// <summary>One Winsock catalog entry (#575), from one `netsh winsock show catalog` block.
/// <see cref="LooksNonMicrosoft"/> flags a provider DLL that isn't under the Windows System32
/// folder - every genuine Microsoft base/layered provider ships there; a third-party one almost
/// always doesn't. The classic Winsock-corruption signature this item describes is a layered
/// provider left behind pointing at a DLL an uninstalled proxy/"internet accelerator" already
/// deleted.</summary>
public sealed record WinsockProviderEntry(string EntryType, string Description, string ProviderId, string ProviderPath, bool LooksNonMicrosoft);

public sealed record WinsockCatalogResult(List<WinsockProviderEntry> Entries, int NonMicrosoftCount);

/// <summary>
/// Item #575 (suggestions.md "Proxy, PAC, VPN and Winsock"): parses `netsh winsock show catalog`
/// (the standard tool for this - the LSP catalog has no public WMI class or simple registry
/// equivalent) and flags non-Microsoft layered service providers, plus the `netsh winsock reset`
/// action - always called from behind an explicit confirm with a reboot-required warning in the
/// ViewModel (this service has no confirmation of its own, matching every other disruptive action's
/// "caller owns the prompt" convention in this app), since a winsock reset doesn't fully take effect
/// until the next restart and can briefly disrupt networking immediately after it runs.
/// </summary>
public static class WinsockService
{
    private const int TimeoutMs = 20000;
    private const int ResetTimeoutMs = 30000;

    public static async Task<WinsockCatalogResult> ReadCatalogAsync()
    {
        var (output, exitCode) = await RunNetshAsync("winsock show catalog", TimeoutMs);
        // Only a completed run is parsed - an error/failure message containing ':' would otherwise
        // turn into a bogus "provider" row.
        var entries = exitCode == 0 ? Parse(output) : new List<WinsockProviderEntry>();
        return new WinsockCatalogResult(entries, entries.Count(e => e.LooksNonMicrosoft));
    }

    /// <summary>Success only on exit code 0 - a timeout, a start failure or a netsh error (e.g.
    /// "requires elevation") used to come back as empty output and was reported as a completed
    /// reset.</summary>
    public static async Task<(bool Success, string Message)> ResetAsync()
    {
        var (output, exitCode) = await RunNetshAsync("winsock reset", ResetTimeoutMs);
        if (exitCode is null)
            return (false, output.Length > 0 ? $"Winsock reset failed: {output}" : "Winsock reset timed out - it may not have completed. Check `netsh winsock show catalog` before restarting.");
        if (exitCode != 0)
            return (false, output.Length > 0 ? $"Winsock reset failed (exit code {exitCode}): {output}" : $"Winsock reset failed (exit code {exitCode}).");
        return (true, output.Length == 0 ? "Winsock catalog reset. Restart Windows for this to fully take effect." : output);
    }

    private static List<WinsockProviderEntry> Parse(string output)
    {
        var results = new List<WinsockProviderEntry>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (block.Count == 0) return;
            string path = block.GetValueOrDefault("Provider Path", string.Empty);
            results.Add(new WinsockProviderEntry(
                block.GetValueOrDefault("Entry Type", "Unknown"),
                block.GetValueOrDefault("Description", "(unnamed)"),
                block.GetValueOrDefault("Provider ID", string.Empty),
                path,
                LooksNonMicrosoft(path)));
            block.Clear();
        }

        // The provider entries are followed by "Name Space Provider Entry" blocks (no Entry Type or
        // Provider Path) - skipped, or each would become a bogus "Unknown" provider row, or overwrite
        // the last real one's Description where no blank line separates them.
        bool inNameSpaceBlock = false;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            if (line.Contains("Winsock Catalog Provider Entry", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                inNameSpaceBlock = false;
                continue;
            }
            if (line.Contains("Name Space Provider Entry", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                inNameSpaceBlock = true;
                continue;
            }
            if (inNameSpaceBlock) continue;

            int idx = line.IndexOf(':');
            if (idx < 0) continue; // the "----" separator
            string key = line[..idx].Trim();
            string value = line[(idx + 1)..].Trim();
            if (key.Length == 0) continue;
            block[key] = value;
        }
        Flush();

        return results;
    }

    private static bool LooksNonMicrosoft(string providerPath)
    {
        if (string.IsNullOrWhiteSpace(providerPath)) return false; // no path at all - not enough to flag
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(providerPath);
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return !expanded.StartsWith(system32, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A null ExitCode means the run didn't complete: timed out (Output empty) or
    /// couldn't start (Output is the reason).</summary>
    private static async Task<(string Output, int? ExitCode)> RunNetshAsync(string arguments, int timeoutMs)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("netsh.exe", arguments, timeoutMs, timeoutOutput: string.Empty);
            return (output.Trim(), exitCode);
        }
        catch (Exception ex)
        {
            return ($"couldn't run netsh.exe ({ex.Message})", null);
        }
    }
}
