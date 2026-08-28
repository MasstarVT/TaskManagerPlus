namespace TaskManagerPlus.Models;

/// <summary>Round 11, #70: persisted Summary-tab preferences. Currently just one toggle, kept as
/// its own small file (summary-settings.json) rather than folded into AlertThresholds/ThemeColors
/// since it's conceptually unrelated to either.</summary>
public sealed class SummarySettings
{
    /// <summary>Silently writes a diagnostic report (the same Markdown report the manual "Markdown
    /// report" button generates) to %AppData%\TaskManagerPlus\Reports\ every time the app closes -
    /// useful for "what did the system look like right before I closed it" without remembering to
    /// click the button first. Off by default, like every other opt-in toggle in this app.</summary>
    public bool GenerateReportOnExit { get; set; }

    /// <summary>#989: Dark/Light/Follow system - read by both GenerateHtmlReport (SummaryViewModel)
    /// and the evidence bundle's index.html generator (EvidenceBundleService), so one setting
    /// covers every generated HTML report in the app.</summary>
    public ReportTheme ReportTheme { get; set; } = ReportTheme.Dark;

    public static SummarySettings Defaults => new();
}
