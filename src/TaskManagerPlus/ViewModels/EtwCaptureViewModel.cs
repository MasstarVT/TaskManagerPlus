using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs #146-153's "ETW trace capture" panel - a toggleable overlay reachable from the Events
/// tab, the same pattern #113's Provider Catalog panel already established (see
/// EventsViewModel.IsProviderCatalogOpen and its Border/DataTrigger in EventsView.xaml) rather than
/// a fourteenth top-level tab. Composed into EventsViewModel as <c>Etw</c> instead of being folded
/// directly onto EventsViewModel's own already-large property surface (1600+ lines before this
/// even existed) - this is the first sub-ViewModel composition in this file, but it mirrors exactly
/// how MainViewModel composes each tab's own ViewModel, just one level deeper.
///
/// Everything here is button-gated (no DispatcherTimer polling any data) per CLAUDE.md's on-demand
/// rule - logman/wpr/tracerpt shell-outs are all at least as expensive as the event-log scans that
/// rule already covers. The one timer this class does own (<see cref="_elapsedTimer"/>) only
/// re-renders an in-app elapsed-time readout while a capture started in *this* app session is
/// running; it never re-runs any capture/query logic itself, which is the distinction CLAUDE.md's
/// own on-demand rule draws.
/// </summary>
public sealed class EtwCaptureViewModel : ObservableObject, IDisposable
{
    private bool _isPanelOpen;
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value) || !value) return;
            // First open: load the two cheap catalogs automatically: bare session list (146) and
            // providers (148, also feeds #147's GUID->name lookup). Autologgers and session detail
            // stay behind their own explicit buttons since they're each a heavier read.
            if (Sessions.Count == 0 && RefreshSessionsCommand.CanExecute(null)) RefreshSessionsCommand.Execute(null);
            if (EtwProviders.Count == 0 && LoadEtwProvidersCommand.CanExecute(null)) LoadEtwProvidersCommand.Execute(null);
            // #160: also cheap (a couple of file/registry checks, no shell-out unless PATH doesn't
            // already have it) - worth doing on first open rather than waiting for the user to
            // stumble on a "Detect" button in the handoff section.
            if (IsWpaInstalled is null && DetectWpaCommand.CanExecute(null)) DetectWpaCommand.Execute(null);
        }
    }

    // ==================== #146: ETW session inspector ====================

    public ObservableCollection<EtwSessionRow> Sessions { get; } = new();

    private bool _isSessionsLoading;
    public bool IsSessionsLoading { get => _isSessionsLoading; private set => SetProperty(ref _isSessionsLoading, value); }

    private string? _sessionsStatusText;
    public string? SessionsStatusText { get => _sessionsStatusText; private set => SetProperty(ref _sessionsStatusText, value); }

    private EtwSessionRow? _selectedSession;
    public EtwSessionRow? SelectedSession { get => _selectedSession; set => SetProperty(ref _selectedSession, value); }

    public AsyncRelayCommand RefreshSessionsCommand { get; }

    /// <summary>Loads the bare Name/Type/Status list, then loads every session's detail (provider
    /// count, buffers, loss counters) with modest bounded concurrency - a system commonly has
    /// several dozen ETW sessions, and each detail query is its own logman.exe process, so this
    /// still runs several seconds slower than the initial list alone even bounded. Fine as an
    /// explicit "Refresh" button press; never on a tick.</summary>
    private async Task RefreshSessionsAsync()
    {
        IsSessionsLoading = true;
        SessionsStatusText = "Loading ETW sessions...";
        try
        {
            var list = await EtwTraceService.QueryRunningSessionsAsync();
            Sessions.Clear();
            foreach (var row in list) Sessions.Add(row);
            SessionsStatusText = list.Count == 0 ? "No running ETW sessions found (or logman is unavailable)." : $"{list.Count} session(s) found - loading detail...";

            using var gate = new SemaphoreSlim(4);
            var detailTasks = list.Select(async row =>
            {
                await gate.WaitAsync();
                try { return await EtwTraceService.QuerySessionDetailAsync(row.Name); }
                finally { gate.Release(); }
            }).ToList();

            var details = await Task.WhenAll(detailTasks);
            var byName = details.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Sessions)
            {
                if (!byName.TryGetValue(row.Name, out var detail)) continue;
                row.DetailLoaded = detail.DetailLoaded;
                row.DetailError = detail.DetailError;
                row.ProviderCount = detail.ProviderCount;
                row.Providers = detail.Providers;
                row.BufferSizeText = detail.BufferSizeText;
                row.BufferCountText = detail.BufferCountText;
                row.IsRealTime = detail.IsRealTime;
                row.LogFileName = detail.LogFileName;
                row.EventsLost = detail.EventsLost;
                row.BuffersLost = detail.BuffersLost;
                row.RawDetailText = detail.RawDetailText;
            }

            int lossy = Sessions.Count(r => r.HasLoss);
            SessionsStatusText = lossy > 0
                ? $"{Sessions.Count} session(s) loaded - {lossy} reporting lost events/buffers."
                : $"{Sessions.Count} session(s) loaded.";
            // EtwSessionRow is a plain POCO (no INotifyPropertyChanged) and the rows above were
            // mutated in place rather than replaced, so the DataGrid's cell bindings won't know
            // anything changed on their own - force the bound view to re-pull every cell.
            CollectionViewSource.GetDefaultView(Sessions).Refresh();
        }
        catch (Exception ex)
        {
            SessionsStatusText = $"Couldn't load ETW sessions: {ex.Message}";
        }
        finally
        {
            IsSessionsLoading = false;
        }
    }

    // ==================== #147: autologger inspector ====================

    public ObservableCollection<AutologgerRow> Autologgers { get; } = new();

    private bool _isAutologgersLoading;
    public bool IsAutologgersLoading { get => _isAutologgersLoading; private set => SetProperty(ref _isAutologgersLoading, value); }

    private string? _autologgersStatusText;
    public string? AutologgersStatusText { get => _autologgersStatusText; private set => SetProperty(ref _autologgersStatusText, value); }

    private AutologgerRow? _selectedAutologger;
    public AutologgerRow? SelectedAutologger { get => _selectedAutologger; set => SetProperty(ref _selectedAutologger, value); }

    public AsyncRelayCommand RefreshAutologgersCommand { get; }

    private Task RefreshAutologgersAsync()
    {
        IsAutologgersLoading = true;
        try
        {
            // Resolve provider GUIDs to friendly names using whatever #148 catalog is already
            // loaded - purely cosmetic, degrades to raw GUIDs if the catalog hasn't been loaded yet.
            var nameByGuid = EtwProviders.Count > 0
                ? EtwProviders.Where(p => !string.IsNullOrEmpty(p.Guid))
                    .GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase)
                : null;

            var list = EtwTraceService.ReadAutologgers(nameByGuid);
            Autologgers.Clear();
            foreach (var row in list) Autologgers.Add(row);
            AutologgersStatusText = list.Count == 0
                ? "No autologgers found (or the registry key couldn't be read)."
                : $"{list.Count} autologger(s) found - {list.Count(r => r.Enabled)} enabled.";
        }
        catch (Exception ex)
        {
            AutologgersStatusText = $"Couldn't read autologgers: {ex.Message}";
        }
        finally
        {
            IsAutologgersLoading = false;
        }
        return Task.CompletedTask;
    }

    // ==================== #148: ETW provider catalog ("ETW Providers") ====================

    public ObservableCollection<EtwProviderRow> EtwProviders { get; } = new();
    public ICollectionView EtwProvidersView { get; }

    private bool _isEtwProvidersLoading;
    public bool IsEtwProvidersLoading { get => _isEtwProvidersLoading; private set => SetProperty(ref _isEtwProvidersLoading, value); }

    private string _etwProviderSearchText = string.Empty;
    public string EtwProviderSearchText
    {
        get => _etwProviderSearchText;
        set { if (SetProperty(ref _etwProviderSearchText, value)) EtwProvidersView.Refresh(); }
    }

    private EtwProviderRow? _selectedEtwProvider;
    public EtwProviderRow? SelectedEtwProvider
    {
        get => _selectedEtwProvider;
        set
        {
            if (!SetProperty(ref _selectedEtwProvider, value)) return;
            ComputeListeningSessions();
            // #155/#156: convenience auto-fill - picking a provider here also seeds the real-time
            // tail and CSV-extract provider fields below, so the common path (pick a provider once,
            // then either tail it live or pull it out of a saved trace) doesn't require retyping
            // the name. Only fills while that field's own operation isn't already running, so it
            // never clobbers a name someone is mid-edit on.
            if (value is not null)
            {
                if (!IsTailing) TailProviderName = value.Name;
                if (!IsExtracting) ExtractProviderName = value.Name;
            }
        }
    }

    public ObservableCollection<EtwProviderSessionUsage> ListeningSessions { get; } = new();
    private string? _listeningSessionsStatusText;
    public string? ListeningSessionsStatusText { get => _listeningSessionsStatusText; private set => SetProperty(ref _listeningSessionsStatusText, value); }

    public AsyncRelayCommand LoadEtwProvidersCommand { get; }

    private async Task LoadEtwProvidersAsync()
    {
        IsEtwProvidersLoading = true;
        try
        {
            var list = await EtwTraceService.QueryEtwProvidersAsync();
            EtwProviders.Clear();
            foreach (var p in list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) EtwProviders.Add(p);
        }
        finally
        {
            IsEtwProvidersLoading = false;
        }
    }

    /// <summary>#148's "who's listening" - filters whatever session detail #146 already loaded,
    /// in memory. Explains itself when no session detail is loaded yet rather than silently
    /// showing an empty (and misleadingly "nobody's listening") list.</summary>
    private void ComputeListeningSessions()
    {
        ListeningSessions.Clear();
        if (_selectedEtwProvider is null) { ListeningSessionsStatusText = null; return; }

        if (!Sessions.Any(s => s.DetailLoaded))
        {
            ListeningSessionsStatusText = "Load ETW sessions above first (session detail is what \"who's listening\" is read from).";
            return;
        }

        var usage = EtwTraceService.FindListeningSessions(_selectedEtwProvider, Sessions);
        foreach (var u in usage) ListeningSessions.Add(u);
        ListeningSessionsStatusText = usage.Count == 0
            ? "No currently-running session has this provider enabled."
            : $"{usage.Count} running session(s) have this provider enabled.";
    }

    // ==================== #155: real-time provider tail (no WPA needed) ====================

    private string _tailProviderName = string.Empty;
    public string TailProviderName { get => _tailProviderName; set => SetProperty(ref _tailProviderName, value); }

    private bool _isTailing;
    public bool IsTailing { get => _isTailing; private set => SetProperty(ref _isTailing, value); }

    private string? _tailStatusText;
    public string? TailStatusText { get => _tailStatusText; private set => SetProperty(ref _tailStatusText, value); }

    // Bounded so a chatty provider's scrolling text pane can't grow without limit - keeps roughly
    // the most recent ~150 KB of output once the buffer crosses ~200 KB.
    private readonly StringBuilder _tailBuffer = new();
    private string _tailOutputText = string.Empty;
    public string TailOutputText { get => _tailOutputText; private set => SetProperty(ref _tailOutputText, value); }

    private EtwTraceService.EtwRealtimeTailHandle? _tailHandle;

    public AsyncRelayCommand StartTailCommand { get; }
    public RelayCommand StopTailCommand { get; }
    public RelayCommand ClearTailOutputCommand { get; }

    private async Task StartTailAsync()
    {
        if (IsTailing) return;
        string provider = TailProviderName.Trim();
        if (provider.Length == 0) { TailStatusText = "Enter or pick a provider (above) first."; return; }

        TailStatusText = "Starting real-time session...";
        _tailBuffer.Clear();
        TailOutputText = string.Empty;

        var (handle, message) = await EtwTraceService.StartRealtimeTailAsync(
            provider,
            onLine: line => Application.Current?.Dispatcher.BeginInvoke(() => AppendTailLine(line)),
            onExited: msg => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TailStatusText = msg;
                IsTailing = false;
                _tailHandle = null;
            }));

        if (handle is null)
        {
            TailStatusText = message;
            return;
        }

        _tailHandle = handle;
        IsTailing = true;
        TailStatusText = message;
    }

    private void AppendTailLine(string line)
    {
        _tailBuffer.AppendLine(line);
        if (_tailBuffer.Length > 200_000) _tailBuffer.Remove(0, _tailBuffer.Length - 150_000);
        TailOutputText = _tailBuffer.ToString();
    }

    /// <summary>Stops a tail this panel started - guaranteed teardown (kill tracerpt, `logman stop`
    /// + `logman delete`) via EtwRealtimeTailHandle.Dispose(). The same handle is also disposed from
    /// this ViewModel's own Dispose() below, so app exit tears down a still-running tail exactly the
    /// same way an explicit Stop click does.</summary>
    private void StopTail()
    {
        _tailHandle?.Dispose();
        _tailHandle = null;
        IsTailing = false;
        TailStatusText = "Real-time tail stopped.";
    }

    private void ClearTailOutput()
    {
        _tailBuffer.Clear();
        TailOutputText = string.Empty;
    }

    // ==================== #156: extract one provider's events from an .etl to CSV ====================

    private string _extractProviderName = string.Empty;
    public string ExtractProviderName { get => _extractProviderName; set => SetProperty(ref _extractProviderName, value); }

    private string _extractSourceEtlPath = string.Empty;
    public string ExtractSourceEtlPath { get => _extractSourceEtlPath; set => SetProperty(ref _extractSourceEtlPath, value); }

    private bool _isExtracting;
    public bool IsExtracting { get => _isExtracting; private set => SetProperty(ref _isExtracting, value); }

    private string? _extractStatusText;
    public string? ExtractStatusText { get => _extractStatusText; private set => SetProperty(ref _extractStatusText, value); }

    public RelayCommand BrowseExtractSourceCommand { get; }
    public AsyncRelayCommand ExtractProviderCsvCommand { get; }

    /// <summary>#156's "next to the trace file in the capture history list" note doesn't apply here -
    /// this ViewModel has no capture-history list (only the single most recent LastEtlPath), so this
    /// is the "browse for an .etl file" substitute the task instructions call out as acceptable when
    /// that's the case. Defaults to whatever trace was last captured/summarized/collected in this
    /// session (LastEtlPath), when there is one.</summary>
    private void BrowseExtractSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a trace to extract from",
            Filter = "ETL trace files (*.etl)|*.etl|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Traces"),
        };
        if (dialog.ShowDialog() == true) ExtractSourceEtlPath = dialog.FileName;
    }

    private async Task ExtractProviderCsvAsync()
    {
        string source = ExtractSourceEtlPath.Trim();
        string provider = ExtractProviderName.Trim();
        if (source.Length == 0) { ExtractStatusText = "Choose a trace file first."; return; }
        if (provider.Length == 0) { ExtractStatusText = "Enter or pick a provider (above) first."; return; }

        var saveDialog = new SaveFileDialog
        {
            Title = "Save extracted events as",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{Path.GetFileNameWithoutExtension(source)}-{SanitizeFileNamePart(provider)}.csv",
            InitialDirectory = Path.GetDirectoryName(source) is { Length: > 0 } dir ? dir : AppPaths.GetPath("Traces"),
        };
        if (saveDialog.ShowDialog() != true) return;

        IsExtracting = true;
        ExtractStatusText = "Exporting to CSV (this re-parses the whole .etl, so a large trace can take a minute)...";
        try
        {
            var (_, message, _) = await EtwTraceService.ExtractProviderEventsToCsvAsync(source, provider, saveDialog.FileName);
            ExtractStatusText = message;
        }
        finally
        {
            IsExtracting = false;
        }
    }

    private static string SanitizeFileNamePart(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 40 ? s[..40] : s;
    }

    // ==================== #149/#150: capture ====================

    public List<EtwCapturePreset> Presets { get; } = EtwTraceService.GetCapturePresets();

    private string _captureOutputPath = DefaultCapturePath();
    public string CaptureOutputPath { get => _captureOutputPath; set => SetProperty(ref _captureOutputPath, value); }

    private static string DefaultCapturePath() => Path.Combine(AppPaths.GetPath("Traces"), $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.etl");

    public RelayCommand BrowseCaptureOutputCommand { get; }

    private void BrowseCaptureOutput()
    {
        try { Directory.CreateDirectory(AppPaths.GetPath("Traces")); } catch { /* SaveFileDialog will surface any real problem */ }
        var dialog = new SaveFileDialog
        {
            Title = "Save trace as",
            Filter = "ETL trace files (*.etl)|*.etl|All files (*.*)|*.*",
            FileName = Path.GetFileName(CaptureOutputPath),
            InitialDirectory = AppPaths.GetPath("Traces"),
        };
        if (dialog.ShowDialog() == true) CaptureOutputPath = dialog.FileName;
    }

    private bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; private set => SetProperty(ref _isCapturing, value); }

    private string? _activeCaptureName;
    public string? ActiveCaptureName { get => _activeCaptureName; private set => SetProperty(ref _activeCaptureName, value); }

    private DateTime _captureStartedAtUtc;
    private readonly DispatcherTimer _elapsedTimer;

    private string _elapsedText = "00:00";
    /// <summary>Live elapsed-time readout while a capture is running - purely a UI re-render tick,
    /// never re-runs the capture itself (see class remarks on the on-demand rule this doesn't
    /// violate).</summary>
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }

    private string? _captureStatusText;
    public string? CaptureStatusText { get => _captureStatusText; private set => SetProperty(ref _captureStatusText, value); }

    // ==================== #157: trace-file growth watchdog ====================

    private readonly DispatcherTimer _growthTimer;
    private EtwCaptureSizeSample? _lastGrowthSample;

    private string? _captureSizeText;
    /// <summary>Live "how big is this capture so far" readout - see EtwTraceService.
    /// SampleCaptureSize's remarks on exactly what this measures while WPR is still recording.</summary>
    public string? CaptureSizeText { get => _captureSizeText; private set => SetProperty(ref _captureSizeText, value); }

    private string? _captureGrowthRateText;
    public string? CaptureGrowthRateText { get => _captureGrowthRateText; private set => SetProperty(ref _captureGrowthRateText, value); }

    private string? _captureProjectedSizeText;
    public string? CaptureProjectedSizeText { get => _captureProjectedSizeText; private set => SetProperty(ref _captureProjectedSizeText, value); }

    private string? _captureSpaceWarningText;
    /// <summary>Non-null only when the capture's drive is already below the safety threshold, or is
    /// projected to drop below it within the next 10 minutes at the current growth rate - null the
    /// rest of the time, so the UI's warning banner stays hidden rather than showing a stale "all
    /// clear" message.</summary>
    public string? CaptureSpaceWarningText { get => _captureSpaceWarningText; private set => SetProperty(ref _captureSpaceWarningText, value); }

    private const long GrowthWatchdogFreeSpaceThresholdBytes = 1024L * 1024 * 1024;

    /// <summary>Ticks every 5s while a capture is running - a lightweight file-size/free-space
    /// re-render, never a shell-out, so this doesn't violate CLAUDE.md's on-demand rule any more
    /// than #149's own elapsed-time timer does (see this class's own remarks on that distinction).</summary>
    private void GrowthTimerTick(object? sender, EventArgs e)
    {
        var sample = EtwTraceService.SampleCaptureSize(CaptureOutputPath);
        if (!sample.Available)
        {
            CaptureSizeText = "Unknown";
            CaptureGrowthRateText = null;
            CaptureProjectedSizeText = null;
            return;
        }

        CaptureSizeText = Formatting.FormatBytes(sample.TotalBytes);

        double? bytesPerSec = null;
        if (_lastGrowthSample is { } prev)
        {
            double seconds = (sample.SampledAtUtc - prev.SampledAtUtc).TotalSeconds;
            if (seconds > 0.5) bytesPerSec = Math.Max(0, (sample.TotalBytes - prev.TotalBytes) / seconds);
        }
        _lastGrowthSample = sample;

        if (bytesPerSec is not { } rate)
        {
            CaptureGrowthRateText = "Measuring...";
            CaptureProjectedSizeText = null;
            return;
        }

        CaptureGrowthRateText = Formatting.FormatByteRate(rate);
        long projectedIn10Min = sample.TotalBytes + (long)(rate * 600);
        CaptureProjectedSizeText = $"~{Formatting.FormatBytes(projectedIn10Min)} in 10 more minutes at this rate";

        long? freeBytes = EtwTraceService.GetAvailableFreeBytes(CaptureOutputPath);
        if (freeBytes is not { } free)
        {
            CaptureSpaceWarningText = null;
            return;
        }

        if (free < GrowthWatchdogFreeSpaceThresholdBytes)
        {
            CaptureSpaceWarningText = $"Only {Formatting.FormatBytes(free)} free on the capture's drive - already below the 1 GB safety threshold.";
        }
        else if (rate > 0)
        {
            double secondsToThreshold = (free - GrowthWatchdogFreeSpaceThresholdBytes) / rate;
            CaptureSpaceWarningText = secondsToThreshold < 600
                ? $"At this growth rate, free space on the capture's drive will drop below 1 GB in about {TimeSpan.FromSeconds(secondsToThreshold):mm\\:ss}."
                : null;
        }
        else
        {
            CaptureSpaceWarningText = null;
        }
    }

    public AsyncRelayCommand StartGeneralCaptureCommand { get; }
    public AsyncRelayCommand StartPresetCaptureCommand { get; }
    public AsyncRelayCommand StopCaptureCommand { get; }

    private async Task StartGeneralCaptureAsync() => await StartCaptureCoreAsync(new[] { "GeneralProfile" }, "General profile (first-level triage)");

    private async Task StartPresetCaptureAsync(EtwCapturePreset? preset)
    {
        if (preset is null) return;
        await StartCaptureCoreAsync(preset.WprProfiles, preset.Name);
    }

    /// <summary>#149's pre-checks (free disk space, no other WPR session already running) before
    /// actually starting - both are worth doing up front since a failed `wpr -start` after the
    /// fact is a worse experience than catching it here, even though StartCaptureAsync itself also
    /// translates a race-lost "already recording" failure via #152's ExplainWprError.</summary>
    private async Task StartCaptureCoreAsync(IReadOnlyList<string> profiles, string label)
    {
        if (IsCapturing) return;

        string outputPath = CaptureOutputPath;
        try { Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppPaths.GetPath("Traces")); }
        catch (Exception ex) { CaptureStatusText = $"Couldn't create the output folder: {ex.Message}"; return; }

        var (spaceOk, spaceMessage) = EtwTraceService.CheckFreeDiskSpace(outputPath);
        if (!spaceOk) { CaptureStatusText = spaceMessage; return; }

        CaptureStatusText = "Checking for an already-running capture...";
        var status = await EtwTraceService.GetWprStatusAsync();
        if (status.IsRecording)
        {
            CaptureStatusText = "Another WPR trace capture is already running. Check \"Trace status & rescue\" below to see it, or cancel it, before starting a new one.";
            return;
        }

        CaptureStatusText = $"Starting \"{label}\" capture...";
        var (success, message) = await EtwTraceService.StartCaptureAsync(profiles);
        if (!success)
        {
            CaptureStatusText = message;
            return;
        }

        IsCapturing = true;
        ActiveCaptureName = label;
        _captureStartedAtUtc = DateTime.UtcNow;
        ElapsedText = "00:00";
        _elapsedTimer.Start();

        _lastGrowthSample = null;
        CaptureSizeText = null; CaptureGrowthRateText = null; CaptureProjectedSizeText = null; CaptureSpaceWarningText = null;
        _growthTimer.Start();

        CaptureStatusText = $"Recording \"{label}\" - click Stop when you've reproduced the issue.";
    }

    private async Task StopCaptureAsync()
    {
        if (!IsCapturing) return;
        _elapsedTimer.Stop();
        _growthTimer.Stop();
        string outputPath = CaptureOutputPath;

        CaptureStatusText = "Stopping and merging the trace - this can take a minute or two for a longer capture...";
        var (success, message) = await EtwTraceService.StopCaptureAsync(outputPath);
        IsCapturing = false;
        ActiveCaptureName = null;

        if (!success)
        {
            CaptureStatusText = message;
            return;
        }

        CaptureStatusText = message;
        LastEtlPath = outputPath;
        ExtractSourceEtlPath = outputPath;
        CaptureOutputPath = DefaultCapturePath();

        // #153: automatic summary right after a successful capture.
        _ = SummarizeAsync(outputPath);
    }

    private void ElapsedTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _captureStartedAtUtc;
        ElapsedText = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    // ==================== #151: boot trace ====================

    private bool _isBootTraceBusy;
    public bool IsBootTraceBusy { get => _isBootTraceBusy; private set => SetProperty(ref _isBootTraceBusy, value); }

    private BootTraceMarker? _bootTraceMarker;

    private bool _isBootTracePending;
    /// <summary>True once a boot trace has been armed and is waiting to be collected after the
    /// next reboot - drives the reminder banner. Reloaded from disk in the constructor, so this is
    /// true again on the very first launch after the reboot the trace was armed for, exactly the
    /// "was a boot trace pending?" check #151 asks for.</summary>
    public bool IsBootTracePending { get => _isBootTracePending; private set => SetProperty(ref _isBootTracePending, value); }

    public string? BootTracePendingText => _bootTraceMarker is null
        ? null
        : $"A boot trace was armed on {_bootTraceMarker.ArmedAtUtc.ToLocalTime():g} and should have recorded during the most recent boot. "
          + $"Collect it now to save \"{Path.GetFileName(_bootTraceMarker.EtlPath)}\", or cancel if you no longer need it.";

    private string? _bootTraceStatusText;
    public string? BootTraceStatusText { get => _bootTraceStatusText; private set => SetProperty(ref _bootTraceStatusText, value); }

    public RelayCommand ArmBootTraceCommand { get; }
    public AsyncRelayCommand CollectBootTraceCommand { get; }
    public AsyncRelayCommand CancelBootTraceCommand { get; }
    public RelayCommand DismissBootTraceBannerCommand { get; }

    /// <summary>#151: a real confirmation dialog (not just a code comment) explaining plainly that
    /// this requires a reboot before anything is armed.</summary>
    private void ArmBootTrace()
    {
        var confirm = MessageBox.Show(
            "This arms a one-time trace of your next Windows boot (wpr -addboot). "
            + "It will NOT start recording now - you need to restart the computer for it to actually capture anything. "
            + "After the next boot, come back to this panel to collect the resulting trace file.\n\n"
            + "Arm the boot trace now? (You can restart whenever you're ready - this doesn't reboot for you.)",
            "Trace the next boot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = ArmBootTraceCoreAsync();
    }

    private async Task ArmBootTraceCoreAsync()
    {
        IsBootTraceBusy = true;
        try
        {
            string etlPath = Path.Combine(AppPaths.GetPath("Traces"), $"boot-trace-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
            try { Directory.CreateDirectory(AppPaths.GetPath("Traces")); } catch { /* wpr -addboot itself doesn't need this to exist yet */ }

            var (success, message) = await EtwTraceService.ArmBootTraceAsync();
            BootTraceStatusText = message;
            if (!success) return;

            _bootTraceMarker = new BootTraceMarker { EtlPath = etlPath, ArmedAtUtc = DateTime.UtcNow };
            SaveBootTraceMarker(_bootTraceMarker);
            IsBootTracePending = true;
            OnPropertyChanged(nameof(BootTracePendingText));
        }
        finally
        {
            IsBootTraceBusy = false;
        }
    }

    private async Task CollectBootTraceAsync()
    {
        if (_bootTraceMarker is null) return;
        IsBootTraceBusy = true;
        try
        {
            var (success, message) = await EtwTraceService.CollectBootTraceAsync(_bootTraceMarker.EtlPath);
            BootTraceStatusText = message;
            if (!success) return;

            LastEtlPath = _bootTraceMarker.EtlPath;
            ExtractSourceEtlPath = _bootTraceMarker.EtlPath;
            ClearBootTraceMarker();
            _ = SummarizeAsync(_bootTraceMarker.EtlPath);
        }
        finally
        {
            IsBootTraceBusy = false;
        }
    }

    private async Task CancelBootTraceAsync()
    {
        IsBootTraceBusy = true;
        try
        {
            var (success, message) = await EtwTraceService.CancelBootTraceAsync();
            BootTraceStatusText = message;
            if (!success) return;
            ClearBootTraceMarker();
        }
        finally
        {
            IsBootTraceBusy = false;
        }
    }

    private void DismissBootTraceBanner()
    {
        // Hides this session's banner without discarding the armed trace - it reappears on the
        // next launch (marker file untouched) until Collect or Cancel actually resolves it.
        IsBootTracePending = false;
    }

    private void ClearBootTraceMarker()
    {
        _bootTraceMarker = null;
        IsBootTracePending = false;
        SaveBootTraceMarker(null);
        OnPropertyChanged(nameof(BootTracePendingText));
    }

    private static string BootTraceMarkerPath => AppPaths.GetPath("etw-boot-trace-pending.json");

    private static BootTraceMarker? LoadBootTraceMarker()
    {
        try
        {
            if (!File.Exists(BootTraceMarkerPath)) return null;
            return JsonSerializer.Deserialize<BootTraceMarker>(File.ReadAllText(BootTraceMarkerPath));
        }
        catch { return null; } // corrupt/missing marker - same as "no boot trace pending"
    }

    private static void SaveBootTraceMarker(BootTraceMarker? marker)
    {
        try
        {
            if (marker is null) { if (File.Exists(BootTraceMarkerPath)) File.Delete(BootTraceMarkerPath); return; }
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(BootTraceMarkerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort - worst case the reminder banner doesn't survive a restart */ }
    }

    // ==================== #152: trace status & rescue ====================

    private WprStatusResult? _wprStatus;
    public WprStatusResult? WprStatus
    {
        get => _wprStatus;
        private set { if (SetProperty(ref _wprStatus, value)) OnPropertyChanged(nameof(IsWprRecording)); }
    }

    /// <summary>Null-safe passthrough for XAML - binding straight to "WprStatus.IsRecording" would
    /// leave Visibility at its unset default while WprStatus is still null (before the first
    /// status refresh), rather than reliably Collapsed.</summary>
    public bool IsWprRecording => _wprStatus?.IsRecording ?? false;

    private bool _isStatusLoading;
    public bool IsStatusLoading { get => _isStatusLoading; private set => SetProperty(ref _isStatusLoading, value); }

    public AsyncRelayCommand RefreshWprStatusCommand { get; }
    public AsyncRelayCommand RescueCancelCommand { get; }

    private async Task RefreshWprStatusAsync()
    {
        IsStatusLoading = true;
        try { WprStatus = await EtwTraceService.GetWprStatusAsync(); }
        finally { IsStatusLoading = false; }
    }

    /// <summary>#152's rescue action - discards a capture left running (very commonly one an app
    /// or the user forgot about, quietly filling the disk and costing CPU) without trying to save
    /// it. Distinct from <see cref="StopCaptureCommand"/>, which only applies to a capture this
    /// view model itself started and knows the output path for.</summary>
    private async Task RescueCancelAsync()
    {
        IsStatusLoading = true;
        try
        {
            var (success, message) = await EtwTraceService.CancelCaptureAsync();
            CaptureStatusText = message;
            if (IsCapturing) { IsCapturing = false; ActiveCaptureName = null; _elapsedTimer.Stop(); _growthTimer.Stop(); }
            if (success) WprStatus = await EtwTraceService.GetWprStatusAsync();
        }
        finally
        {
            IsStatusLoading = false;
        }
    }

    // ==================== #153: tracerpt summary ====================

    private string? _lastEtlPath;
    public string? LastEtlPath { get => _lastEtlPath; private set => SetProperty(ref _lastEtlPath, value); }

    private bool _isSummarizing;
    public bool IsSummarizing { get => _isSummarizing; private set => SetProperty(ref _isSummarizing, value); }

    private TracerptSummary? _lastSummary;
    public TracerptSummary? LastSummary { get => _lastSummary; private set => SetProperty(ref _lastSummary, value); }

    public AsyncRelayCommand SummarizeLastTraceCommand { get; }
    public RelayCommand PickAndSummarizeCommand { get; }
    public RelayCommand OpenHtmlReportCommand { get; }

    private Task SummarizeLastTraceAsync() => LastEtlPath is null ? Task.CompletedTask : SummarizeAsync(LastEtlPath);

    private void PickAndSummarize()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a trace to summarize",
            Filter = "ETL trace files (*.etl)|*.etl|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Traces"),
        };
        if (dialog.ShowDialog() != true) return;
        LastEtlPath = dialog.FileName;
        ExtractSourceEtlPath = dialog.FileName;
        _ = SummarizeAsync(dialog.FileName);
    }

    /// <summary>#153: runs tracerpt against a finished capture and parses its summary - called
    /// automatically right after a successful Stop (see StopCaptureAsync/CollectBootTraceAsync)
    /// and available as a manual re-run for a previously-saved trace via the two commands above.</summary>
    private async Task SummarizeAsync(string etlPath)
    {
        IsSummarizing = true;
        LastSummary = null;
        try
        {
            LastSummary = await EtwTraceService.RunTracerptSummaryAsync(etlPath);
        }
        finally
        {
            IsSummarizing = false;
        }
    }

    /// <summary>Opens the tracerpt HTML report in the user's default browser -
    /// UseShellExecute=true is the deliberate exception here (opening a document for the user to
    /// read, not consuming its output the way every other Process.Start in this app does).</summary>
    private void OpenHtmlReport()
    {
        string? path = LastSummary?.HtmlReportPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CaptureStatusText = $"Couldn't open the report: {ex.Message}";
        }
    }

    // ==================== #158: user-editable capture recipes ====================

    public ObservableCollection<EtwCaptureRecipe> Recipes { get; } = new();

    private EtwCaptureRecipe? _selectedRecipe;
    public EtwCaptureRecipe? SelectedRecipe { get => _selectedRecipe; set => SetProperty(ref _selectedRecipe, value); }

    private bool _isRunningRecipe;
    public bool IsRunningRecipe { get => _isRunningRecipe; private set => SetProperty(ref _isRunningRecipe, value); }

    private string? _recipeStatusText;
    public string? RecipeStatusText { get => _recipeStatusText; private set => SetProperty(ref _recipeStatusText, value); }

    // Small inline "add a recipe" form fields - a separate dialog window felt like overkill for
    // five short fields, and every other list-editing surface in this app (saved event filters,
    // the watchlist) is likewise just data-bound rows plus a small form, not a modal.
    private string _recipeEditName = string.Empty;
    public string RecipeEditName { get => _recipeEditName; set => SetProperty(ref _recipeEditName, value); }
    private string _recipeEditDescription = string.Empty;
    public string RecipeEditDescription { get => _recipeEditDescription; set => SetProperty(ref _recipeEditDescription, value); }
    private string _recipeEditTool = "wpr.exe";
    public string RecipeEditTool { get => _recipeEditTool; set => SetProperty(ref _recipeEditTool, value); }
    private string _recipeEditArguments = string.Empty;
    public string RecipeEditArguments { get => _recipeEditArguments; set => SetProperty(ref _recipeEditArguments, value); }
    private string _recipeEditSizeEstimate = string.Empty;
    public string RecipeEditSizeEstimate { get => _recipeEditSizeEstimate; set => SetProperty(ref _recipeEditSizeEstimate, value); }

    public RelayCommand AddRecipeCommand { get; }
    public RelayCommand RemoveRecipeCommand { get; }
    public RelayCommand ResetRecipesToDefaultsCommand { get; }
    public AsyncRelayCommand RunRecipeCommand { get; }

    private void LoadRecipes()
    {
        var settings = EtwRecipeSettingsService.Load();
        Recipes.Clear();
        foreach (var r in settings.Recipes) Recipes.Add(r);
    }

    private void SaveRecipes() => EtwRecipeSettingsService.Save(new EtwRecipeSettings { Recipes = Recipes.ToList() });

    private void AddRecipe()
    {
        if (string.IsNullOrWhiteSpace(RecipeEditName) || string.IsNullOrWhiteSpace(RecipeEditArguments))
        {
            RecipeStatusText = "A recipe needs at least a name and arguments.";
            return;
        }

        Recipes.Add(new EtwCaptureRecipe
        {
            Name = RecipeEditName.Trim(),
            Description = RecipeEditDescription.Trim(),
            Tool = string.IsNullOrWhiteSpace(RecipeEditTool) ? "wpr.exe" : RecipeEditTool.Trim(),
            Arguments = RecipeEditArguments.Trim(),
            ExpectedSizePerMinute = RecipeEditSizeEstimate.Trim(),
            IsBuiltIn = false,
        });
        SaveRecipes();
        RecipeStatusText = $"Added \"{RecipeEditName.Trim()}\".";
        RecipeEditName = string.Empty;
        RecipeEditDescription = string.Empty;
        RecipeEditArguments = string.Empty;
        RecipeEditSizeEstimate = string.Empty;
    }

    private void RemoveRecipe()
    {
        if (SelectedRecipe is null) return;
        Recipes.Remove(SelectedRecipe);
        SaveRecipes();
        SelectedRecipe = null;
    }

    private void ResetRecipesToDefaults()
    {
        var confirm = MessageBox.Show(
            "Replace your custom recipe list with the built-in defaults? This can't be undone.",
            "Reset recipes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Recipes.Clear();
        foreach (var r in EtwRecipeSettings.Defaults.Recipes) Recipes.Add(r);
        SaveRecipes();
    }

    private async Task RunRecipeAsync()
    {
        if (SelectedRecipe is null) return;
        IsRunningRecipe = true;
        RecipeStatusText = $"Running \"{SelectedRecipe.Name}\"...";
        try
        {
            try { Directory.CreateDirectory(AppPaths.GetPath("Traces")); } catch { /* RunRecipeAsync itself will surface any real problem */ }
            string outputPath = Path.Combine(AppPaths.GetPath("Traces"), $"recipe-{SanitizeFileNamePart(SelectedRecipe.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
            var (_, message) = await EtwTraceService.RunRecipeAsync(SelectedRecipe, outputPath);
            RecipeStatusText = message;
        }
        finally
        {
            IsRunningRecipe = false;
        }
    }

    // ==================== #159: stale trace-artifact finder ====================

    public ObservableCollection<EtwStaleArtifact> StaleArtifacts { get; } = new();

    private bool _isScanningStaleArtifacts;
    public bool IsScanningStaleArtifacts { get => _isScanningStaleArtifacts; private set => SetProperty(ref _isScanningStaleArtifacts, value); }

    private string? _staleArtifactsStatusText;
    public string? StaleArtifactsStatusText { get => _staleArtifactsStatusText; private set => SetProperty(ref _staleArtifactsStatusText, value); }

    public AsyncRelayCommand ScanStaleArtifactsCommand { get; }
    public RelayCommand RevealStaleArtifactCommand { get; }

    private async Task ScanStaleArtifactsAsync()
    {
        IsScanningStaleArtifacts = true;
        StaleArtifactsStatusText = "Scanning known trace-artifact locations...";
        try
        {
            var list = await Task.Run(EtwTraceService.ScanStaleTraceArtifacts);
            StaleArtifacts.Clear();
            foreach (var a in list) StaleArtifacts.Add(a);
            StaleArtifactsStatusText = list.Count == 0
                ? "No leftover .etl files found in the known trace-artifact locations."
                : $"{list.Count} file(s) found, {Formatting.FormatBytes(list.Sum(a => a.SizeBytes))} total.";
        }
        catch (Exception ex)
        {
            StaleArtifactsStatusText = $"Couldn't scan: {ex.Message}";
        }
        finally
        {
            IsScanningStaleArtifacts = false;
        }
    }

    private static void RevealStaleArtifact(object? parameter)
    {
        if (parameter is EtwStaleArtifact artifact) EtwTraceService.RevealInExplorer(artifact.Path);
    }

    // ==================== #160: trace handoff helper (WPA + notes.md stub) ====================

    private bool? _isWpaInstalled;
    public bool? IsWpaInstalled { get => _isWpaInstalled; private set { if (SetProperty(ref _isWpaInstalled, value)) OnPropertyChanged(nameof(IsWpaNotInstalled)); } }

    /// <summary>Null-safe passthrough for XAML - true only once detection has actually run and come
    /// back negative, never during the brief "still detecting" (null) state right after the panel
    /// opens. Same "computed bool for a nullable/derived state" shape as IsWprRecording above.</summary>
    public bool IsWpaNotInstalled => _isWpaInstalled == false;

    private string? _wpaPath;
    public string? WpaPath { get => _wpaPath; private set => SetProperty(ref _wpaPath, value); }

    public AsyncRelayCommand DetectWpaCommand { get; }
    public RelayCommand OpenInWpaCommand { get; }

    private string _notesSymptomText = string.Empty;
    public string NotesSymptomText { get => _notesSymptomText; set => SetProperty(ref _notesSymptomText, value); }

    private string? _notesStatusText;
    public string? NotesStatusText { get => _notesStatusText; private set => SetProperty(ref _notesStatusText, value); }

    private bool _isGeneratingNotes;
    public bool IsGeneratingNotes { get => _isGeneratingNotes; private set => SetProperty(ref _isGeneratingNotes, value); }

    public AsyncRelayCommand GenerateNotesCommand { get; }

    private async Task DetectWpaAsync()
    {
        string? path = await EtwTraceService.DetectWpaPathAsync();
        WpaPath = path;
        IsWpaInstalled = path is not null;
    }

    /// <summary>Hands the trace off to WPA - UseShellExecute=true is the same deliberate exception
    /// OpenHtmlReport above takes (opening a document/app for the user, not consuming its output).</summary>
    private void OpenInWpa()
    {
        if (WpaPath is null || LastEtlPath is null || !File.Exists(LastEtlPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(WpaPath, $"\"{LastEtlPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NotesStatusText = $"Couldn't open WPA: {ex.Message}";
        }
    }

    private async Task GenerateNotesAsync()
    {
        if (LastEtlPath is null) { NotesStatusText = "Summarize or pick a trace first (below) so there's a file to write notes next to."; return; }

        IsGeneratingNotes = true;
        NotesStatusText = "Reading machine specs...";
        try
        {
            var specs = await new SystemSpecsService().QueryAsync();
            var (_, message, _) = await EtwTraceService.GenerateNotesStubAsync(LastEtlPath, NotesSymptomText, specs);
            NotesStatusText = message;
        }
        catch (Exception ex)
        {
            NotesStatusText = $"Couldn't generate notes: {ex.Message}";
        }
        finally
        {
            IsGeneratingNotes = false;
        }
    }

    public EtwCaptureViewModel()
    {
        RefreshSessionsCommand = new AsyncRelayCommand(RefreshSessionsAsync, () => !IsSessionsLoading);
        RefreshAutologgersCommand = new AsyncRelayCommand(RefreshAutologgersAsync, () => !IsAutologgersLoading);

        LoadEtwProvidersCommand = new AsyncRelayCommand(LoadEtwProvidersAsync, () => !IsEtwProvidersLoading);
        EtwProvidersView = CollectionViewSource.GetDefaultView(EtwProviders);
        EtwProvidersView.Filter = o => o is EtwProviderRow p
            && (string.IsNullOrWhiteSpace(EtwProviderSearchText)
                || p.Name.Contains(EtwProviderSearchText, StringComparison.OrdinalIgnoreCase)
                || p.Guid.Contains(EtwProviderSearchText, StringComparison.OrdinalIgnoreCase));

        StartTailCommand = new AsyncRelayCommand(StartTailAsync, () => !IsTailing);
        StopTailCommand = new RelayCommand(_ => StopTail(), _ => IsTailing);
        ClearTailOutputCommand = new RelayCommand(_ => ClearTailOutput());

        BrowseExtractSourceCommand = new RelayCommand(_ => BrowseExtractSource(), _ => !IsExtracting);
        ExtractProviderCsvCommand = new AsyncRelayCommand(ExtractProviderCsvAsync, () => !IsExtracting);

        BrowseCaptureOutputCommand = new RelayCommand(_ => BrowseCaptureOutput(), _ => !IsCapturing);
        StartGeneralCaptureCommand = new AsyncRelayCommand(StartGeneralCaptureAsync, () => !IsCapturing);
        StartPresetCaptureCommand = new AsyncRelayCommand(p => StartPresetCaptureAsync(p as EtwCapturePreset), _ => !IsCapturing);
        StopCaptureCommand = new AsyncRelayCommand(StopCaptureAsync, () => IsCapturing);

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += ElapsedTimerTick;

        _growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _growthTimer.Tick += GrowthTimerTick;

        AddRecipeCommand = new RelayCommand(_ => AddRecipe());
        RemoveRecipeCommand = new RelayCommand(_ => RemoveRecipe(), _ => SelectedRecipe is not null);
        ResetRecipesToDefaultsCommand = new RelayCommand(_ => ResetRecipesToDefaults());
        RunRecipeCommand = new AsyncRelayCommand(RunRecipeAsync, () => !IsRunningRecipe && SelectedRecipe is not null);
        LoadRecipes();

        ScanStaleArtifactsCommand = new AsyncRelayCommand(ScanStaleArtifactsAsync, () => !IsScanningStaleArtifacts);
        RevealStaleArtifactCommand = new RelayCommand(RevealStaleArtifact);

        DetectWpaCommand = new AsyncRelayCommand(DetectWpaAsync, () => true);
        OpenInWpaCommand = new RelayCommand(_ => OpenInWpa(), _ => IsWpaInstalled == true && LastEtlPath is not null);
        GenerateNotesCommand = new AsyncRelayCommand(GenerateNotesAsync, () => !IsGeneratingNotes && LastEtlPath is not null);

        ArmBootTraceCommand = new RelayCommand(_ => ArmBootTrace(), _ => !IsBootTraceBusy && !IsBootTracePending);
        CollectBootTraceCommand = new AsyncRelayCommand(CollectBootTraceAsync, () => !IsBootTraceBusy && IsBootTracePending);
        CancelBootTraceCommand = new AsyncRelayCommand(CancelBootTraceAsync, () => !IsBootTraceBusy && IsBootTracePending);
        DismissBootTraceBannerCommand = new RelayCommand(_ => DismissBootTraceBanner());

        RefreshWprStatusCommand = new AsyncRelayCommand(RefreshWprStatusAsync, () => !IsStatusLoading);
        RescueCancelCommand = new AsyncRelayCommand(RescueCancelAsync, () => !IsStatusLoading);

        SummarizeLastTraceCommand = new AsyncRelayCommand(SummarizeLastTraceAsync, () => !IsSummarizing && LastEtlPath is not null);
        PickAndSummarizeCommand = new RelayCommand(_ => PickAndSummarize(), _ => !IsSummarizing);
        OpenHtmlReportCommand = new RelayCommand(_ => OpenHtmlReport(), _ => LastSummary?.HtmlReportPath is not null);

        _bootTraceMarker = LoadBootTraceMarker();
        IsBootTracePending = _bootTraceMarker is not null;
    }

    public void Dispose()
    {
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= ElapsedTimerTick;
        _growthTimer.Stop();
        _growthTimer.Tick -= GrowthTimerTick;

        // #155: guaranteed teardown on app exit too, not just on an explicit Stop click - mirrors
        // EventsViewModel.Dispose disposing its own live watchers/handles.
        _tailHandle?.Dispose();
        _tailHandle = null;
    }
}
