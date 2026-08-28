namespace TaskManagerPlus.Models;

/// <summary>One installed browser extension/add-on (#19) - a common, currently invisible source
/// of "slow startup"/"slow browsing" complaints that the registry-Run/Startup-folder/Scheduled-
/// Tasks scans elsewhere on this tab don't cover at all, since extensions aren't a Windows startup
/// mechanism, they're a per-browser one. See Services/BrowserExtensionService.
///
/// #898 (Round 20) extended this with the permission-surface fields below, all read from the SAME
/// manifest.json this service already parses for Name/Version - "which extensions can read every
/// page I visit" is one click (sort by HasAllUrlsAccess) per that item's own text.</summary>
public sealed class BrowserExtensionInfo
{
    public string Browser { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;

    /// <summary>manifest.json's "permissions" array, verbatim (e.g. "tabs", "storage",
    /// "webRequest") - empty for Firefox extensions or a manifest that doesn't declare any.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>manifest.json's "host_permissions" array (Manifest V3) - falls back to any
    /// URL-shaped entries already present in Permissions for Manifest V2, which folds host
    /// patterns into the same "permissions" array.</summary>
    public IReadOnlyList<string> HostPermissions { get; init; } = Array.Empty<string>();

    /// <summary>True when Permissions or HostPermissions contains "&lt;all_urls&gt;" or a
    /// match-all-hosts pattern ("*://*/*", "http://*/*", "https://*/*") - "can read every page you
    /// visit," the specific thing #898's own text calls out as worth sorting by.</summary>
    public bool HasAllUrlsAccess { get; init; }

    /// <summary>Other individually-notable permissions found (webRequest/nativeMessaging/
    /// debugger/cookies) - shown as a short comma-joined list, empty when none apply.</summary>
    public IReadOnlyList<string> SensitivePermissions { get; init; } = Array.Empty<string>();

    /// <summary>Comma-joined SensitivePermissions, for a plain-text DataGrid column binding
    /// without needing a value converter - empty string when none apply.</summary>
    public string SensitivePermissionsText => string.Join(", ", SensitivePermissions);

    /// <summary>"Web Store" / "Policy-installed" / "Unknown source" - a REASONABLE, explicitly
    /// approximate proxy (see BrowserExtensionService.DetermineInstallSource's remarks), never a
    /// guessed "sideloaded" label.</summary>
    public string InstallSource { get; init; } = "Unknown source";

    /// <summary>Enabled state per the profile's Preferences/Secure Preferences JSON
    /// (extensions.settings.&lt;id&gt;.state, 1=enabled/0=disabled) - null ("Unknown") when that
    /// structure isn't present/parseable, never guessed.</summary>
    public bool? IsEnabled { get; init; }

    public string InstallSourceOrEnabledSummary => IsEnabled switch
    {
        true => "Enabled",
        false => "Disabled",
        _ => "Unknown",
    };
}
