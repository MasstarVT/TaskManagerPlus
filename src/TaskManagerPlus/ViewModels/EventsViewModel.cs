using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Events tab (#101-108) - a full Event Viewer replacement: a left channel tree, a
/// center paged/virtualized grid, a right friendly/raw-XML detail pane, a visual XPath filter
/// builder, an opt-in live-tail "Follow" toggle, and named saved filters. Everything here is
/// button- or selection-driven (no DispatcherTimer), the same on-demand convention
/// StabilityViewModel already uses for its own (much smaller) event-log read - a channel scan or a
/// 500-record page read is exactly the kind of "not cheap enough to repeat on a tick" work
/// CLAUDE.md's on-demand rule is about.
/// </summary>
public sealed class EventsViewModel : ObservableObject, IDisposable
{
    private readonly EventLogExplorerService _service = new();

    // Needed only for #106's "which process logged this" PID -> name lookup in the detail pane -
    // reads the already-live Processes collection (no new polling) purely on the UI thread inside
    // BuildDetail, so there's no cross-thread ObservableCollection access to worry about.
    private readonly ProcessesViewModel _processes;

    public ObservableCollection<EventChannelNode> ChannelTree { get; } = new();
    public ObservableCollection<EventRecordRow> Events { get; } = new();
    public ObservableCollection<SavedEventFilter> SavedFilters { get; } = new();
    public ObservableCollection<EventPropertyDisplay> DetailProperties { get; } = new();

    private EventBookmark? _pageBookmark;
    private string _lastQueriedChannel = string.Empty;
    private string _lastQueriedXPath = "*";
    // #110: whether _lastQueriedChannel is a channel name (PathType.LogName) or the full path to
    // an opened .evtx file (PathType.FilePath) - ReadPage/StartWatch need to know which.
    private bool _lastQueriedIsFilePath;

    private bool _isChannelsLoading;
    public bool IsChannelsLoading { get => _isChannelsLoading; private set => SetProperty(ref _isChannelsLoading, value); }

    private bool _isEventsLoading;
    public bool IsEventsLoading { get => _isEventsLoading; private set => SetProperty(ref _isEventsLoading, value); }

    private bool _hasMore;
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private EventChannelNode? _selectedChannel;
    public EventChannelNode? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (!SetProperty(ref _selectedChannel, value)) return;
            OnPropertyChanged(nameof(CanQuerySelectedChannel));
            if (IsFollowing) StopFollow();
        }
    }

    public bool CanQuerySelectedChannel => _selectedChannel is { IsGroup: false, IsAccessible: true };

    // ---- Filter bar (#104) ----
    private bool _levelCritical = true;
    public bool LevelCritical { get => _levelCritical; set => SetProperty(ref _levelCritical, value); }
    private bool _levelError = true;
    public bool LevelError { get => _levelError; set => SetProperty(ref _levelError, value); }
    private bool _levelWarning;
    public bool LevelWarning { get => _levelWarning; set => SetProperty(ref _levelWarning, value); }
    private bool _levelInformation;
    public bool LevelInformation { get => _levelInformation; set => SetProperty(ref _levelInformation, value); }
    private bool _levelVerbose;
    public bool LevelVerbose { get => _levelVerbose; set => SetProperty(ref _levelVerbose, value); }

    private string _providerFilterText = string.Empty;
    /// <summary>Comma-separated provider names - the "provider multi-select" from #104's spec,
    /// expressed as free text rather than a bespoke multi-select control (no such control exists
    /// elsewhere in this app's hand-rolled MVVM layer to reuse).</summary>
    public string ProviderFilterText { get => _providerFilterText; set => SetProperty(ref _providerFilterText, value); }

    private string _eventIdsText = string.Empty;
    public string EventIdsText { get => _eventIdsText; set => SetProperty(ref _eventIdsText, value); }

    private int _lookbackDays = 7;
    public int LookbackDays { get => _lookbackDays; set => SetProperty(ref _lookbackDays, value); }

    private string _userSidText = string.Empty;
    public string UserSidText { get => _userSidText; set => SetProperty(ref _userSidText, value); }

    private string _keywordText = string.Empty;
    public string KeywordText { get => _keywordText; set => SetProperty(ref _keywordText, value); }

    private string _rawXPathText = "*";
    /// <summary>The XPath actually used by Load/LoadMore/Follow - shown editable so a power user
    /// can hand-edit past whatever BuildXPathCommand last generated (#104).</summary>
    public string RawXPathText { get => _rawXPathText; set => SetProperty(ref _rawXPathText, value); }

    // ---- Selected event / detail pane (#105/#106) ----
    private EventRecordRow? _selectedEvent;
    public EventRecordRow? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value)) return;
            BuildDetail();
        }
    }

    private bool _showRawXml;
    public bool ShowRawXml { get => _showRawXml; set => SetProperty(ref _showRawXml, value); }

    // ---- Live tail (#107) ----
    private EventLogExplorerService.EventWatchHandle? _watchHandle;
    private bool _isFollowing;
    public bool IsFollowing
    {
        get => _isFollowing;
        set
        {
            if (!SetProperty(ref _isFollowing, value)) return;
            if (value) StartFollow(); else StopFollow();
        }
    }

    public AsyncRelayCommand RefreshChannelsCommand { get; }
    public RelayCommand BuildXPathCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public RelayCommand SaveFilterCommand { get; }
    /// <summary>Bound via CommandParameter="{Binding}" from each SavedFilters row (a ListBox/
    /// ItemsControl item), rather than this app's more common SelectedX-property convention - a
    /// closer fit for "apply/delete whichever row's button was clicked" than a shared selection.</summary>
    public RelayCommand ApplyFilterCommand { get; }
    public RelayCommand DeleteFilterCommand { get; }

    private string _newFilterName = string.Empty;
    public string NewFilterName { get => _newFilterName; set => SetProperty(ref _newFilterName, value); }

    // ---- #109: import/export Event Viewer custom views ----
    public ObservableCollection<ImportableCustomView> ImportableCustomViews { get; } = new();
    public AsyncRelayCommand ScanCustomViewsCommand { get; }
    public RelayCommand ImportCustomViewCommand { get; }
    public RelayCommand ExportFilterCommand { get; }

    // ---- #110: open an external .evtx file ----
    public ObservableCollection<RecentArchiveEntry> RecentArchives { get; } = new();
    public RelayCommand RefreshRecentArchivesCommand { get; }
    public RelayCommand OpenEvtxFileCommand { get; }
    public RelayCommand OpenArchiveCommand { get; }

    // ---- #111: cross-channel full-text search (button-gated) ----
    private CancellationTokenSource? _searchCts;
    public ObservableCollection<EventRecordRow> SearchResults { get; } = new();

    private bool _isSearchingAllChannels;
    public bool IsSearchingAllChannels { get => _isSearchingAllChannels; private set { if (SetProperty(ref _isSearchingAllChannels, value)) CancelSearchCommand.RaiseCanExecuteChanged(); } }

    private string? _searchProgressText;
    public string? SearchProgressText { get => _searchProgressText; private set => SetProperty(ref _searchProgressText, value); }

    private bool _isRegexSearch;
    public bool IsRegexSearch { get => _isRegexSearch; set => SetProperty(ref _isRegexSearch, value); }

    public AsyncRelayCommand SearchAllChannelsCommand { get; }
    public RelayCommand CancelSearchCommand { get; }

    // ---- #112: one structured query across several channels at once ----
    public ObservableCollection<EventRecordRow> MultiChannelResults { get; } = new();
    private EventBookmark? _multiChannelBookmark;
    private string _multiChannelStructuredXml = string.Empty;

    private bool _hasMoreMultiChannel;
    public bool HasMoreMultiChannel { get => _hasMoreMultiChannel; private set => SetProperty(ref _hasMoreMultiChannel, value); }

    private bool _isMultiChannelLoading;
    public bool IsMultiChannelLoading { get => _isMultiChannelLoading; private set => SetProperty(ref _isMultiChannelLoading, value); }

    private string? _multiChannelStatusText;
    public string? MultiChannelStatusText { get => _multiChannelStatusText; private set => SetProperty(ref _multiChannelStatusText, value); }

    public AsyncRelayCommand RunMultiChannelQueryCommand { get; }
    public AsyncRelayCommand LoadMoreMultiChannelCommand { get; }

    // ---- #113: provider catalog browser ----
    public ObservableCollection<string> ProviderNames { get; } = new();
    public ObservableCollection<ProviderEventMetadataRow> ProviderEvents { get; } = new();

    private bool _isProviderCatalogOpen;
    public bool IsProviderCatalogOpen
    {
        get => _isProviderCatalogOpen;
        set { if (SetProperty(ref _isProviderCatalogOpen, value) && value && ProviderNames.Count == 0) _ = LoadProviderNamesAsync(); }
    }

    private string? _selectedProvider;
    public string? SelectedProvider
    {
        get => _selectedProvider;
        set { if (SetProperty(ref _selectedProvider, value)) _ = LoadProviderMetadataAsync(value); }
    }

    private string _providerSearchText = string.Empty;
    public string ProviderSearchText
    {
        get => _providerSearchText;
        set { if (SetProperty(ref _providerSearchText, value)) ProviderNamesView.Refresh(); }
    }

    public ICollectionView ProviderNamesView { get; }
    public AsyncRelayCommand LoadProviderNamesCommand { get; }

    // ---- #114: copy the current view as a reusable command ----
    public RelayCommand CopyAsWevtutilCommand { get; }
    public RelayCommand CopyAsPowerShellCommand { get; }

    // ---- #115: event row context menu ----
    public RelayCommand ExplainEventCommand { get; }
    public RelayCommand CopyEventXmlCommand { get; }
    public RelayCommand CopyEventMarkdownCommand { get; }
    public RelayCommand FilterToEventIdCommand { get; }
    public RelayCommand FilterToProviderCommand { get; }
    public RelayCommand ShowAroundTimeCommand { get; }
    public RelayCommand AddToEvidenceCommand { get; }

    /// <summary>#115: a stub in-memory evidence collector - a real evidence-bundle exporter is
    /// item 200 (later); this just gathers selected rows with a small counter/badge in the UI.</summary>
    public ObservableCollection<EventRecordRow> EvidenceBundle { get; } = new();

    /// <summary>#115: TODO(#117) - "Explain" is meant to open/focus a real event knowledge-base
    /// panel once one exists (backlog item 117). No such service exists yet in this codebase, so
    /// this just surfaces a clearly-labeled placeholder note in the detail pane rather than doing
    /// nothing when clicked.</summary>
    private string? _explainNote;
    public string? ExplainNote { get => _explainNote; private set => SetProperty(ref _explainNote, value); }

    // ---- #116: group-by and correlation columns ----
    public ICollectionView EventsView { get; }

    private string _groupBy = "None";
    /// <summary>"None" / "Provider" / "EventId" (#116) - collapses e.g. 400 identical
    /// DistributedCOM rows under one expandable group header with a count.</summary>
    public string GroupBy
    {
        get => _groupBy;
        set { if (SetProperty(ref _groupBy, value)) ApplyGrouping(); }
    }

    private bool _showCorrelationColumns;
    public bool ShowCorrelationColumns { get => _showCorrelationColumns; set => SetProperty(ref _showCorrelationColumns, value); }

    public EventsViewModel(ProcessesViewModel processes)
    {
        _processes = processes;

        RefreshChannelsCommand = new AsyncRelayCommand(RefreshChannelsAsync);
        BuildXPathCommand = new RelayCommand(_ => RawXPathText = BuildXPathFromFilters());
        LoadCommand = new AsyncRelayCommand(() => LoadAsync(reset: true), () => CanQuerySelectedChannel && !IsEventsLoading);
        LoadMoreCommand = new AsyncRelayCommand(() => LoadAsync(reset: false), () => HasMore && !IsEventsLoading);
        SaveFilterCommand = new RelayCommand(_ => SaveCurrentAsFilter(), _ => !string.IsNullOrWhiteSpace(NewFilterName));
        ApplyFilterCommand = new RelayCommand(p => ApplySavedFilter(p as SavedEventFilter));
        DeleteFilterCommand = new RelayCommand(p => DeleteSavedFilter(p as SavedEventFilter));

        ScanCustomViewsCommand = new AsyncRelayCommand(ScanCustomViewsAsync);
        ImportCustomViewCommand = new RelayCommand(p => ImportCustomView(p as ImportableCustomView));
        ExportFilterCommand = new RelayCommand(p => ExportFilter(p as SavedEventFilter));

        RefreshRecentArchivesCommand = new RelayCommand(_ => RefreshRecentArchives());
        OpenEvtxFileCommand = new RelayCommand(_ => OpenEvtxFile());
        OpenArchiveCommand = new RelayCommand(p => OpenArchive(p as RecentArchiveEntry));

        SearchAllChannelsCommand = new AsyncRelayCommand(SearchAllChannelsAsync, () => !IsSearchingAllChannels);
        CancelSearchCommand = new RelayCommand(_ => _searchCts?.Cancel(), _ => IsSearchingAllChannels);

        RunMultiChannelQueryCommand = new AsyncRelayCommand(() => RunMultiChannelQueryAsync(reset: true));
        LoadMoreMultiChannelCommand = new AsyncRelayCommand(() => RunMultiChannelQueryAsync(reset: false), () => HasMoreMultiChannel && !IsMultiChannelLoading);

        LoadProviderNamesCommand = new AsyncRelayCommand(LoadProviderNamesAsync);
        ProviderNamesView = CollectionViewSource.GetDefaultView(ProviderNames);
        ProviderNamesView.Filter = o => o is string name
            && (string.IsNullOrWhiteSpace(ProviderSearchText) || name.Contains(ProviderSearchText, StringComparison.OrdinalIgnoreCase));

        CopyAsWevtutilCommand = new RelayCommand(_ => CopyAsWevtutil());
        CopyAsPowerShellCommand = new RelayCommand(_ => CopyAsPowerShell());

        ExplainEventCommand = new RelayCommand(p => ExplainEvent(p as EventRecordRow));
        CopyEventXmlCommand = new RelayCommand(p => CopyEventXml(p as EventRecordRow));
        CopyEventMarkdownCommand = new RelayCommand(p => CopyEventMarkdown(p as EventRecordRow));
        FilterToEventIdCommand = new RelayCommand(p => FilterToEventId(p as EventRecordRow));
        FilterToProviderCommand = new RelayCommand(p => FilterToProvider(p as EventRecordRow));
        ShowAroundTimeCommand = new RelayCommand(p => _ = ShowAroundTimeAsync(p as EventRecordRow));
        AddToEvidenceCommand = new RelayCommand(p => AddToEvidence(p as EventRecordRow));

        EventsView = CollectionViewSource.GetDefaultView(Events);

        LoadSavedFilters();
        _ = RefreshChannelsAsync();
    }

    private async Task RefreshChannelsAsync()
    {
        IsChannelsLoading = true;
        try
        {
            var tree = await Task.Run(() => _service.GetChannelTree());
            ChannelTree.Clear();
            foreach (var node in tree) ChannelTree.Add(node);
            StatusText = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't enumerate event log channels: {ex.Message}";
        }
        finally
        {
            IsChannelsLoading = false;
        }
    }

    /// <summary>#104: composes RawXPathText from the filter bar controls - a snapshot, not a live
    /// binding, so a subsequent hand-edit of the text box isn't silently overwritten until the
    /// user explicitly rebuilds it again.</summary>
    private string BuildXPathFromFilters()
    {
        var criteria = new EventFilterCriteria { LookbackDays = LookbackDays > 0 ? LookbackDays : null };

        if (LevelCritical) criteria.Levels.Add(1);
        if (LevelError) criteria.Levels.Add(2);
        if (LevelWarning) criteria.Levels.Add(3);
        if (LevelInformation) criteria.Levels.Add(4);
        if (LevelVerbose) criteria.Levels.Add(5);

        if (!string.IsNullOrWhiteSpace(ProviderFilterText))
        {
            criteria.Providers.AddRange(ProviderFilterText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var (ids, ranges) = EventFilterCriteria.ParseEventIds(EventIdsText);
        criteria.EventIds.AddRange(ids);
        criteria.EventIdRanges.AddRange(ranges);

        if (!string.IsNullOrWhiteSpace(UserSidText)) criteria.UserSid = UserSidText.Trim();

        return EventLogExplorerService.BuildXPath(criteria);
    }

    private async Task LoadAsync(bool reset)
    {
        var channel = SelectedChannel;
        if (channel is null || channel.IsGroup || !channel.IsAccessible) return;

        if (reset)
        {
            Events.Clear();
            _pageBookmark = null;
            _lastQueriedChannel = channel.Name;
            _lastQueriedXPath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText;
            _lastQueriedIsFilePath = channel.IsFilePath;
        }
        else if (!string.Equals(_lastQueriedChannel, channel.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Selected channel changed since the last page without an explicit reload - avoid
            // silently paging the wrong channel.
            return;
        }

        IsEventsLoading = true;
        try
        {
            string keyword = KeywordText;
            var pathType = _lastQueriedIsFilePath ? PathType.FilePath : PathType.LogName;
            var result = await Task.Run(() => _service.ReadPage(_lastQueriedChannel, _lastQueriedXPath, reset ? null : _pageBookmark, pathType: pathType));
            if (result.ErrorText is not null)
            {
                StatusText = $"Couldn't read \"{_lastQueriedChannel}\": {result.ErrorText}";
                HasMore = false;
                return;
            }

            var rows = string.IsNullOrWhiteSpace(keyword)
                ? result.Rows
                : result.Rows.Where(r => r.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var row in rows) Events.Add(row);
            _pageBookmark = result.Bookmark;
            HasMore = result.HasMore;
            StatusText = Events.Count == 0 ? "No matching events found." : $"{Events.Count} event(s) loaded.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't read events: {ex.Message}";
        }
        finally
        {
            IsEventsLoading = false;
        }
    }

    /// <summary>#105/#106: rebuilds the friendly property grid plus the resolved account/process
    /// name for whatever is currently selected. Runs synchronously on the UI thread - SID
    /// translation and the process-list lookup are both fast (cached / already-live), unlike the
    /// event-log reads above.</summary>
    private void BuildDetail()
    {
        DetailProperties.Clear();
        var row = SelectedEvent;
        if (row is null) return;

        string account = _service.ResolveUserAccount(row.UserSid);
        DetailProperties.Add(new EventPropertyDisplay { Name = "User", Value = string.IsNullOrEmpty(row.UserSid) ? "Unknown" : $"{account} ({row.UserSid})" });

        string processName = "Unknown";
        if (row.ProcessId is { } pid)
        {
            var match = _processes.Processes.FirstOrDefault(p => p.Pid == pid);
            processName = match is not null ? $"{match.Name} (PID {pid})" : $"PID {pid} (not currently running)";
        }
        DetailProperties.Add(new EventPropertyDisplay { Name = "Process", Value = processName });

        for (int i = 0; i < row.PropertyValues.Count; i++)
            DetailProperties.Add(new EventPropertyDisplay { Name = $"Property {i}", Value = row.PropertyValues[i] });
    }

    private void StartFollow()
    {
        var channel = SelectedChannel;
        if (channel is null || channel.IsGroup || !channel.IsAccessible)
        {
            _isFollowing = false;
            OnPropertyChanged(nameof(IsFollowing));
            return;
        }

        if (channel.IsFilePath)
        {
            // #110: a static .evtx file never gets new records - Follow makes no sense against
            // one, so refuse rather than silently subscribing to a channel that will never fire.
            _isFollowing = false;
            OnPropertyChanged(nameof(IsFollowing));
            StatusText = "Follow isn't available for an opened .evtx file - it's a static snapshot, not a live channel.";
            return;
        }

        StopFollow();
        string xpath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText;
        _watchHandle = _service.StartWatch(channel.Name, xpath,
            row => Application.Current?.Dispatcher.Invoke(() =>
            {
                Events.Insert(0, row);
                StatusText = $"{Events.Count} event(s) loaded (following live).";
            }),
            err => Application.Current?.Dispatcher.Invoke(() => StatusText = $"Follow stopped: {err}"));

        if (_watchHandle is null)
        {
            _isFollowing = false;
            OnPropertyChanged(nameof(IsFollowing));
        }
    }

    /// <summary>Stops the live-tail subscription (#107) - called when the toggle is turned off,
    /// when the selected channel changes, and from MainWindow when the Events tab loses focus (see
    /// EventsViewModel.OnTabDeactivated).</summary>
    public void StopFollow()
    {
        _watchHandle?.Dispose();
        _watchHandle = null;
        if (_isFollowing)
        {
            _isFollowing = false;
            OnPropertyChanged(nameof(IsFollowing));
        }
    }

    /// <summary>Called by MainWindow when the TabControl's selection moves away from the Events
    /// tab (#107's "disposed when the tab loses focus" requirement).</summary>
    public void OnTabDeactivated() => StopFollow();

    private void LoadSavedFilters()
    {
        var settings = EventFilterSettingsService.Load();
        SavedFilters.Clear();
        foreach (var f in settings.Filters) SavedFilters.Add(f);
    }

    private void SaveCurrentAsFilter()
    {
        if (string.IsNullOrWhiteSpace(NewFilterName)) return;

        var filter = new SavedEventFilter
        {
            Name = NewFilterName.Trim(),
            Channels = SelectedChannel is { IsGroup: false } ch ? new List<string> { ch.Name } : new List<string>(),
            XPath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText,
            Columns = new List<string> { "Time", "Level", "Provider", "EventId", "Task", "RecordId", "ProcessId", "User" },
        };

        SavedFilters.Add(filter);
        PersistSavedFilters();
        NewFilterName = string.Empty;
    }

    private void ApplySavedFilter(SavedEventFilter? filter)
    {
        if (filter is null) return;
        RawXPathText = filter.XPath;

        if (filter.Channels.Count > 0)
        {
            var target = filter.Channels[0];
            var match = ChannelTree.SelectMany(g => g.Children).FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (match is not null) SelectedChannel = match;
        }
    }

    private void DeleteSavedFilter(SavedEventFilter? filter)
    {
        if (filter is null) return;
        SavedFilters.Remove(filter);
        PersistSavedFilters();
    }

    private void PersistSavedFilters()
        => EventFilterSettingsService.Save(new EventFilterSettings { Filters = SavedFilters.ToList() });

    // ---- #109: import/export Event Viewer custom views ----

    private async Task ScanCustomViewsAsync()
    {
        var found = await Task.Run(() => _service.GetImportableCustomViews());
        ImportableCustomViews.Clear();
        foreach (var v in found) ImportableCustomViews.Add(v);
        StatusText = found.Count == 0
            ? "No Event Viewer custom views found to import."
            : $"{found.Count} Event Viewer custom view(s) available to import.";
    }

    private void ImportCustomView(ImportableCustomView? view)
    {
        if (view is null) return;

        var filter = new SavedEventFilter
        {
            Name = view.Name,
            Channels = new List<string>(view.Channels),
            XPath = view.XPath,
            Columns = new List<string> { "Time", "Level", "Provider", "EventId", "Task", "RecordId", "ProcessId", "User" },
        };
        SavedFilters.Add(filter);
        PersistSavedFilters();
        StatusText = $"Imported \"{view.Name}\" as a saved filter.";
    }

    private void ExportFilter(SavedEventFilter? filter)
    {
        if (filter is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export as Event Viewer custom view",
            Filter = "Custom View XML (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = $"{filter.Name}.xml",
        };
        if (dialog.ShowDialog() != true) return;

        StatusText = _service.ExportCustomView(filter, dialog.FileName)
            ? $"Exported \"{filter.Name}\" to {dialog.FileName}."
            : $"Couldn't export \"{filter.Name}\" - see the file path and try again.";
    }

    // ---- #110: open an external .evtx file ----

    private void RefreshRecentArchives()
    {
        var archives = _service.GetRecentArchives();
        RecentArchives.Clear();
        foreach (var a in archives) RecentArchives.Add(a);
    }

    private void OpenEvtxFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an event log file",
            Filter = "Windows Event Log (*.evtx)|*.evtx|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        OpenEvtxPath(dialog.FileName);
    }

    private void OpenArchive(RecentArchiveEntry? entry)
    {
        if (entry is null) return;
        OpenEvtxPath(entry.Path);
    }

    private void OpenEvtxPath(string path)
    {
        var node = _service.OpenEvtxFile(path);
        if (node is null)
        {
            StatusText = $"Couldn't open \"{Path.GetFileName(path)}\" - it may not be a valid .evtx file, or it's locked by another process.";
            return;
        }

        // Surface the opened file as a synthetic top-level entry in the channel tree (rather than
        // folding it into "Applications and Services") so it's obviously distinct from a live
        // registered channel - same greyed/labeled treatment #102's "no access" leaves already use
        // for a different kind of special-case node.
        ChannelTree.Insert(0, node);
        SelectedChannel = node;
        StatusText = $"Opened \"{node.DisplayName}\" ({node.RecordCount ?? 0} record(s)).";
    }

    // ---- #111: cross-channel full-text search (button-gated) ----

    private async Task SearchAllChannelsAsync()
    {
        string xpath = BuildXPathFromFilters();
        string? keyword = string.IsNullOrWhiteSpace(KeywordText) ? null : KeywordText;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearchingAllChannels = true;
        SearchResults.Clear();
        SearchProgressText = "Starting cross-channel search...";
        try
        {
            var progress = new Progress<EventLogExplorerService.CrossChannelSearchProgress>(p =>
                SearchProgressText = $"Scanning \"{p.CurrentChannel}\" ({p.ChannelsCompleted}/{p.ChannelsTotal} channels) - {p.MatchesSoFar} match(es) so far.");

            var results = await Task.Run(() => _service.SearchAllChannels(xpath, keyword, IsRegexSearch, maxPerChannel: 5000, maxTotalResults: 2000, progress, token), token);

            foreach (var row in results) SearchResults.Add(row);
            SearchProgressText = $"Done - {SearchResults.Count} match(es) found across all readable channels.";
        }
        catch (OperationCanceledException)
        {
            SearchProgressText = $"Search cancelled - {SearchResults.Count} match(es) found before stopping.";
        }
        catch (Exception ex)
        {
            SearchProgressText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingAllChannels = false;
        }
    }

    // ---- #112: one structured query across several channels at once ----

    /// <summary>#112: builds the structured query from whichever channel-tree leaves have
    /// IsSelectedForMulti checked (falls back to every accessible non-group leaf if nothing was
    /// explicitly checked, so the button still does something useful with zero setup) and reads
    /// one page - reused by the row context menu's "Show ±5 minutes across all channels" with a
    /// synthesized time-bounded xpath instead of the filter bar's.</summary>
    private async Task RunMultiChannelQueryAsync(bool reset)
    {
        if (reset)
        {
            var selected = ChannelTree.SelectMany(g => g.Children)
                .Where(c => !c.IsGroup && c.IsAccessible && c.IsSelectedForMulti)
                .Select(c => c.Name)
                .ToList();
            if (selected.Count == 0)
                selected = ChannelTree.SelectMany(g => g.Children).Where(c => !c.IsGroup && c.IsAccessible).Select(c => c.Name).ToList();

            if (selected.Count == 0)
            {
                MultiChannelStatusText = "No accessible channels to query.";
                return;
            }

            string xpath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText;
            _multiChannelStructuredXml = EventLogExplorerService.BuildStructuredQuery(selected, xpath);
            _multiChannelBookmark = null;
            MultiChannelResults.Clear();
        }
        else if (string.IsNullOrEmpty(_multiChannelStructuredXml))
        {
            return;
        }

        IsMultiChannelLoading = true;
        try
        {
            var result = await Task.Run(() => _service.ReadMultiChannel(_multiChannelStructuredXml, reset ? null : _multiChannelBookmark));
            if (result.ErrorText is not null)
            {
                MultiChannelStatusText = $"Multi-channel query failed: {result.ErrorText}";
                HasMoreMultiChannel = false;
                return;
            }

            foreach (var row in result.Rows) MultiChannelResults.Add(row);
            _multiChannelBookmark = result.Bookmark;
            HasMoreMultiChannel = result.HasMore;
            MultiChannelStatusText = MultiChannelResults.Count == 0 ? "No matching events found." : $"{MultiChannelResults.Count} event(s) loaded.";
        }
        catch (Exception ex)
        {
            MultiChannelStatusText = $"Multi-channel query failed: {ex.Message}";
        }
        finally
        {
            IsMultiChannelLoading = false;
        }
    }

    // ---- #113: provider catalog browser ----

    private async Task LoadProviderNamesAsync()
    {
        var names = await Task.Run(() => _service.GetProviderNames());
        ProviderNames.Clear();
        foreach (var n in names) ProviderNames.Add(n);
    }

    private async Task LoadProviderMetadataAsync(string? providerName)
    {
        ProviderEvents.Clear();
        if (string.IsNullOrWhiteSpace(providerName)) return;

        var events = await Task.Run(() => _service.GetProviderMetadata(providerName));
        foreach (var e in events) ProviderEvents.Add(e);
    }

    // ---- #114: copy the current view as a reusable command ----

    private void CopyAsWevtutil()
    {
        var channel = SelectedChannel is { IsGroup: false } ch ? ch.Name : "System";
        string xpath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText;
        string line = $"wevtutil qe \"{channel}\" \"/q:{xpath}\" /f:text /c:50";
        TrySetClipboardText(line);
        StatusText = "Copied as a wevtutil command.";
    }

    private void CopyAsPowerShell()
    {
        var channel = SelectedChannel is { IsGroup: false } ch ? ch.Name : "System";
        string xpath = string.IsNullOrWhiteSpace(RawXPathText) ? "*" : RawXPathText;
        string line = $"Get-WinEvent -LogName '{channel.Replace("'", "''")}' -FilterXPath '{xpath.Replace("'", "''")}'";
        TrySetClipboardText(line);
        StatusText = "Copied as a PowerShell Get-WinEvent command.";
    }

    // ---- #115: event row context menu ----

    private void ExplainEvent(EventRecordRow? row)
    {
        if (row is null) return;
        // TODO(#117): wire this to the real event knowledge-base panel once it exists (later
        // backlog chunk, items 117-126). Until then, this is a clearly-labeled placeholder rather
        // than a silently-do-nothing menu item.
        ExplainNote = $"Explain: no local knowledge base yet for {row.ProviderName} event {row.EventId} (see backlog item 117).";
        SelectedEvent = row;
    }

    private void CopyEventXml(EventRecordRow? row)
    {
        if (row is null) return;
        TrySetClipboardText(row.RawXml);
        StatusText = "Copied event XML to the clipboard.";
    }

    private void CopyEventMarkdown(EventRecordRow? row)
    {
        if (row is null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"**{row.ProviderName}** - Event {row.EventId} ({row.Level})");
        sb.AppendLine();
        sb.AppendLine($"- Channel: `{row.ChannelName}`");
        sb.AppendLine($"- Time: {row.TimeCreated:u}");
        if (row.RecordId is { } rid) sb.AppendLine($"- Record ID: {rid}");
        if (row.ProcessId is { } pid) sb.AppendLine($"- Process ID: {pid}");
        if (!string.IsNullOrEmpty(row.UserSid)) sb.AppendLine($"- User SID: {row.UserSid}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(row.Message))
        {
            sb.AppendLine("```");
            sb.AppendLine(row.Message.Trim());
            sb.AppendLine("```");
        }

        TrySetClipboardText(sb.ToString());
        StatusText = "Copied event as Markdown to the clipboard.";
    }

    private void FilterToEventId(EventRecordRow? row)
    {
        if (row is null) return;
        EventIdsText = row.EventId.ToString();
        RawXPathText = BuildXPathFromFilters();
    }

    private void FilterToProvider(EventRecordRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.ProviderName)) return;
        ProviderFilterText = row.ProviderName;
        RawXPathText = BuildXPathFromFilters();
    }

    /// <summary>#115: "Show ±5 minutes across all channels" - reuses #112's multi-channel reader
    /// against a synthesized time-bounded xpath (ignores the filter bar's own level/provider/ID
    /// filters entirely, since the point here is "what else happened near this moment," not a
    /// narrower slice of it) across whichever channels are currently accessible.</summary>
    private async Task ShowAroundTimeAsync(EventRecordRow? row)
    {
        if (row is null || row.TimeCreated == DateTime.MinValue) return;

        var start = row.TimeCreated.ToUniversalTime().AddMinutes(-5);
        var end = row.TimeCreated.ToUniversalTime().AddMinutes(5);
        string xpath = $"*[System[TimeCreated[@SystemTime>='{start:o}'] and TimeCreated[@SystemTime<='{end:o}']]]";

        var channels = ChannelTree.SelectMany(g => g.Children).Where(c => !c.IsGroup && c.IsAccessible).Select(c => c.Name).ToList();
        if (channels.Count == 0) return;

        _multiChannelStructuredXml = EventLogExplorerService.BuildStructuredQuery(channels, xpath);
        _multiChannelBookmark = null;
        MultiChannelResults.Clear();
        MultiChannelStatusText = $"Loading events within +/-5 minutes of {row.TimeCreated:g}...";

        IsMultiChannelLoading = true;
        try
        {
            var result = await Task.Run(() => _service.ReadMultiChannel(_multiChannelStructuredXml, null));
            if (result.ErrorText is not null)
            {
                MultiChannelStatusText = $"Couldn't load the surrounding window: {result.ErrorText}";
                return;
            }
            foreach (var r in result.Rows) MultiChannelResults.Add(r);
            _multiChannelBookmark = result.Bookmark;
            HasMoreMultiChannel = result.HasMore;
            MultiChannelStatusText = $"{MultiChannelResults.Count} event(s) within +/-5 minutes of {row.TimeCreated:g}.";
        }
        finally
        {
            IsMultiChannelLoading = false;
        }
    }

    private void AddToEvidence(EventRecordRow? row)
    {
        if (row is null) return;
        if (EvidenceBundle.Contains(row)) return;
        EvidenceBundle.Add(row);
        StatusText = $"Added to evidence bundle ({EvidenceBundle.Count} item(s)).";
    }

    private static void TrySetClipboardText(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard owned by another process momentarily - best-effort, same as elsewhere in this app */ }
    }

    // ---- #116: group-by and correlation columns ----

    private void ApplyGrouping()
    {
        EventsView.GroupDescriptions.Clear();
        switch (GroupBy)
        {
            case "Provider":
                EventsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EventRecordRow.ProviderName)));
                break;
            case "EventId":
                EventsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EventRecordRow.EventId)));
                break;
        }
    }

    public void Dispose()
    {
        StopFollow();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
