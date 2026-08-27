namespace TaskManagerPlus.Models;

/// <summary>One installed browser extension/add-on (#19) - a common, currently invisible source
/// of "slow startup"/"slow browsing" complaints that the registry-Run/Startup-folder/Scheduled-
/// Tasks scans elsewhere on this tab don't cover at all, since extensions aren't a Windows startup
/// mechanism, they're a per-browser one. See Services/BrowserExtensionService.</summary>
public sealed class BrowserExtensionInfo
{
    public string Browser { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
}
