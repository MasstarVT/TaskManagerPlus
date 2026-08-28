namespace TaskManagerPlus.Models;

/// <summary>#989: light/dark theme selector for the generated HTML reports - SummaryViewModel's
/// GenerateHtmlReport (BuildReportHtml) and EvidenceBundleService's index.html generator both read
/// this same setting, so choosing a theme once covers both report generators. "Follow system"
/// emits both palettes with a `prefers-color-scheme` media query rather than picking one at
/// generation time, since the report is a static file that may be opened later on a different
/// machine/browser than the one that generated it.</summary>
public enum ReportTheme
{
    Dark,
    Light,
    FollowSystem,
}
