using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Windows;
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
            var result = await Task.Run(() => _service.ReadPage(_lastQueriedChannel, _lastQueriedXPath, reset ? null : _pageBookmark));
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

    public void Dispose() => StopFollow();
}
