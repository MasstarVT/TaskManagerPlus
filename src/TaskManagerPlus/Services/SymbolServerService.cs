using System.Net.Http;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, item 25: _NT_SYMBOL_PATH read/apply + symbol-server reachability check -
/// !analyze -v (DebuggerToolsService) is only as good as its symbol path, and a bad/unreachable
/// one is a common reason an analysis stalls for minutes, so this is testable up front rather
/// than discovered the hard way mid-analysis.</summary>
public static class SymbolServerService
{
    public const string SuggestedPathTemplate = @"srv*{0}*https://msdl.microsoft.com/download/symbols";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string? ReadCurrentSymbolPath()
    {
        try
        {
            return Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH", EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH", EnvironmentVariableTarget.Machine);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets _NT_SYMBOL_PATH for the current user (persists across sessions/processes,
    /// including a cdb.exe launched from this app) and for this process (so an Analyse click in
    /// the same session picks it up immediately without needing a restart).</summary>
    public static bool ApplySymbolPath(string cacheFolder)
    {
        try
        {
            string path = string.Format(SuggestedPathTemplate, cacheFolder);
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", path, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", path, EnvironmentVariableTarget.Process);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Item 25: reachability test - a plain GET against the symbol server's own root,
    /// before a long !analyze -v run stalls on a server that's actually unreachable (offline,
    /// blocked by a proxy/firewall, ...). Any response at all (even a 404/403) confirms the host
    /// is reachable, which is all this app cares about here.</summary>
    public static async Task<(bool Reachable, string Detail)> TestSymbolServerReachabilityAsync()
    {
        try
        {
            using var response = await Http.GetAsync("https://msdl.microsoft.com/download/symbols/", HttpCompletionOption.ResponseHeadersRead);
            return (true, $"Reachable (HTTP {(int)response.StatusCode}).");
        }
        catch (TaskCanceledException)
        {
            return (false, "Timed out - the symbol server didn't respond within 8 seconds.");
        }
        catch (Exception ex)
        {
            return (false, $"Unreachable: {ex.Message}");
        }
    }
}
