using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One selectable entry in the #948 date-range preset row. A record (not a ValueTuple) so
/// its Key/Label are real bindable properties - WPF data binding can't see a ValueTuple's C#-only
/// element names (Item1/Item2 are all that exist at runtime).</summary>
public sealed record TimelineRangePresetOption(string Key, string Label);

/// <summary>One selectable entry in the #945 marker-detail window picker - same "real properties,
/// not a ValueTuple" reasoning as TimelineRangePresetOption above.</summary>
public sealed record TimelineDetailWindowOption(double Hours, string Label);

/// <summary>
/// #938-949: backs the Timeline panel - one shared time axis overlaying every dated event this
/// app can find (crashes, Windows Updates, driver installs, software installs, thermal events,
/// service failures, perf spikes, and user notes), reachable from the Troubleshoot tab's landing
/// page. Loaded on demand (<see cref="LoadCommand"/>, an initial load plus a manual "Refresh"),
/// never on a timer - every underlying source (WMI sweeps, event-log scans, a registry walk, a
/// setupapi.dev.log parse, a pnputil shell-out) is exactly the kind of "genuinely expensive"
/// read CLAUDE.md's on-demand convention calls for.
///
/// "Zoomable" (#938) is implemented as the date-range preset in <see cref="RangePreset"/> (#948)
/// rather than true pixel-drag zoom - simpler, and the linked detail table (#946) gives the same
/// "narrow down to a window" outcome. Lane markers are positioned on a fixed-width track by
/// TimelineMarkerPositionConverter (a MultiBinding over each marker's Timestamp plus this
/// view-model's WindowStartLocal/WindowEndLocal), not a true zoom/pan gesture.
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly LoggingViewModel _logging;
    private readonly TimelineViewSettings _settings;
    private readonly Dictionary<TimelineLane, TimelineLaneRow> _laneByKey = new();

    // Last full aggregation from LoadAsync, unfiltered by lane visibility/date-range - kept around
    // so toggling a lane checkbox or changing the range preset re-filters instantly without
    // re-running every WMI/event-log/registry read.
    private List<TimelineEvent> _allEvents = new();

    public ObservableCollection<TimelineLaneRow> Lanes { get; } = new();

    /// <summary>#946: the linked detail table - every visible-lane event inside the currently
    /// resolved window, newest first.</summary>
    public ObservableCollection<TimelineEvent> FilteredEvents { get; } = new();

    /// <summary>#944: "N of your M crashes/failures happened within this window of X" headlines,
    /// most-matched first - see TimelineService.ComputeCorrelations' remarks on why this is always
    /// worded as a coincidence count, never causation.</summary>
    public ObservableCollection<string> CorrelationFindings { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public static readonly TimelineRangePresetOption[] RangePresets =
    {
        new("24h", "24 h"), new("7d", "7 d"), new("30d", "30 d"), new("90d", "90 d"), new("all", "All"),
    };

    private string _rangePreset;
    public string RangePreset
    {
        get => _rangePreset;
        set
        {
            string normalized = RangePresets.Any(p => p.Key == value) ? value : "7d";
            if (!SetProperty(ref _rangePreset, normalized)) return;
            _settings.RangePreset = normalized;
            TimelineViewSettingsService.Save(_settings);
            RebuildFilteredView();
        }
    }

    private DateTime _windowStartLocal;
    public DateTime WindowStartLocal { get => _windowStartLocal; private set => SetProperty(ref _windowStartLocal, value); }

    private DateTime _windowEndLocal;
    public DateTime WindowEndLocal { get => _windowEndLocal; private set => SetProperty(ref _windowEndLocal, value); }

    /// <summary>#946: explicit, copyable text for the currently resolved window - selecting a lane
    /// marker or a range preset should always show exactly what range is now in effect.</summary>
    public string WindowRangeText => $"{WindowStartLocal:g} – {WindowEndLocal:g}";

    /// <summary>Fixed pixel width of each lane's marker track - shared between TimelineView.xaml's
    /// Canvas Width and TimelineMarkerPositionConverter's MultiBinding (see the converter's
    /// remarks) so both agree on the same scale.</summary>
    public double TrackWidthPx => 900;

    private double _correlationWindowHours;
    public double CorrelationWindowHours
    {
        get => _correlationWindowHours;
        set
        {
            double clamped = Math.Clamp(value, 1, 24 * 30);
            if (!SetProperty(ref _correlationWindowHours, clamped)) return;
            _settings.CorrelationWindowHours = clamped;
            TimelineViewSettingsService.Save(_settings);
            RebuildCorrelations();
        }
    }

    // #945: "what happened right before this?" - selecting a marker (a click, not a right-click
    // context menu; see TimelineView.xaml's remarks for why) opens a filtered detail list of
    // everything from every lane within +/- the chosen window of that marker's timestamp.
    private TimelineEvent? _selectedMarker;
    public TimelineEvent? SelectedMarker
    {
        get => _selectedMarker;
        private set { if (SetProperty(ref _selectedMarker, value)) OnPropertyChanged(nameof(HasSelectedMarker)); }
    }
    public bool HasSelectedMarker => SelectedMarker is not null;

    public static readonly TimelineDetailWindowOption[] DetailWindowOptions =
    {
        new(0.25, "15 min"), new(1, "1 hr"), new(6, "6 hr"), new(24, "24 hr"),
    };

    private double _detailWindowHours = 1;
    public double DetailWindowHours
    {
        get => _detailWindowHours;
        set { if (SetProperty(ref _detailWindowHours, value)) RebuildMarkerDetail(); }
    }

    public ObservableCollection<TimelineEvent> AroundSelectedMarker { get; } = new();

    public RelayCommand ShowMarkerDetailCommand { get; }
    public RelayCommand CloseMarkerDetailCommand { get; }

    // #948/#945: button-driven equivalents of setting RangePreset/DetailWindowHours directly -
    // XAML Buttons need an ICommand, not a raw property setter.
    public RelayCommand SelectRangePresetCommand { get; }
    public RelayCommand SelectDetailWindowCommand { get; }

    // #947: "Mark now" plus a plain text+date form (a simple inline form, not a modal dialog -
    // see TimelineView.xaml's remarks) that both write dated free-text markers to
    // timeline-notes.json and render as their own Notes lane.
    private string _newNoteText = string.Empty;
    public string NewNoteText { get => _newNoteText; set => SetProperty(ref _newNoteText, value); }

    private DateTime _newNoteDate = DateTime.Now;
    public DateTime NewNoteDate { get => _newNoteDate; set => SetProperty(ref _newNoteDate, value); }

    public RelayCommand MarkNowCommand { get; }
    public RelayCommand AddNoteCommand { get; }

    /// <summary>#949: exports every event in FilteredEvents (i.e. the currently visible lanes,
    /// currently resolved window) as a chronological Markdown or CSV table, picked by the
    /// SaveFileDialog's chosen extension - same pattern as SummaryViewModel.GenerateReport's
    /// SaveFileDialog usage.</summary>
    public RelayCommand ExportRangeCommand { get; }

    public AsyncRelayCommand LoadCommand { get; }

    public TimelineViewModel(LoggingViewModel logging)
    {
        _logging = logging;
        _settings = TimelineViewSettingsService.Load();
        _rangePreset = RangePresets.Any(p => p.Key == _settings.RangePreset) ? _settings.RangePreset : "7d";
        _correlationWindowHours = _settings.CorrelationWindowHours <= 0 ? 48 : _settings.CorrelationWindowHours;

        AddLane(TimelineLane.Crashes, "Crashes", _settings.ShowCrashes);
        AddLane(TimelineLane.ServiceFailures, "Service failures", _settings.ShowServiceFailures);
        AddLane(TimelineLane.WindowsUpdates, "Windows Updates", _settings.ShowWindowsUpdates);
        AddLane(TimelineLane.DriverInstalls, "Driver installs", _settings.ShowDriverInstalls);
        AddLane(TimelineLane.SoftwareInstalls, "Software installs", _settings.ShowSoftwareInstalls);
        AddLane(TimelineLane.ThermalEvents, "Thermal events", _settings.ShowThermalEvents);
        AddLane(TimelineLane.PerfSpikes, "Perf spikes", _settings.ShowPerfSpikes);
        AddLane(TimelineLane.Notes, "Notes", _settings.ShowNotes);

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        MarkNowCommand = new RelayCommand(_ => AddNote(DateTime.Now, "Marked now"));
        AddNoteCommand = new RelayCommand(_ => AddNote(NewNoteDate, NewNoteText), _ => !string.IsNullOrWhiteSpace(NewNoteText));
        ShowMarkerDetailCommand = new RelayCommand(param =>
        {
            if (param is not TimelineEvent ev) return;
            SelectedMarker = ev;
            RebuildMarkerDetail();
        });
        CloseMarkerDetailCommand = new RelayCommand(_ => SelectedMarker = null);
        SelectRangePresetCommand = new RelayCommand(param => { if (param is string key) RangePreset = key; });
        SelectDetailWindowCommand = new RelayCommand(param => { if (param is double hours) DetailWindowHours = hours; });
        ExportRangeCommand = new RelayCommand(_ => ExportRange(), _ => FilteredEvents.Count > 0);

        RebuildFilteredView(); // establishes an initial WindowStart/EndLocal before the first load finishes
        _ = LoadAsync();
    }

    private void AddLane(TimelineLane lane, string displayName, bool visible)
    {
        var row = new TimelineLaneRow(lane, displayName, visible);
        row.VisibilityChanged += () => { PersistLaneVisibility(); RebuildFilteredView(); };
        Lanes.Add(row);
        _laneByKey[lane] = row;
    }

    private void PersistLaneVisibility()
    {
        _settings.ShowCrashes = _laneByKey[TimelineLane.Crashes].IsVisible;
        _settings.ShowServiceFailures = _laneByKey[TimelineLane.ServiceFailures].IsVisible;
        _settings.ShowWindowsUpdates = _laneByKey[TimelineLane.WindowsUpdates].IsVisible;
        _settings.ShowDriverInstalls = _laneByKey[TimelineLane.DriverInstalls].IsVisible;
        _settings.ShowSoftwareInstalls = _laneByKey[TimelineLane.SoftwareInstalls].IsVisible;
        _settings.ShowThermalEvents = _laneByKey[TimelineLane.ThermalEvents].IsVisible;
        _settings.ShowPerfSpikes = _laneByKey[TimelineLane.PerfSpikes].IsVisible;
        _settings.ShowNotes = _laneByKey[TimelineLane.Notes].IsVisible;
        TimelineViewSettingsService.Save(_settings);
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = null;
        try
        {
            var driverTask = TimelineService.GetDriverInstallEventsAsync();

            var syncEvents = await Task.Run(() =>
            {
                var list = new List<TimelineEvent>();
                list.AddRange(TimelineService.GetReliabilityCrashEvents());
                list.AddRange(TimelineService.GetServiceFailureEvents());
                list.AddRange(TimelineService.GetWindowsUpdateEvents());
                list.AddRange(TimelineService.GetSoftwareInstallEvents());
                list.AddRange(ThermalEventLogService.ReadAll());
                foreach (var n in TimelineNotesService.Load())
                {
                    list.Add(new TimelineEvent
                    {
                        Lane = TimelineLane.Notes,
                        Timestamp = n.Timestamp,
                        Title = n.Text.Length > 60 ? n.Text[..60] + "…" : n.Text,
                        Detail = n.Text,
                        Source = "User note",
                        IsFailure = false,
                    });
                }
                return list;
            });

            var events = new List<TimelineEvent>(syncEvents);
            events.AddRange(await driverTask);

            // #942: perf spikes only if a CSV log has actually been replayed this session - never
            // auto-scans every historical log on disk (see CLAUDE.md's on-demand convention and
            // this task's own instructions).
            var replay = _logging.LastReplayResult;
            if (replay is not null)
                events.AddRange(TimelineService.DetectPerfSpikes(replay));

            _allEvents = events;
            RebuildLaneEvents();
            RebuildFilteredView();
            RebuildCorrelations();
            RebuildMarkerDetail();

            StatusText = replay is null
                ? "No CSV log loaded this session - load one from the Logging tab to populate the Perf spikes lane."
                : null;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't fully load the timeline: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildLaneEvents()
    {
        foreach (var lane in Lanes)
        {
            lane.Events.Clear();
            foreach (var e in _allEvents.Where(e => e.Lane == lane.Lane).OrderBy(e => e.Timestamp))
                lane.Events.Add(e);
        }
    }

    private (DateTime StartLocal, DateTime EndLocal) ResolveWindow()
    {
        DateTime end = DateTime.Now;
        DateTime start = RangePreset switch
        {
            "24h" => end.AddHours(-24),
            "7d" => end.AddDays(-7),
            "30d" => end.AddDays(-30),
            "90d" => end.AddDays(-90),
            _ => _allEvents.Count > 0 ? _allEvents.Min(e => e.Timestamp) : end.AddDays(-7), // "all"
        };
        return (start, end);
    }

    private void RebuildFilteredView()
    {
        var (start, end) = ResolveWindow();
        WindowStartLocal = start;
        WindowEndLocal = end;
        OnPropertyChanged(nameof(WindowRangeText));

        var visibleLanes = Lanes.Where(l => l.IsVisible).Select(l => l.Lane).ToHashSet();
        FilteredEvents.Clear();
        foreach (var e in _allEvents
                     .Where(e => visibleLanes.Contains(e.Lane) && e.Timestamp >= start && e.Timestamp <= end)
                     .OrderByDescending(e => e.Timestamp))
        {
            FilteredEvents.Add(e);
        }
    }

    private void RebuildCorrelations()
    {
        CorrelationFindings.Clear();
        foreach (var f in TimelineService.ComputeCorrelations(_allEvents, TimeSpan.FromHours(CorrelationWindowHours)))
            CorrelationFindings.Add(f.Headline);
    }

    private void RebuildMarkerDetail()
    {
        AroundSelectedMarker.Clear();
        if (SelectedMarker is null) return;

        double windowHours = DetailWindowHours;
        foreach (var e in _allEvents
                     .Where(e => Math.Abs((e.Timestamp - SelectedMarker.Timestamp).TotalHours) <= windowHours)
                     .OrderBy(e => e.Timestamp))
        {
            AroundSelectedMarker.Add(e);
        }
    }

    private void AddNote(DateTime timestamp, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var notes = TimelineNotesService.Load();
        notes.Add(new TimelineNoteEntry { Timestamp = timestamp, Text = text });
        TimelineNotesService.Save(notes);
        NewNoteText = string.Empty;

        var noteEvent = new TimelineEvent
        {
            Lane = TimelineLane.Notes,
            Timestamp = timestamp,
            Title = text.Length > 60 ? text[..60] + "…" : text,
            Detail = text,
            Source = "User note",
            IsFailure = false,
        };
        _allEvents.Add(noteEvent);

        var noteLane = _laneByKey[TimelineLane.Notes];
        var ordered = noteLane.Events.Append(noteEvent).OrderBy(e => e.Timestamp).ToList();
        noteLane.Events.Clear();
        foreach (var e in ordered) noteLane.Events.Add(e);

        RebuildFilteredView();
        RebuildMarkerDetail();
    }

    private void ExportRange()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export timeline range",
            Filter = "Markdown files (*.md)|*.md|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"TaskManagerPlus-Timeline-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            bool csv = Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, csv ? BuildCsv() : BuildMarkdown());
        }
        catch
        {
            // Best-effort - a failed write shouldn't crash the app; the user can just retry.
        }
    }

    private string BuildMarkdown()
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# Task Manager Plus timeline export");
        Line($"Range: {WindowRangeText}");
        Line();
        Line("| Time | Lane | Title | Detail | Source |");
        Line("|---|---|---|---|---|");
        foreach (var e in FilteredEvents.OrderBy(e => e.Timestamp))
        {
            string detail = e.Detail.Replace("\n", " ").Replace("\r", "").Replace("|", "\\|");
            string title = e.Title.Replace("|", "\\|");
            string source = e.Source.Replace("|", "\\|");
            Line($"| {e.Timestamp:g} | {e.LaneDisplayName} | {title} | {detail} | {source} |");
        }
        return sb.ToString();
    }

    private string BuildCsv()
    {
        var sb = new StringBuilder();
        static string Escape(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";

        sb.Append("Time,Lane,Title,Detail,Source,IsFailure\n");
        foreach (var e in FilteredEvents.OrderBy(e => e.Timestamp))
        {
            sb.Append(string.Join(",",
                Escape(e.Timestamp.ToString("s")), Escape(e.LaneDisplayName), Escape(e.Title),
                Escape(e.Detail), Escape(e.Source), e.IsFailure.ToString())).Append('\n');
        }
        return sb.ToString();
    }
}
