using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Cross-tab search (#100) - "find anything mentioning 'nvidia'" across Processes, Services,
/// Startup items, and the System tab's driver/software/USB-device lists at once, without hunting
/// through each tab individually. Purely a live filter over collections the other view-models are
/// already polling/have already loaded - no new sampling or I/O of its own, the same "thin
/// composition, no new poller" shape CpuViewModel/StorageViewModel/etc. already follow.
///
/// suggestions.md #1000: extended (Ctrl+K command palette) to also search tab names, the current
/// Health Check findings, every loaded rule, a handful of system-wide remediation actions, glossary
/// terms, and already-loaded recent timeline events - each result carries an optional
/// <see cref="SearchNavigationRequest"/> that MainWindow.xaml.cs (which owns real tab-switching/
/// drawer-opening capability, unlike this ViewModel) performs when the palette activates it (see
/// <see cref="Activate"/> and MainWindow's subscription to it).
/// </summary>
public sealed class GlobalSearchViewModel : ObservableObject
{
    private const int MaxPerCategory = 12;

    private readonly ProcessesViewModel _processes;
    private readonly ServicesViewModel _services;
    private readonly StartupViewModel _startup;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly SummaryViewModel _summary;
    private readonly RulesEditorViewModel _rulesEditor;
    private readonly TroubleshootViewModel _troubleshoot;

    /// <summary>suggestions.md #1006: every navigable destination - group tabs, leaf tabs, and
    /// `tab › section` chip pairs - generated from MainWindow's real TabControl tree at startup
    /// (MainWindow.EnumerateTabDestinations) and injected here via <see cref="SetTabDestinations"/>,
    /// since this ViewModel has no reference to that Window. Replaces a hand-maintained constant
    /// list that had silently drifted twice (it never learned the group names or the five newest
    /// tabs, and could not address sections at all).</summary>
    private IReadOnlyList<TabDestination> _tabDestinations = Array.Empty<TabDestination>();

    /// <summary>Called once by MainWindow after its tab tree is constructed.</summary>
    public void SetTabDestinations(IReadOnlyList<TabDestination> destinations) => _tabDestinations = destinations;

    public ObservableCollection<SearchResult> Results { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) Refresh(); }
    }

    public bool HasQuery => SearchText.Trim().Length >= 2;

    /// <summary>#1000: MainWindow.xaml.cs subscribes to this once, at construction - see
    /// SearchNavigationRequest's remarks for why the actual navigation happens there, not here.</summary>
    public event Action<SearchNavigationRequest>? NavigationRequested;

    public GlobalSearchViewModel(ProcessesViewModel processes, ServicesViewModel services,
        StartupViewModel startup, SystemSpecsViewModel systemSpecs, SummaryViewModel summary,
        RulesEditorViewModel rulesEditor, TroubleshootViewModel troubleshoot)
    {
        _processes = processes;
        _services = services;
        _startup = startup;
        _systemSpecs = systemSpecs;
        _summary = summary;
        _rulesEditor = rulesEditor;
        _troubleshoot = troubleshoot;
    }

    /// <summary>Activates a result - raises NavigationRequested for MainWindow to carry out, a
    /// no-op for a result with no Navigation (informational-only categories, if any).</summary>
    public void Activate(SearchResult result)
    {
        if (result.Navigation is { } nav) NavigationRequested?.Invoke(nav);
    }

    private static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasQuery));

        var q = SearchText.Trim();
        if (q.Length < 2) return;

        foreach (var p in _processes.Processes.Where(p => Has(p.Name, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Process", Name = p.Name, Detail = $"PID {p.Pid} · {p.CpuPercent:0.0}% CPU · {Formatting.FormatBytes(p.MemoryBytes)}" });

        foreach (var s in _services.Services.Where(s => Has(s.DisplayName, q) || Has(s.ServiceName, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Service", Name = s.DisplayName, Detail = $"{s.ServiceName} · {s.Status}" });

        foreach (var s in _startup.Items.Where(s => Has(s.Name, q) || Has(s.Command, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Startup item", Name = s.Name, Detail = s.SourceDescription });

        foreach (var d in _systemSpecs.OutdatedDrivers.Where(d => Has(d.Primary, q) || Has(d.Secondary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Driver", Name = d.Primary, Detail = d.Secondary });

        foreach (var sw in _systemSpecs.RecentlyInstalledSoftware.Where(s => Has(s.Primary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Software", Name = sw.Primary, Detail = sw.Secondary });

        foreach (var u in _systemSpecs.UsbDevices.Where(u => Has(u.Primary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "USB device", Name = u.Primary, Detail = u.HealthText });

        // #1000/#1006: tab and section names - jump straight to a destination by typing its name
        // ("crashes" finds Stability › Crashes). Section matches search the section's own name so a
        // query matching only the parent tab doesn't drown the tab hit in its section list.
        foreach (var t in _tabDestinations.Where(t => Has(t.Section ?? t.TabName, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult
            {
                Category = t.Section is null ? "Tab" : "Section",
                Name = t.DisplayName,
                Detail = t.Section is null ? "Jump to this tab" : "Jump to this section",
                Navigation = new SearchNavigationRequest { TabName = t.TabName, Section = t.Section },
            });

        // #1000: currently fired Health Check findings - selecting one navigates to Summary (this
        // app has no per-finding scroll-into-view anchor to target more precisely than that).
        foreach (var f in _summary.HealthIssues.Where(f => Has(f.Message, q) || Has(f.Title ?? string.Empty, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Finding", Name = f.Title ?? f.Message, Detail = f.Message, Navigation = new SearchNavigationRequest { TabName = "Summary" } });

        // #1000: every loaded rule (enabled or not) - selecting one opens the Settings drawer with
        // that rule selected in the Rules engine editor.
        foreach (var r in _rulesEditor.Rows.Where(r => Has(r.Title, q) || Has(r.Body, q) || Has(r.Id, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Rule", Name = r.Title, Detail = $"{r.Category} · {r.Id}", Navigation = new SearchNavigationRequest { OpenSettings = true, SelectRuleId = r.Id } });

        // #1000: a handful of system-wide remediation actions (the ones that need no live target -
        // per-drive/per-service/per-startup-item actions only make sense resolved against a real
        // finding, so aren't listed standalone here). Selecting one navigates to Summary, where
        // "Fix this" buttons on a matching finding actually offer it.
        foreach (var a in RemediationActionCatalog.SystemWideCatalog().Where(a => Has(a.Title, q) || Has(a.PlainEnglishDescription, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Remediation action", Name = a.Title, Detail = a.PlainEnglishDescription, Navigation = new SearchNavigationRequest { TabName = "Summary" } });

        // #1000: glossary terms - selecting one opens the Troubleshoot tab's Glossary sub-page.
        foreach (var g in GlossaryService.All.Where(g => Has(g.Term, q) || Has(g.Definition, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Glossary term", Name = g.Term, Detail = g.Definition, Navigation = new SearchNavigationRequest { TabName = "Troubleshoot", TroubleshootPanel = "Glossary" } });

        // #1000: recent timeline events - only searches whatever the Timeline panel has already
        // loaded this session (opening it fresh here would mean a search keystroke triggering a
        // real event-log/WMI scan, which this app's "on-demand, never on a tick or a keystroke"
        // convention rules out). Selecting one opens the Troubleshoot tab's Timeline sub-page.
        foreach (var e in _troubleshoot.Timeline.FilteredEvents.Where(e => Has(e.Title, q) || Has(e.Detail, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Timeline event", Name = e.Title, Detail = $"{e.Timestamp:g} · {e.LaneDisplayName}", Navigation = new SearchNavigationRequest { TabName = "Troubleshoot", TroubleshootPanel = "Timeline" } });
    }
}
