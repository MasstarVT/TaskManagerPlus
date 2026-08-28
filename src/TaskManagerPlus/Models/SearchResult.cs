namespace TaskManagerPlus.Models;

/// <summary>suggestions.md #1000: what activating a search/command-palette result should do -
/// built by GlobalSearchViewModel (which has no direct tab-switching or drawer-opening capability
/// of its own) and carried out by MainWindow.xaml.cs (which does), the same "ViewModel raises an
/// event, the Window with real navigation capability performs it" split GlobalHotkeyService.Pressed
/// already establishes. Every field is optional and independently actionable - a request can
/// switch tabs, open the Settings drawer, open a specific Troubleshoot sub-page, and/or select a
/// specific rule row, in any combination a given result needs.</summary>
public sealed record SearchNavigationRequest
{
    /// <summary>A tab header to switch to (matched the same case-insensitive way Ctrl+1..9/--tab
    /// already do) - e.g. "Summary", "CPU", "Troubleshoot".</summary>
    public string? TabName { get; init; }

    public bool OpenSettings { get; init; }

    /// <summary>One of "Glossary" or "Timeline" - opens that Troubleshoot sub-page (TabName should
    /// also be set to "Troubleshoot" alongside this).</summary>
    public string? TroubleshootPanel { get; init; }

    /// <summary>Selects this rule id in the Settings drawer's Rules engine editor (OpenSettings
    /// should also be set alongside this).</summary>
    public string? SelectRuleId { get; init; }
}

/// <summary>One hit from the cross-tab search (#100) / Ctrl+K command palette (#1000) - "find
/// anything mentioning 'nvidia'" across Processes, Services, Startup, drivers, installed software,
/// USB devices, tab names, current findings, loaded rules, the remediation action catalog,
/// glossary terms, and recent timeline events, all in one list.</summary>
public sealed class SearchResult
{
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>#1000: null for a result with nothing sensible to navigate to (rare - kept so
    /// every category can still degrade to "just informational" rather than requiring one).</summary>
    public SearchNavigationRequest? Navigation { get; init; }
}
