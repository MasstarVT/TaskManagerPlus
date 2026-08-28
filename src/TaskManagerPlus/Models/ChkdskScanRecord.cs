namespace TaskManagerPlus.Models;

/// <summary>
/// #340/#341: one persisted result of an app-initiated chkdsk-family action - an online `chkdsk
/// /scan` run, or a MSFT_Volume.Repair Scan/SpotFix/OfflineScanAndFix call - saved to
/// chkdsk-history.json under AppPaths.SettingsDirectory (via ChkdskHistoryStore) so the card can show
/// "last scanned: &lt;date&gt; - ..." across app restarts, the same fail-silent-to-defaults
/// persistence shape as SmartHistoryService/smart-history.json.
/// </summary>
public sealed class ChkdskScanRecord
{
    public string DriveLetter { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool ProblemsFound { get; init; }
    public string Summary { get; init; } = string.Empty;
}
