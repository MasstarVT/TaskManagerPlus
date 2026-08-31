using System.Diagnostics;

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
        string output = await RunNetshAsync("winsock show catalog", TimeoutMs);
        var entries = Parse(output);
        return new WinsockCatalogResult(entries, entries.Count(e => e.LooksNonMicrosoft));
    }

    public static async Task<string> ResetAsync()
    {
        string output = await RunNetshAsync("winsock reset", ResetTimeoutMs);
        return output.Length == 0 ? "Winsock catalog reset. Restart Windows for this to fully take effect." : output;
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

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            int idx = line.IndexOf(':');
            if (idx < 0) continue; // the "Winsock Catalog Provider Entry" header line, or the "----" separator
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

    private static async Task<string> RunNetshAsync(string arguments, int timeoutMs)
    {
        try
        {
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh.exe", arguments, timeoutMs, timeoutOutput: string.Empty);
            return output.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
