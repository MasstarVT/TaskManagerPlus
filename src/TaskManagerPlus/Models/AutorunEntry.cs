namespace TaskManagerPlus.Models;

/// <summary>
/// One persistence-location entry - a "thing that runs without being launched by hand". Round 13,
/// #802: a single shared shape for every persistence mechanism AutorunsService knows about
/// (registry Run/RunOnce/RunOnceEx/RunServices/RunServicesOnce/policy Run keys, the Winlogon shell
/// chain, Winlogon Notify packages, AppInit_DLLs, AppCertDlls, and whatever later chunks add - e.g.
/// services, scheduled tasks, WMI event subscriptions), so all of them render in one sortable/
/// filterable DataGrid (the Security tab's Persistence section) with one signature column, one
/// "Open containing folder" action, and one "Copy registry path" action, rather than a bespoke
/// panel per location the way StartupView.xaml currently has one section per source.
///
/// Plain init-only data, same shape as ShellExtensionInfo/BrowserExtensionInfo - each scan produces
/// a fresh immutable snapshot rather than mutating rows in place, and this shape is also what gets
/// JSON-serialized for the baseline snapshot (AutorunsBaselineService).
/// </summary>
public sealed class AutorunEntry
{
    /// <summary>Which persistence mechanism this came from, e.g. "RunOnce (HKCU)",
    /// "Policy Run (HKLM)", "Logon chain", "Winlogon Notify", "AppInit_DLLs", "AppCertDlls".</summary>
    public string Category { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The raw registry value exactly as stored, before any parsing.</summary>
    public string RawCommand { get; init; } = string.Empty;

    /// <summary>Best-effort bare executable/DLL path extracted from RawCommand - empty when it
    /// couldn't be resolved to a file path.</summary>
    public string ResolvedPath { get; init; } = string.Empty;

    /// <summary>#837: certificate subject CN (falling back to issuer CN, then "Unknown"), via
    /// SignatureCheckService.GetSignerInfo - see AutorunsService.GetPublisher. "Unknown" here means
    /// either no resolvable path (most non-file-backed locations - registry values with no
    /// executable/DLL target) or a signature check that couldn't determine a signer.</summary>
    public string Publisher { get; init; } = "Unknown";

    /// <summary>"Signed" / "Unsigned" / "Unknown" - the backward-compatible string form of
    /// SignatureCheckService's richer SignatureVerification enum (#836: now a real WinVerifyTrust
    /// chain-and-catalog check, not just an embedded-signature-only read). A quick flag, not a
    /// verdict - see SignatureCheckService's remarks for exactly what is and isn't verified.</summary>
    public string SignatureStatus { get; init; } = "Unknown";

    /// <summary>Where this entry lives - a "HIVE\key\value" registry path for every location this
    /// chunk covers; file-based persistence locations added later would use a file path instead.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>Most of the locations this chunk covers (RunOnce, RunServices, the Winlogon
    /// values, AppInit_DLLs, AppCertDlls) have no Explorer-recognized "disabled" flag the way the
    /// classic Run key does - true here just means "present", not necessarily "toggleable".</summary>
    public bool Enabled { get; init; } = true;
}
