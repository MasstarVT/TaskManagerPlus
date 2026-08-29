using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class ProcessesViewModel : ObservableObject, IDisposable
{
    private readonly ProcessMonitorService _monitor = new();
    private readonly ProcessHistoryService _processHistory;
    private readonly LeakWatchViewModel _leakWatch;
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;

    // #274/#275/#276/#277: shared .NET CLR perf-counter resolver, ridden on this tab's own poll
    // tick - see DotNetPerfCounterService's remarks. Sampled once per tick and merged into each
    // ProcessRow (#274/#276/#277 below); ResponsivenessViewModel reads LastDotNetCounters directly
    // for #275's GC-pause-monitor card rather than re-sampling.
    private readonly DotNetPerfCounterService _dotNetCounters = new();
    public Dictionary<int, DotNetProcessCounters> LastDotNetCounters { get; private set; } = new();

    /// <summary>#261: set once by MainViewModel right after ResponsivenessViewModel is constructed
    /// (this ViewModel itself is built first, via a parameterless constructor, so there's no
    /// circular-constructor-dependency way to pass it in up front). Gives this tab read access to
    /// SchedulerService's shared system-wide thread sweep (owned/refreshed by
    /// ResponsivenessViewModel on its own slower cadence - see that class's remarks) for the
    /// per-process wait-reason breakdown below, without a second syscall sweep on this tab's own
    /// faster poll interval.</summary>
    public ResponsivenessViewModel? Responsiveness { get; set; }

    // #404: edge-triggered GDI/USER quota toasts - one toast per pid per threshold crossing, not
    // one every tick a sustained warning stays above the threshold. See CheckGdiUserQuotaAlerts.
    private readonly HashSet<int> _gdiAlertedPids = new();
    private readonly HashSet<int> _userAlertedPids = new();

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ICollectionView ProcessesView { get; }

    // Round 17, item 63: pids this view model has successfully hooked Process.Exited on, so a
    // best-effort exit code can be captured at the moment .NET itself detects the exit rather than
    // only ever learning "the pid is gone" a poll tick later - see TrackForExit/
    // OnTrackedProcessExited. Every entry here is disposed and removed once its exit (or the
    // process's removal from Processes for any other reason, e.g. the app closing) is handled.
    private readonly Dictionary<int, Process> _trackedProcesses = new();

    // Round 17, item 63: pids already recorded in RecentlyExited - guards against double-adding a
    // pid whose Process.Exited fired right around the same poll tick that also noticed its
    // removal via the plain merge below. Trimmed alongside RecentlyExited itself (see
    // AddRecentlyExited) so this doesn't grow without bound over a long session; a reused pid
    // landing here after aging out of the capped RecentlyExited list is a known, accepted
    // limitation (Windows pid reuse), not something this best-effort feature tries to solve.
    private readonly HashSet<int> _exitRecorded = new();

    /// <summary>Round 17, item 63: processes seen in a previous poll tick and gone in the current
    /// one - "my app closed itself" turned into "exit code 0xC0000005" wherever a code was
    /// actually captured. Newest first, capped at MaxRecentlyExited so a churny machine (lots of
    /// short-lived helper processes) doesn't grow this list forever.</summary>
    public ObservableCollection<RecentlyExitedProcessInfo> RecentlyExited { get; } = new();
    private const int MaxRecentlyExited = 25;

    /// <summary>#261: thread-state/wait-reason breakdown for SelectedProcess, refreshed whenever the
    /// selection changes and on every tick thereafter (a cheap in-memory filter over the already-
    /// shared sweep, not a new syscall) - see RefreshSelectedWaitBreakdown.</summary>
    public ObservableCollection<ThreadWaitBreakdownRow> SelectedProcessWaitBreakdown { get; } = new();

    /// <summary>Round 7 #1: process tree/hierarchy view, toggleable alongside the flat grid above -
    /// rebuilt from the same already-sampled Processes collection each tick (BuildProcessTree), no
    /// second sampling source.</summary>
    public ObservableCollection<ProcessTreeNode> ProcessTree { get; } = new();

    private bool _showTree;
    public bool ShowTree
    {
        get => _showTree;
        set
        {
            if (SetProperty(ref _showTree, value) && value)
                BuildProcessTree();
        }
    }

    private ProcessRow? _selectedProcess;
    public ProcessRow? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value))
            {
                // Stale on-demand results from a previous selection would be misleading.
                SelectedProcessModules.Clear();
                SideLoadFindings.Clear();
                SelectedProcessAppDirWarning = null;
                SelectedProcessEnvironment.Clear();
                SelectedProcessEnvironmentDrift = null;
                SelectedProcessHandleTypes.Clear();
                SelectedProcessHostedServices.Clear();
                FileLockResults.Clear();
                SelectedProcessUnbackedMemory.Clear();
                SelectedProcessHollowingCheck.Clear();
                SelectedProcessForeignThreads.Clear();
                SelectedProcessMitigations.Clear();
                SelectedProcessPrivileges.Clear();
                DecodedCommandLineText = null;
                WaitChainResults.Clear();
                WaitChainNodes.Clear();
                WaitChainStatusText = string.Empty;
                SelectedProcessAddressSpace = null;
                LoadAffinityForSelection();
                RefreshSelectedWaitBreakdown();
                OnPropertyChanged(nameof(IsSvchostRowSelected));
            }
        }
    }

    /// <summary>#761: public mirror of IsSvchostSelected() (below), so the Services-tab cross-link
    /// button's IsEnabled can bind to it directly - IsSvchostSelected() itself stays private since
    /// it only otherwise backs ViewHostedServicesCommand's CanExecute, which doesn't need a bindable
    /// property.</summary>
    public bool IsSvchostRowSelected => IsSvchostSelected();

    /// <summary>Loaded modules/DLLs for SelectedProcess (#39, extended by Round 15 #849 with
    /// signature/publisher/user-writable-location trust columns), populated on demand via
    /// ViewModulesCommand rather than every tick - walking a process's full module list (and, since
    /// #849, checking every module's signature) is comparatively expensive and something Task
    /// Manager itself also only does on request. See ModuleTrustInspectionService.</summary>
    public ObservableCollection<ProcessModuleInfo> SelectedProcessModules { get; } = new();

    /// <summary>Round 15, #850: DLL side-loading findings from the same module inspection pass above -
    /// a filtered view (IsSideLoadSuspect) of SelectedProcessModules, kept as its own collection so
    /// the XAML "Side-loading risk" panel doesn't need a converter/filter to stay in sync.</summary>
    public ObservableCollection<ProcessModuleInfo> SideLoadFindings { get; } = new();

    /// <summary>Round 15, #850: set when the selected process's own application directory is itself
    /// in a user-writable location - null when clean/not yet checked.</summary>
    private string? _selectedProcessAppDirWarning;
    public string? SelectedProcessAppDirWarning { get => _selectedProcessAppDirWarning; set => SetProperty(ref _selectedProcessAppDirWarning, value); }

    /// <summary>Round 15, #846: unbacked executable memory scan results for SelectedProcess -
    /// see UnbackedExecutableMemoryService.</summary>
    public ObservableCollection<string> SelectedProcessUnbackedMemory { get; } = new();

    /// <summary>Round 15, #847: hollowed-image indicator results for SelectedProcess's main module -
    /// see HollowedImageIndicatorService.</summary>
    public ObservableCollection<string> SelectedProcessHollowingCheck { get; } = new();

    /// <summary>Round 15, #848: foreign (unbacked) thread start-address findings for SelectedProcess -
    /// see ForeignThreadStartService.</summary>
    public ObservableCollection<string> SelectedProcessForeignThreads { get; } = new();

    /// <summary>Round 15, #851: mitigation-policy badge row for SelectedProcess - see
    /// ProcessMitigationService.</summary>
    public ObservableCollection<MitigationFlag> SelectedProcessMitigations { get; } = new();

    /// <summary>Round 16, #853: token privilege audit results for SelectedProcess - see
    /// TokenPrivilegeAuditService.</summary>
    public ObservableCollection<TokenPrivilegeInfo> SelectedProcessPrivileges { get; } = new();

    /// <summary>Round 16, #856: decoded text from SelectedProcess's -EncodedCommand PowerShell
    /// argument (or an error message if decoding failed) - null until DecodeEncodedCommandCommand
    /// has been run. See LivingOffTheLandService.DecodeEncodedCommand.</summary>
    private string? _decodedCommandLineText;
    public string? DecodedCommandLineText { get => _decodedCommandLineText; set => SetProperty(ref _decodedCommandLineText, value); }

    /// <summary>Round 7 #3: environment variables for SelectedProcess, populated on demand via
    /// ViewEnvironmentCommand - see ProcessEnvironmentService for why this needs a PEB memory walk
    /// and is best-effort/64-bit-only.</summary>
    public ObservableCollection<string> SelectedProcessEnvironment { get; } = new();

    /// <summary>#799: whether SelectedProcess's own PATH/TEMP (from the environment dump just
    /// above) has drifted from the current machine+user environment - populated alongside
    /// SelectedProcessEnvironment by the same ViewEnvironmentCommand click (no extra query - it's a
    /// pure comparison over data already read). Null until "View environment" has been run for this
    /// selection. See ProcessEnvironmentDriftService.</summary>
    private ProcessEnvironmentDrift? _selectedProcessEnvironmentDrift;
    public ProcessEnvironmentDrift? SelectedProcessEnvironmentDrift { get => _selectedProcessEnvironmentDrift; private set => SetProperty(ref _selectedProcessEnvironmentDrift, value); }

    /// <summary>Round 7 #12: open-handle counts by object type for SelectedProcess, populated on
    /// demand via ViewHandleTypesCommand - see HandleInspectionService.</summary>
    public ObservableCollection<string> SelectedProcessHandleTypes { get; } = new();

    /// <summary>Round 7 #17: hosted service display names for SelectedProcess (svchost.exe and
    /// similar host processes only) - populated on demand via ViewHostedServicesCommand.</summary>
    public ObservableCollection<string> SelectedProcessHostedServices { get; } = new();

    /// <summary>Round 7 #9: processes found holding FileLockPath open, via Restart Manager - see
    /// FileLockLookupService.</summary>
    public ObservableCollection<string> FileLockResults { get; } = new();

    /// <summary>#271: Wait Chain Traversal result for SelectedProcess (or whatever ProcessRow was
    /// passed as the command parameter - see AnalyzeWaitChainCommand's remarks), rendered as a
    /// flat indented list. Cleared at the start of each new analysis, same "stale on-demand results
    /// would be misleading" reasoning as SelectedProcess's other on-demand panels.</summary>
    public ObservableCollection<WaitChainNodeRow> WaitChainNodes { get; } = new();

    private string _waitChainStatusText = string.Empty;
    public string WaitChainStatusText { get => _waitChainStatusText; private set => SetProperty(ref _waitChainStatusText, value); }

    private bool _isAnalyzingWaitChain;
    public bool IsAnalyzingWaitChain { get => _isAnalyzingWaitChain; private set => SetProperty(ref _isAnalyzingWaitChain, value); }

    /// <summary>#271: reachable both from the grid's context menu (no CommandParameter - falls
    /// back to SelectedProcess) and from #272's inline "Analyze" link on a flagged row
    /// (CommandParameter={Binding}, so it targets that row directly without disturbing the current
    /// selection). Named distinctly from item 64's simpler AnalyzeWaitChainCommand below (a
    /// separate, independently-built Wait Chain Traversal view over WaitChainNodes/
    /// WaitChainStatusText rather than WaitChainResults) since both exist side by side.</summary>
    public AsyncRelayCommand AnalyzeWaitChainDetailedCommand { get; }

    /// <summary>#408: SelectedProcess's address-space breakdown from the most recent on-demand
    /// walk - null until ViewAddressSpaceCommand runs (or after the selection changes, so a stale
    /// walk from a previous process is never shown as if it were current). See
    /// AddressSpaceInspectionService.</summary>
    private AddressSpaceSummary? _selectedProcessAddressSpace;
    public AddressSpaceSummary? SelectedProcessAddressSpace { get => _selectedProcessAddressSpace; private set => SetProperty(ref _selectedProcessAddressSpace, value); }

    private string _fileLockPath = string.Empty;
    public string FileLockPath { get => _fileLockPath; set => SetProperty(ref _fileLockPath, value); }

    /// <summary>Round 7 #5: one checkbox per logical processor, loaded from SelectedProcess's
    /// current affinity mask whenever the selection changes - see LoadAffinityForSelection. Not
    /// applied until ApplyAffinityCommand runs, the same "view now, commit explicitly" shape the
    /// modules/environment viewers use for anything heavier than a plain read.</summary>
    public ObservableCollection<CpuAffinityOption> AffinityCores { get; } = new();

    /// <summary>Round 7 #6: priority-class choices for the toolbar combo box, plain strings so the
    /// XAML doesn't need an x:Static reference into System.Diagnostics for each enum value.</summary>
    public IReadOnlyList<string> PriorityOptionNames { get; } =
        new[] { "RealTime", "High", "AboveNormal", "Normal", "BelowNormal", "Idle" };

    private string _selectedPriorityName = "Normal";
    public string SelectedPriorityName { get => _selectedPriorityName; set => SetProperty(ref _selectedPriorityName, value); }

    public RelayCommand ViewModulesCommand { get; }
    public RelayCommand ViewEnvironmentCommand { get; }
    public RelayCommand ViewHandleTypesCommand { get; }
    public RelayCommand ViewHostedServicesCommand { get; }
    public RelayCommand LookupFileLockCommand { get; }
    /// <summary>Round 15, #846.</summary>
    public RelayCommand ViewUnbackedMemoryCommand { get; }
    /// <summary>Round 15, #847.</summary>
    public RelayCommand ViewHollowingCheckCommand { get; }
    /// <summary>Round 15, #848.</summary>
    public RelayCommand ViewForeignThreadsCommand { get; }
    /// <summary>Round 15, #851.</summary>
    public RelayCommand ViewMitigationsCommand { get; }
    /// <summary>Round 16, #853.</summary>
    public RelayCommand ViewPrivilegesCommand { get; }
    /// <summary>Round 16, #856.</summary>
    public RelayCommand DecodeEncodedCommandCommand { get; }
    /// <summary>Round 17, #864: "Scan this process's image file" context-menu item.</summary>
    public AsyncRelayCommand ScanWithDefenderCommand { get; }

    /// <summary>#408: single-process, on-demand VirtualQueryEx address-space walk - never on a
    /// tick, see AddressSpaceInspectionService's remarks.</summary>
    public RelayCommand ViewAddressSpaceCommand { get; }
    public RelayCommand TrimWorkingSetCommand { get; }
    public RelayCommand SuspendCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand ApplyAffinityCommand { get; }
    public RelayCommand SetPriorityCommand { get; }

    /// <summary>#406: right-click "Watch for leaks" - adds/removes SelectedProcess's image name
    /// from the pinned leak-watch list (LeakWatchViewModel), which then samples it independently
    /// at a fixed 5s interval and charts it on the Memory tab.</summary>
    public RelayCommand ToggleLeakWatchCommand { get; }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ProcessesView.Refresh();
        }
    }

    private bool _recentlyStartedOnly;
    public bool RecentlyStartedOnly
    {
        get => _recentlyStartedOnly;
        set
        {
            if (SetProperty(ref _recentlyStartedOnly, value))
                ProcessesView.Refresh();
        }
    }

    /// <summary>#266: filter checkbox, same pattern as RecentlyStartedOnly above - "this app is
    /// slow because Windows classified it as background" is otherwise undiagnosable in a long
    /// process list.</summary>
    private bool _onlyPowerThrottled;
    public bool OnlyPowerThrottled
    {
        get => _onlyPowerThrottled;
        set
        {
            if (SetProperty(ref _onlyPowerThrottled, value))
                ProcessesView.Refresh();
        }
    }

    private int _processCount;
    public int ProcessCount { get => _processCount; private set => SetProperty(ref _processCount, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public RelayCommand EndTaskCommand { get; }
    public RelayCommand EndProcessTreeCommand { get; }
    public RelayCommand RefreshNowCommand { get; }

    /// <summary>Item 64: indented-list-of-strings render of the last Analyse-wait-chain result for
    /// SelectedProcess - plain strings (not a structured node type) since the whole chain is
    /// always shown as-is, the same "flat display list, populated on demand" shape
    /// SelectedProcessHandleTypes above already uses.</summary>
    public ObservableCollection<string> WaitChainResults { get; } = new();
    public AsyncRelayCommand AnalyzeWaitChainCommand { get; }

    /// <summary>Item 65: on-demand mini/full dump of SelectedProcess - see ProcessDumpService.</summary>
    public RelayCommand CreateMiniDumpCommand { get; }
    public RelayCommand CreateFullDumpCommand { get; }

    /// <summary>Item 67: opt-in (default off) SendMessageTimeout probe against every windowed
    /// process's main window, once per poll tick - off by default so an ordinary tick never pays
    /// for it; see ProcessMonitorService.Sample's own remarks on the exact gating/cost tradeoff.</summary>
    private bool _measureResponseTime;
    public bool MeasureResponseTime { get => _measureResponseTime; set => SetProperty(ref _measureResponseTime, value); }

    public ProcessesViewModel(ProcessHistoryService processHistory, LeakWatchViewModel leakWatch)
    {
        _processHistory = processHistory;
        _leakWatch = leakWatch;

        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterPredicate;

        // #980: read-only mode disables every mutating process command below but leaves the
        // read-only lookups (modules/environment/handle types/hosted services/file-lock/refresh)
        // working - each predicate ANDs in !ReadOnlyModeService.IsReadOnly alongside its existing
        // SelectedProcess check, per that suggestion's own "AND into existing CanExecute" guidance.
        EndTaskCommand = new RelayCommand(_ => EndSelected(tree: false), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        EndProcessTreeCommand = new RelayCommand(_ => EndSelected(tree: true), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshAsync());
        ViewModulesCommand = new RelayCommand(_ => _ = LoadSelectedProcessModulesAsync(), _ => SelectedProcess is not null);
        ViewEnvironmentCommand = new RelayCommand(_ => LoadSelectedProcessEnvironment(), _ => SelectedProcess is not null);
        ViewHandleTypesCommand = new RelayCommand(_ => _ = LoadSelectedProcessHandleTypesAsync(), _ => SelectedProcess is not null);
        ViewHostedServicesCommand = new RelayCommand(_ => LoadSelectedProcessHostedServices(), _ => IsSvchostSelected());
        LookupFileLockCommand = new RelayCommand(_ => _ = LookupFileLockAsync(), _ => !string.IsNullOrWhiteSpace(FileLockPath));
        ViewUnbackedMemoryCommand = new RelayCommand(_ => _ = LoadSelectedProcessUnbackedMemoryAsync(), _ => SelectedProcess is not null);
        ViewHollowingCheckCommand = new RelayCommand(_ => _ = LoadSelectedProcessHollowingCheckAsync(), _ => SelectedProcess is not null);
        ViewForeignThreadsCommand = new RelayCommand(_ => _ = LoadSelectedProcessForeignThreadsAsync(), _ => SelectedProcess is not null);
        ViewMitigationsCommand = new RelayCommand(_ => _ = LoadSelectedProcessMitigationsAsync(), _ => SelectedProcess is not null);
        ViewPrivilegesCommand = new RelayCommand(_ => _ = LoadSelectedProcessPrivilegesAsync(), _ => SelectedProcess is not null);
        DecodeEncodedCommandCommand = new RelayCommand(_ => DecodeEncodedCommand(), _ => HasEncodedCommand());
        ScanWithDefenderCommand = new AsyncRelayCommand(ScanSelectedWithDefenderAsync, () => !string.IsNullOrWhiteSpace(SelectedProcess?.FilePath));
        ViewAddressSpaceCommand = new RelayCommand(_ => _ = LoadSelectedProcessAddressSpaceAsync(), _ => SelectedProcess is not null);
        // #980: read-only mode disables every state-mutating process action below.
        TrimWorkingSetCommand = new RelayCommand(_ => TrimWorkingSet(), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        SuspendCommand = new RelayCommand(_ => SetSuspended(true), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        ResumeCommand = new RelayCommand(_ => SetSuspended(false), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        ApplyAffinityCommand = new RelayCommand(_ => ApplyAffinity(), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null && AffinityCores.Count > 0);
        SetPriorityCommand = new RelayCommand(_ => SetPriority(), _ => !ReadOnlyModeService.IsReadOnly && SelectedProcess is not null);
        // #271: param is the flagged-row ProcessRow when invoked from #272's inline link, or null
        // (falls back to SelectedProcess) from the context menu.
        AnalyzeWaitChainDetailedCommand = new AsyncRelayCommand(param => AnalyzeWaitChainAsync(param as ProcessRow ?? SelectedProcess));

        // Item 64: gated to a currently-not-responding process, matching this item's own "on any
        // not-responding process" wording - a healthy process' wait chain isn't the point of this
        // feature (and would just show it running, nothing to analyse).
        AnalyzeWaitChainCommand = new AsyncRelayCommand(AnalyzeWaitChainAsync,
            () => SelectedProcess is { Status: "Not responding" });

        // Item 65: available for any selected process, hung or not - Task Manager's own "Create
        // dump file" isn't limited to not-responding processes either.
        CreateMiniDumpCommand = new RelayCommand(_ => CreateProcessDump(ProcessDumpService.DumpKind.Mini), _ => SelectedProcess is not null);
        CreateFullDumpCommand = new RelayCommand(_ => CreateProcessDump(ProcessDumpService.DumpKind.Full), _ => SelectedProcess is not null);
        ToggleLeakWatchCommand = new RelayCommand(_ => ToggleLeakWatch(), _ => SelectedProcess is not null);

        // Round 12, #100: configurable poll interval - see PollIntervalSettingsService's remarks.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(PollIntervalSettingsService.Load().ProcessesSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    /// <summary>Round 12, #100: how often the Processes tab refreshes - default unchanged (1s).
    /// Loaded fresh from disk on every change (never cached) so this tab's slider can't clobber
    /// another tab's own saved interval in the same shared JSON file.</summary>
    public double PollIntervalSeconds
    {
        get => _timer.Interval.TotalSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 10.0);
            if (Math.Abs(_timer.Interval.TotalSeconds - clamped) < 0.01) return;

            _timer.Interval = TimeSpan.FromSeconds(clamped);
            var settings = PollIntervalSettingsService.Load();
            settings.ProcessesSeconds = clamped;
            PollIntervalSettingsService.Save(settings);
            OnPropertyChanged();
        }
    }

    /// <summary>How far back "Recently started" reaches - right after a slowdown or crash starts
    /// is exactly when a user wants to see "what just launched" without hunting through the
    /// full, mostly-idle process list.</summary>
    private static readonly TimeSpan RecentlyStartedWindow = TimeSpan.FromMinutes(5);

    private bool FilterPredicate(object obj)
    {
        if (obj is not ProcessRow row) return false;

        if (RecentlyStartedOnly &&
            (row.StartTime is null || DateTime.Now - row.StartTime.Value > RecentlyStartedWindow))
            return false;

        if (OnlyPowerThrottled && !row.IsPowerThrottled)
            return false;

        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return row.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               row.Pid.ToString().Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               row.User.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            // #401: history recording (and its regression-based #402/#403/#405 fields) runs here,
            // inside the same background Task.Run as the sample itself - the rows aren't yet
            // attached to the UI-bound Processes collection, so mutating them off the UI thread
            // is safe, the same reasoning ProcessMonitorService.Sample already relies on for its
            // own per-row computed fields.
            var latest = await Task.Run(() =>
            {
                var rows = _monitor.Sample(MeasureResponseTime);
                _processHistory.RecordSample(rows);
                return rows;
            });
            // #274/#275/#276/#277: shared .NET CLR perf-counter sample, on this same tick - see
            // DotNetPerfCounterService's remarks for why this is one dictionary build per tick
            // rather than a per-process query.
            LastDotNetCounters = await Task.Run(() => _dotNetCounters.Sample());
            MergeInto(latest);
            ApplyDotNetCounters();
            ProcessCount = Processes.Count;

            // MergeInto() only implicitly re-filters on add/remove - re-evaluate explicitly so a
            // row drops out of "Recently started" once it ages past the window, even if nothing
            // else in the process list changed this tick.
            if (RecentlyStartedOnly)
                ProcessesView.Refresh();

            if (ShowTree)
                BuildProcessTree();

            // #261: the shared sweep (owned by ResponsivenessViewModel) refreshes on its own
            // slower cadence, independent of this tab's tick - re-pull whatever's current for the
            // still-selected pid rather than only refreshing on selection change, so the panel
            // catches up once a new sweep lands.
            RefreshSelectedWaitBreakdown();
            CheckGdiUserQuotaAlerts();
        }
        catch
        {
            // Best-effort - a failed sample shouldn't crash the timer loop.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>#261: cheap in-memory filter over ResponsivenessViewModel's already-shared sweep -
    /// see Responsiveness's remarks above for why this doesn't run its own syscall.</summary>
    private void RefreshSelectedWaitBreakdown()
    {
        SelectedProcessWaitBreakdown.Clear();
        if (SelectedProcess is not { } target || Responsiveness is null) return;
        foreach (var row in Responsiveness.GetThreadWaitBreakdown(target.Pid))
            SelectedProcessWaitBreakdown.Add(row);
    }

    /// <summary>#274/#276/#277: merges this tick's shared .NET CLR perf-counter sample into every
    /// row (blank/false for a pid with no resolved entry - not managed, or the categories aren't
    /// published - never a fabricated value), and #272's stuck-thread flag from
    /// ResponsivenessViewModel's shared scheduler sweep (the same cross-viewmodel read
    /// RefreshSelectedWaitBreakdown already does for #261, just applied to every row instead of
    /// only the selected one).</summary>
    private void ApplyDotNetCounters()
    {
        foreach (var row in Processes)
        {
            if (LastDotNetCounters.TryGetValue(row.Pid, out var c))
            {
                row.DotNetContentionRatePerSec = c.ContentionRatePerSec;
                row.DotNetTotalContentions = c.TotalContentions;
                row.DotNetQueueLength = c.CurrentQueueLength;
                row.GcModeText = c.GcModeText;
                row.GcConcurrentText = c.GcConcurrentText;
                row.IsThreadPoolStarvationSuspect = c.IsThreadPoolStarvationSuspect;
            }
            else
            {
                row.DotNetContentionRatePerSec = null;
                row.DotNetTotalContentions = null;
                row.DotNetQueueLength = null;
                row.GcModeText = string.Empty;
                row.GcConcurrentText = string.Empty;
                row.IsThreadPoolStarvationSuspect = false;
            }

            var (isStuck, hint) = Responsiveness?.GetStuckThreadFlag(row.Pid) ?? (false, null);
            row.IsStuckThreadSuspect = isStuck;
            row.StuckThreadHintText = hint;

            // #294: composite responsiveness score - see ResponsivenessViewModel.
            // GetProcessResponsivenessScore's remarks for how each factor is gathered.
            var score = Responsiveness?.GetProcessResponsivenessScore(row.Pid);
            row.ResponsivenessScore = score?.Score;
            row.ResponsivenessScoreTooltip = score?.TooltipText
                ?? "Not enough data yet to compute a responsiveness score for this process.";
        }
    }

    /// <summary>#271: analyzes <paramref name="target"/>'s (best-effort) main thread's wait chain -
    /// see WaitChainTraversalService.Analyze's remarks on why this always runs via Task.Run.</summary>
    private async Task AnalyzeWaitChainAsync(ProcessRow? target)
    {
        if (target is null) return;

        IsAnalyzingWaitChain = true;
        WaitChainStatusText = "Analyzing wait chain...";
        WaitChainNodes.Clear();
        try
        {
            int threadId = 0;
            try
            {
                using var proc = Process.GetProcessById(target.Pid);
                threadId = proc.Threads.Count > 0 ? proc.Threads[0].Id : 0;
            }
            catch
            {
                // leave threadId 0 - WaitChainTraversalService.Analyze reports a clean failure below.
            }

            var result = await Task.Run(() => WaitChainTraversalService.Analyze(target.Pid, threadId));
            WaitChainStatusText = result.StatusText;
            foreach (var n in result.Nodes) WaitChainNodes.Add(n);
        }
        finally
        {
            IsAnalyzingWaitChain = false;
        }
    }

    private void MergeInto(List<ProcessRow> latest)
    {
        var latestByPid = latest.ToDictionary(r => r.Pid);

        // Update or mark-for-removal existing rows.
        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            var existing = Processes[i];
            if (latestByPid.TryGetValue(existing.Pid, out var fresh))
            {
                existing.CpuPercent = fresh.CpuPercent;
                existing.CpuPercent10sAvg = fresh.CpuPercent10sAvg;
                // #283: detect a working-set trim before overwriting MemoryBytes with this tick's
                // fresh value - needs the about-to-be-replaced old value as the "before" side.
                DetectWorkingSetTrim(existing, fresh.MemoryBytes);
                existing.MemoryBytes = fresh.MemoryBytes;
                existing.PrivateBytes = fresh.PrivateBytes;
                existing.VirtualBytes = fresh.VirtualBytes;
                existing.NonpagedPoolBytes = fresh.NonpagedPoolBytes;
                existing.PagedPoolBytes = fresh.PagedPoolBytes;
                existing.DiskBytesPerSec = fresh.DiskBytesPerSec;
                existing.Status = fresh.Status;

                // Round 17 chunk 64-70, item 66: a hang episode just ended (this row was hung last
                // tick and isn't now) - record its peak duration/count for this executable.
                // NotRespondingSeconds is monotonic while hung and resets to 0 the instant Windows
                // reports the process responsive again (see ProcessMonitorService.Sample), so the
                // *previous* tick's value (still on `existing` here, not yet overwritten) is
                // exactly that episode's peak.
                if (existing.NotRespondingSeconds > 0 && fresh.NotRespondingSeconds == 0)
                    HangHistoryService.RecordHang(existing.Name, existing.NotRespondingSeconds);

                existing.NotRespondingSeconds = fresh.NotRespondingSeconds;
                existing.ResponseTimeMs = fresh.ResponseTimeMs;
                existing.ThreadCount = fresh.ThreadCount;
                existing.HandleCount = fresh.HandleCount;
                existing.SignatureStatus = fresh.SignatureStatus;
                // Publisher/IsSelfSigned resolve asynchronously now (see GetResultOrQueue in
                // ProcessMonitorService.Sample) - a row born while its binary's verify was still
                // queued starts as Unknown and picks up the real values here a tick later.
                existing.Publisher = fresh.Publisher;
                existing.IsSelfSigned = fresh.IsSelfSigned;
                existing.IsHighPrivilege = fresh.IsHighPrivilege;
                existing.IsLeakSuspect = fresh.IsLeakSuspect;
                existing.GpuPercent = fresh.GpuPercent;
                existing.PriorityClassName = fresh.PriorityClassName;
                existing.GdiHandleCount = fresh.GdiHandleCount;
                existing.UserHandleCount = fresh.UserHandleCount;
                existing.IsSuspended = fresh.IsSuspended;
                existing.IntegrityLevel = fresh.IntegrityLevel;
                existing.IsAppContainer = fresh.IsAppContainer;
                existing.ProtectionLevel = fresh.ProtectionLevel;
                existing.SpawnGroupSize = fresh.SpawnGroupSize;
                existing.DuplicateInstanceCount = fresh.DuplicateInstanceCount;
                existing.IsDuplicateInstanceOutlier = fresh.IsDuplicateInstanceOutlier;
                existing.SecurityFlagReason = fresh.SecurityFlagReason;
                // #266/#270
                existing.PowerThrottleText = fresh.PowerThrottleText;
                existing.IoPriorityText = fresh.IoPriorityText;
                existing.IsBackgroundIoPriority = fresh.IsBackgroundIoPriority;
                existing.MemoryPriorityText = fresh.MemoryPriorityText;
                existing.IsGdiQuotaWarning = fresh.IsGdiQuotaWarning;
                existing.IsUserQuotaWarning = fresh.IsUserQuotaWarning;
                // #409/#410/#412
                existing.PageFaultsPerSec = fresh.PageFaultsPerSec;
                existing.PrivateWorkingSetBytes = fresh.PrivateWorkingSetBytes;
                existing.WorkingSetPrivateGapBytes = fresh.WorkingSetPrivateGapBytes;
                existing.IsWorkingSetDivergent = fresh.IsWorkingSetDivergent;
                // #401/#402/#403/#405: regression-derived fields computed by
                // ProcessHistoryService.RecordSample just before this merge ran.
                existing.MemorySparkline = fresh.MemorySparkline;
                existing.LeakSlopeMbPerHour = fresh.LeakSlopeMbPerHour;
                existing.LeakRSquared = fresh.LeakRSquared;
                existing.IsHandleLeakSuspect = fresh.IsHandleLeakSuspect;
                existing.IsThreadRunawaySuspect = fresh.IsThreadRunawaySuspect;
                // CommandLine/FilePath/StartTime/User/ParentPid/ParentName don't change for the
                // lifetime of a pid - no need to reassign them every tick like the values above
                // that actually vary.

                // Round 17, item 60: cheap in-memory cache lookup, never a fresh event-log query
                // on this tick - see CrashHistoryCacheService's own remarks.
                existing.CrashCount30d = CrashHistoryCacheService.GetCrashCount(existing.Name);

                latestByPid.Remove(existing.Pid);
            }
            else
            {
                // Round 17, item 63: this pid was sampled last tick and is gone now - it exited
                // sometime in between. If Process.Exited (hooked in TrackForExit below, when
                // opening a handle to this pid succeeded before it exited) already fired, this
                // pid is already in RecentlyExited with a real exit code and _exitRecorded
                // already has it - this is only the fallback for a pid this app never managed to
                // track (a protected process it couldn't open a handle to, or one that started
                // and exited between two ticks before it was ever added to Processes at all).
                if (_exitRecorded.Add(existing.Pid))
                    AddRecentlyExited(existing.Pid, existing.Name, null);
                UntrackProcess(existing.Pid);

                // Item 66: the other half of "record a completed hang episode" - a process that
                // exits while still flagged Not responding never gets the "recovered" transition
                // above, so this is the only place that hang episode is ever seen ending.
                if (existing.NotRespondingSeconds > 0)
                    HangHistoryService.RecordHang(existing.Name, existing.NotRespondingSeconds);

                Processes.RemoveAt(i);
            }
        }

        // Anything left in latestByPid is a newly-seen process.
        foreach (var row in latestByPid.Values)
        {
            row.CrashCount30d = CrashHistoryCacheService.GetCrashCount(row.Name);
            Processes.Add(row);
            TrackForExit(row.Pid, row.Name);
        }
    }

    /// <summary>Round 17, item 63: opens a handle to a newly-seen pid and hooks Process.Exited so
    /// a best-effort exit code can be captured at the moment .NET itself detects the exit, rather
    /// than only ever learning "the pid is gone" one poll tick later with no way to recover its
    /// exit code at all. Best-effort by nature - a protected process, or a process that exits
    /// before this ever runs for it, simply never gets tracked; the plain merge-detected removal
    /// in MergeInto above is the fallback for those.</summary>
    private void TrackForExit(int pid, string name)
    {
        if (_trackedProcesses.ContainsKey(pid)) return;
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnTrackedProcessExited(pid, name, proc);
            _trackedProcesses[pid] = proc;
        }
        catch
        {
            // Access denied (a protected process) or it already exited before this ran - no
            // Process.Exited notification is possible for this pid.
        }
    }

    private void UntrackProcess(int pid)
    {
        if (_trackedProcesses.Remove(pid, out var proc))
        {
            try { proc.Dispose(); } catch { /* best-effort */ }
        }
    }

    /// <summary>Round 17, item 63: fires on Process.Exited's own ThreadPool thread - hops to the
    /// UI thread before touching RecentlyExited/Processes, the same boundary every other
    /// background-sourced update in this app already respects (e.g. StabilityViewModel.
    /// OnNewDumpDetected).</summary>
    private void OnTrackedProcessExited(int pid, string name, Process proc)
    {
        int? exitCode = null;
        try { exitCode = proc.ExitCode; }
        catch { /* degrade to "unavailable" below */ }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            if (_exitRecorded.Add(pid))
                AddRecentlyExited(pid, name, exitCode);
            UntrackProcess(pid);
        });
    }

    private void AddRecentlyExited(int pid, string name, int? exitCode)
    {
        RecentlyExited.Insert(0, new RecentlyExitedProcessInfo
        {
            Pid = pid,
            Name = name,
            ExitTime = DateTime.Now,
            ExitCode = exitCode,
            ExitCodeText = DescribeExitCode(exitCode),
        });

        while (RecentlyExited.Count > MaxRecentlyExited)
        {
            var removed = RecentlyExited[^1];
            RecentlyExited.RemoveAt(RecentlyExited.Count - 1);
            _exitRecorded.Remove(removed.Pid);
        }
    }

    /// <summary>Round 17, item 63: decodes an NTSTATUS-shaped exit code (round 15, item 30's
    /// table, reused per this item's own instruction) - a normal small exit code (0, 1, ...) is
    /// shown as a plain number instead of a meaningless "0x00000000" hex dump; only a code whose
    /// top bit is set (the 0x8xxxxxxx/0xC0000000+ ranges an actual crash/termination exit code
    /// uses) gets the hex+name treatment.</summary>
    private static string DescribeExitCode(int? exitCode)
    {
        if (exitCode is null) return "Exit code unavailable";
        uint code = unchecked((uint)exitCode.Value);
        return code >= 0x80000000
            ? NtStatusLookup.Describe($"0x{code:X8}")
            : code.ToString();
    }

    // #283: tuned thresholds, not a documented Windows event - Windows fires no public
    // notification this app can subscribe to for "a process's working set just got trimmed", so
    // this is inferred purely from the magnitude of the drop between two consecutive samples.
    private const double TrimDropPercentThreshold = 0.30;
    private const long TrimDropBytesThreshold = 50L * 1024 * 1024;

    /// <summary>#283: flags a large, sudden working-set drop (more than 30% and more than 50MB in
    /// one tick) as a probable working-set trim - it typically precedes a burst of hard faults as
    /// the process pages everything back in on its next touch. Overwrites LastTrimmedText (rather
    /// than appending a history) so the row always shows its most recent trim, matching the
    /// "simplest correct shape" the item calls for over a full timeline visualization.</summary>
    private static void DetectWorkingSetTrim(ProcessRow row, long freshMemoryBytes)
    {
        long previous = row.MemoryBytes;
        long drop = previous - freshMemoryBytes;
        if (previous > 0 && drop >= TrimDropBytesThreshold && drop >= previous * TrimDropPercentThreshold)
            row.LastTrimmedText = $"Trimmed at {DateTime.Now:HH:mm:ss} ({Formatting.FormatBytes(drop)} freed)";
    }

    /// <summary>Round 7 #1: rebuilds the tree from the already-sampled flat Processes collection -
    /// a process whose parent isn't currently running (exited, or genuinely a root like
    /// explorer.exe/services.exe) becomes a root node, the same "orphan" treatment Task Manager's
    /// own Details-tab tree uses. Only run while ShowTree is on, so a user who never opens the
    /// tree view never pays for building it.</summary>
    private void BuildProcessTree()
    {
        var nodesByPid = Processes.ToDictionary(r => r.Pid, r => new ProcessTreeNode(r));
        var roots = new List<ProcessTreeNode>();

        foreach (var node in nodesByPid.Values)
        {
            if (node.Row.ParentPid > 0 && nodesByPid.TryGetValue(node.Row.ParentPid, out var parentNode) && parentNode != node)
                parentNode.Children.Add(node);
            else
                roots.Add(node);
        }

        ProcessTree.Clear();
        foreach (var root in roots.OrderBy(n => n.Row.Name, StringComparer.OrdinalIgnoreCase))
            ProcessTree.Add(root);
    }

    private void EndSelected(bool tree)
    {
        var target = SelectedProcess;
        if (target is null) return;

        var confirm = MessageBox.Show(
            tree
                ? $"End \"{target.Name}\" (PID {target.Pid}) and all of its child processes?\nAny unsaved data in these processes will be lost."
                : $"End \"{target.Name}\" (PID {target.Pid})?\nAny unsaved data in this process will be lost.",
            "End process",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = tree
            ? ProcessMonitorService.EndProcessTree(target.Pid)
            : ProcessMonitorService.EndProcess(target.Pid);

        StatusMessage = success
            ? $"Ended {target.Name} (PID {target.Pid})."
            : $"Couldn't end {target.Name}: {error}";

        if (success)
            _ = RefreshAsync();
    }

    /// <summary>Loads SelectedProcess's module/DLL list (#39), now with Round 15 #849's trust columns
    /// (signature/publisher/user-writable-location) and #850's side-loading findings - see
    /// ModuleTrustInspectionService. Signature-checking every module is real work (a WinVerifyTrust
    /// chain call per distinct path on a cache miss), so unlike the original plain-string-list
    /// version this now runs off the UI thread via Task.Run.</summary>
    private async Task LoadSelectedProcessModulesAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessModules.Clear();
        SideLoadFindings.Clear();
        SelectedProcessAppDirWarning = null;

        var result = await Task.Run(() => ModuleTrustInspectionService.Inspect(pid));

        // The selection (or the whole app) may have moved on while this ran in the background.
        if (SelectedProcess?.Pid != pid) return;

        if (result.Error is not null)
        {
            SelectedProcessModules.Add(new ProcessModuleInfo { ModuleName = "(error)", FilePath = result.Error });
            return;
        }

        foreach (var module in result.Modules)
        {
            SelectedProcessModules.Add(module);
            if (module.IsSideLoadSuspect) SideLoadFindings.Add(module);
        }

        if (result.AppDirectoryIsUserWritable)
        {
            SelectedProcessAppDirWarning =
                $"This process's application directory (\"{result.ApplicationDirectory}\") is in a user-writable location - worth a closer look. Quick flag, not a verdict.";
        }
    }

    /// <summary>Round 7 #3: on-demand environment-variable dump for SelectedProcess - see
    /// ProcessEnvironmentService for the PEB-walk technique and its 64-bit-only limitation.</summary>
    private void LoadSelectedProcessEnvironment()
    {
        SelectedProcessEnvironment.Clear();
        SelectedProcessEnvironmentDrift = null;
        var target = SelectedProcess;
        if (target is null) return;

        var env = ProcessEnvironmentService.Read(target.Pid);
        foreach (var entry in env)
            SelectedProcessEnvironment.Add(entry);

        // #799: pure comparison over the environment dump just read - no extra query.
        SelectedProcessEnvironmentDrift = ProcessEnvironmentDriftService.CheckSingle(target.Pid, target.Name, env);
    }

    /// <summary>Round 7 #12: on-demand open-handle-by-type breakdown - see
    /// HandleInspectionService for why this runs off the UI thread and is capped/best-effort.</summary>
    private async Task LoadSelectedProcessHandleTypesAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessHandleTypes.Clear();
        SelectedProcessHandleTypes.Add("Scanning…");

        var counts = await Task.Run(() => HandleInspectionService.ReadHandleTypeCounts(pid));

        // The selection (or the whole app) may have moved on while this ran in the background.
        if (SelectedProcess?.Pid != pid) return;

        SelectedProcessHandleTypes.Clear();
        if (counts.Count == 0)
        {
            SelectedProcessHandleTypes.Add("(no handles found, or the process couldn't be inspected)");
            return;
        }
        foreach (var (typeName, count) in counts)
            SelectedProcessHandleTypes.Add($"{typeName}: {count}");
    }

    /// <summary>Round 15, #846: on-demand unbacked-executable-memory scan for SelectedProcess - see
    /// UnbackedExecutableMemoryService for the safety discipline behind this call.</summary>
    private async Task LoadSelectedProcessUnbackedMemoryAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessUnbackedMemory.Clear();
        SelectedProcessUnbackedMemory.Add("Scanning…");

        var result = await Task.Run(() => UnbackedExecutableMemoryService.Scan(pid));

        if (SelectedProcess?.Pid != pid) return;

        SelectedProcessUnbackedMemory.Clear();
        SelectedProcessUnbackedMemory.Add(
            "JITs (browsers, .NET, Java) legitimately produce unbacked executable memory - this is a comparison signal, not a verdict.");
        string totalSizeText = result.TotalBytes > 0 ? Formatting.FormatBytes(result.TotalBytes) : "0 B";
        SelectedProcessUnbackedMemory.Add(
            $"{result.RegionCount:N0} unbacked executable region(s), {totalSizeText} total ({result.RegionsWalked:N0} regions inspected{(result.Completed ? "" : ", scan abandoned after timing out")}).");
        if (result.Note is not null)
            SelectedProcessUnbackedMemory.Add(result.Note);
    }

    /// <summary>Round 15, #847: on-demand hollowed-image indicator for SelectedProcess's main module -
    /// see HollowedImageIndicatorService.</summary>
    private async Task LoadSelectedProcessHollowingCheckAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessHollowingCheck.Clear();
        SelectedProcessHollowingCheck.Add("Checking…");

        var result = await Task.Run(() => HollowedImageIndicatorService.CheckMainModule(pid));

        if (SelectedProcess?.Pid != pid) return;

        SelectedProcessHollowingCheck.Clear();
        SelectedProcessHollowingCheck.Add($"Reported path: {result.ReportedPath ?? "(unknown)"}");
        SelectedProcessHollowingCheck.Add($"Actually-mapped path: {result.MappedPath ?? "(couldn't be read)"}");
        SelectedProcessHollowingCheck.Add($"Image file still exists on disk: {(result.FileExists ? "Yes" : "No")}");
        SelectedProcessHollowingCheck.Add($"Path mismatch: {(result.PathMismatch ? "Yes" : "No")}");
        if (result.Note is not null)
            SelectedProcessHollowingCheck.Add(result.Note);
    }

    /// <summary>Round 15, #848: on-demand foreign-thread-start-address scan for SelectedProcess - see
    /// ForeignThreadStartService for the safety discipline behind this call.</summary>
    private async Task LoadSelectedProcessForeignThreadsAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessForeignThreads.Clear();
        SelectedProcessForeignThreads.Add("Scanning…");

        var result = await Task.Run(() => ForeignThreadStartService.Scan(pid));

        if (SelectedProcess?.Pid != pid) return;

        SelectedProcessForeignThreads.Clear();
        if (result.Findings.Count == 0)
        {
            SelectedProcessForeignThreads.Add($"No unbacked thread start addresses found ({result.ThreadsScanned} of {result.ThreadsTotal} threads checked).");
        }
        else
        {
            foreach (var finding in result.Findings)
                SelectedProcessForeignThreads.Add($"Thread {finding.ThreadId} started in unbacked memory at 0x{finding.StartAddress:X}");
        }
        if (result.Note is not null)
            SelectedProcessForeignThreads.Add(result.Note);
    }

    /// <summary>Round 15, #851: on-demand mitigation-policy badge row for SelectedProcess - see
    /// ProcessMitigationService.</summary>
    private async Task LoadSelectedProcessMitigationsAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessMitigations.Clear();

        var flags = await Task.Run(() => ProcessMitigationService.ReadMitigations(pid));

        if (SelectedProcess?.Pid != pid) return;

        foreach (var flag in flags)
            SelectedProcessMitigations.Add(flag);
    }

    /// <summary>Round 16, #853: on-demand token-privilege audit for SelectedProcess - see
    /// TokenPrivilegeAuditService. "Non-Microsoft" is decided from SelectedProcess.Publisher, already
    /// computed every tick by ProcessMonitorService/SignatureCheckService, so this needs no extra
    /// signature check of its own.</summary>
    private async Task LoadSelectedProcessPrivilegesAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;
        bool isMicrosoftSigned = target.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        SelectedProcessPrivileges.Clear();

        var (privileges, error) = await Task.Run(() => TokenPrivilegeAuditService.ReadPrivileges(pid, isMicrosoftSigned));

        // The selection (or the whole app) may have moved on while this ran in the background.
        if (SelectedProcess?.Pid != pid) return;

        if (error is not null)
        {
            StatusMessage = error;
            return;
        }
        foreach (var privilege in privileges)
            SelectedProcessPrivileges.Add(privilege);
    }

    /// <summary>Round 16, #856: true only when SelectedProcess's command line matches a PowerShell
    /// -EncodedCommand/-enc argument - see LivingOffTheLandService.TryExtractEncodedCommandArgument.</summary>
    private bool HasEncodedCommand() =>
        SelectedProcess is not null &&
        LivingOffTheLandService.TryExtractEncodedCommandArgument(SelectedProcess.Name, SelectedProcess.CommandLine) is not null;

    /// <summary>Round 16, #856: decodes SelectedProcess's -EncodedCommand argument (UTF-16LE base64,
    /// PowerShell's documented encoding for this flag) into DecodedCommandLineText - a malformed/
    /// truncated base64 string is reported as a decode failure rather than crashing, per this app's
    /// "quick flag, not a verdict" framing (a failed decode isn't itself a finding).</summary>
    private void DecodeEncodedCommand()
    {
        var target = SelectedProcess;
        if (target is null) return;

        var base64 = LivingOffTheLandService.TryExtractEncodedCommandArgument(target.Name, target.CommandLine);
        if (base64 is null)
        {
            DecodedCommandLineText = null;
            return;
        }

        try
        {
            DecodedCommandLineText = LivingOffTheLandService.DecodeEncodedCommand(base64);
        }
        catch (Exception ex)
        {
            DecodedCommandLineText = $"(couldn't decode - {ex.Message})";
        }
    }

    /// <summary>Round 17, #864: "Scan this process's image file" - shells to MpCmdRun.exe
    /// -Scan -ScanType 3 -File &lt;image path&gt; and reports the result via StatusMessage. A quick
    /// trigger from the Processes tab, distinct from the Security tab's full streamed-output scan
    /// UI - this one just waits (up to 5 minutes) and reports pass/fail.</summary>
    private async Task ScanSelectedWithDefenderAsync()
    {
        var target = SelectedProcess;
        if (target is null || string.IsNullOrWhiteSpace(target.FilePath))
        {
            StatusMessage = "Selected process has no resolvable image path.";
            return;
        }

        string path = target.FilePath;
        StatusMessage = $"Scanning {Path.GetFileName(path)} with Windows Defender...";
        try
        {
            var (exitCode, output) = await Task.Run(() => DefenderService.RunCapturedWithExitCode(
                DefenderService.ResolveMpCmdRunPath(),
                DefenderService.BuildScanArgs(DefenderService.DefenderScanType.Custom, path),
                TimeSpan.FromMinutes(5)));

            StatusMessage = exitCode == 0
                ? $"Defender scan of {Path.GetFileName(path)} finished - no threats reported."
                : $"Defender scan of {Path.GetFileName(path)} finished (exit code {exitCode}). {output.Trim()}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't run Defender scan: {ex.Message}";
        }
    }

    /// <summary>Item 64: Wait Chain Traversal analysis for SelectedProcess - one indented section
    /// per thread the API returned a chain for, prefixed with a loud "DEADLOCK CYCLE DETECTED"
    /// marker when GetThreadWaitChain itself flagged that thread's chain as a cycle. See
    /// WaitChainAnalysisService for the actual native call and its own timeout handling.</summary>
    private async Task AnalyzeWaitChainAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        WaitChainResults.Clear();
        WaitChainResults.Add("Analysing wait chain…");

        var result = await Task.Run(() => WaitChainAnalysisService.Analyze(pid));

        // The selection (or the whole app) may have moved on while this ran in the background.
        if (SelectedProcess?.Pid != pid) return;

        WaitChainResults.Clear();
        if (result.ErrorMessage is not null)
        {
            WaitChainResults.Add(result.ErrorMessage);
            return;
        }

        foreach (var chain in result.Chains)
        {
            WaitChainResults.Add(chain.IsDeadlockCycle
                ? $"Thread {chain.ThreadId} — DEADLOCK CYCLE DETECTED:"
                : $"Thread {chain.ThreadId}:");
            for (int i = 0; i < chain.Nodes.Count; i++)
                WaitChainResults.Add($"{new string(' ', (i + 1) * 2)}→ {chain.Nodes[i]}");
        }
    }

    /// <summary>Item 65: writes a mini or full dump of SelectedProcess to a user-chosen file - see
    /// ProcessDumpService for the actual MiniDumpWriteDump call. Runs via Task.Run (a Full dump of
    /// a large process can take a while) and reports the outcome through StatusMessage, the same
    /// convention every other process-control action on this view model already uses.</summary>
    private void CreateProcessDump(ProcessDumpService.DumpKind kind)
    {
        var target = SelectedProcess;
        if (target is null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{target.Name}_{target.Pid}_{kind}.dmp",
            Filter = "Dump files (*.dmp)|*.dmp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;
        int pid = target.Pid;
        string name = target.Name;
        string kindText = kind == ProcessDumpService.DumpKind.Full ? "full" : "mini";

        StatusMessage = $"Writing {kindText} dump for {name} (PID {pid})…";

        _ = Task.Run(() => ProcessDumpService.WriteDump(pid, path, kind)).ContinueWith(t =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var (success, error) = t.Result;
                StatusMessage = success
                    ? $"Wrote {kindText} dump for {name} to {path}."
                    : $"Couldn't write dump for {name}: {error}";
            });
        });
    }

    /// <summary>Round 7 #17: true only for a process actually named svchost - the reverse-lookup
    /// button is only meaningful there, so it stays disabled for every other row rather than just
    /// silently returning an empty list.</summary>
    private bool IsSvchostSelected() =>
        SelectedProcess is not null && SelectedProcess.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase);

    private void LoadSelectedProcessHostedServices()
    {
        SelectedProcessHostedServices.Clear();
        var target = SelectedProcess;
        if (target is null) return;

        var byPid = ServiceControlService.ReadServicesByPid();
        if (!byPid.TryGetValue(target.Pid, out var services) || services.Count == 0)
        {
            SelectedProcessHostedServices.Add("(no hosted services found)");
            return;
        }
        foreach (var name in services.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            SelectedProcessHostedServices.Add(name);
    }

    /// <summary>Round 7 #9: "what has this file open" - see FileLockLookupService for why this uses
    /// the Restart Manager API rather than a raw handle-table walk.</summary>
    private async Task LookupFileLockAsync()
    {
        string path = FileLockPath;
        FileLockResults.Clear();
        FileLockResults.Add("Checking…");

        var owners = await Task.Run(() => FileLockLookupService.FindProcessesWithFileOpen(path));

        FileLockResults.Clear();
        if (owners.Count == 0)
        {
            FileLockResults.Add("(no processes found holding this file open)");
            return;
        }
        foreach (var owner in owners)
            FileLockResults.Add($"{owner.AppName} (PID {owner.Pid}){(owner.Restartable ? "" : " - not restartable")}");
    }

    /// <summary>#408: on-demand single-process address-space walk - see
    /// AddressSpaceInspectionService's remarks for why this is strictly button-triggered.</summary>
    private async Task LoadSelectedProcessAddressSpaceAsync()
    {
        var target = SelectedProcess;
        if (target is null) return;
        int pid = target.Pid;

        SelectedProcessAddressSpace = null;
        StatusMessage = $"Scanning address space for {target.Name} (PID {pid})...";

        var summary = await Task.Run(() => AddressSpaceInspectionService.Walk(pid));

        // The selection (or the whole app) may have moved on while this ran in the background.
        if (SelectedProcess?.Pid != pid) return;

        SelectedProcessAddressSpace = summary;
        StatusMessage = summary.Error is not null
            ? $"Couldn't read address space for {target.Name}: {summary.Error}"
            : $"Address space for {summary.ProcessName} (PID {summary.Pid}): {summary.TotalRegionsScanned:N0} regions scanned" +
              (summary.WasCapped ? " (stopped early - the walk hit its scan cap)." : ".");
    }

    /// <summary>Round 7 #4: trims the selected process's working set - see
    /// ProcessControlService.TrimWorkingSet (EmptyWorkingSet).</summary>
    private void TrimWorkingSet()
    {
        var target = SelectedProcess;
        if (target is null) return;

        var (success, error) = ProcessControlService.TrimWorkingSet(target.Pid);
        StatusMessage = success
            ? $"Trimmed working set for {target.Name} (PID {target.Pid})."
            : $"Couldn't trim working set for {target.Name}: {error}";

        // #972: recorded but never undoable - Windows lets a trimmed working set regrow on
        // demand, there's no "before" figure worth restoring.
        ChangeJournalService.Append(new ChangeJournalEntry
        {
            Kind = ChangeKind.ProcessTrimWorkingSet,
            Target = $"{target.Name} (PID {target.Pid})",
            ActionDescription = "Trimmed working set",
            TriggeredBy = "Processes tab",
            Success = success,
            IsUndoable = false,
            NotUndoableReason = "Trimming a working set can't be undone - Windows lets it regrow on demand.",
            Pid = target.Pid,
            ProcessName = target.Name,
        });
    }

    /// <summary>Round 7 #8: suspend/resume the selected process - see
    /// ProcessControlService.Suspend/Resume (NtSuspendProcess/NtResumeProcess).</summary>
    private void SetSuspended(bool suspend)
    {
        var target = SelectedProcess;
        if (target is null) return;

        var (success, error) = suspend
            ? ProcessControlService.Suspend(target.Pid)
            : ProcessControlService.Resume(target.Pid);

        StatusMessage = success
            ? $"{(suspend ? "Suspended" : "Resumed")} {target.Name} (PID {target.Pid})."
            : $"Couldn't {(suspend ? "suspend" : "resume")} {target.Name}: {error}";

        // #972: record every mutation this app performs - suspend's inverse is resume and vice
        // versa (both checked, at undo time, against the process still actually being alive - see
        // ChangeJournalViewModel.Evaluate).
        ChangeJournalService.Append(new ChangeJournalEntry
        {
            Kind = suspend ? ChangeKind.ProcessSuspend : ChangeKind.ProcessResume,
            Target = $"{target.Name} (PID {target.Pid})",
            ActionDescription = suspend ? "Suspended" : "Resumed",
            TriggeredBy = "Processes tab",
            Success = success,
            IsUndoable = success,
            Pid = target.Pid,
            ProcessName = target.Name,
        });

        if (success) _ = RefreshAsync();
    }

    /// <summary>Round 7 #5: loads SelectedProcess's current affinity mask into one checkbox per
    /// logical processor - a plain read, so unlike the modules/environment viewers this runs
    /// automatically on selection rather than needing its own button; only *applying* a change is
    /// an explicit, separate action (ApplyAffinityCommand).</summary>
    private void LoadAffinityForSelection()
    {
        AffinityCores.Clear();
        var target = SelectedProcess;
        if (target is null) return;

        long? mask = ProcessControlService.GetAffinity(target.Pid);
        int logicalCount = Environment.ProcessorCount;
        for (int i = 0; i < logicalCount; i++)
        {
            AffinityCores.Add(new CpuAffinityOption
            {
                Index = i,
                IsSelected = mask is null || (mask.Value & (1L << i)) != 0,
            });
        }
    }

    private void ApplyAffinity()
    {
        var target = SelectedProcess;
        if (target is null || AffinityCores.Count == 0) return;

        long mask = 0;
        foreach (var core in AffinityCores)
            if (core.IsSelected) mask |= 1L << core.Index;

        if (mask == 0)
        {
            StatusMessage = "At least one core must stay selected.";
            return;
        }

        long? before = ProcessControlService.GetAffinity(target.Pid);
        var (success, error) = ProcessControlService.SetAffinity(target.Pid, mask);
        StatusMessage = success
            ? $"Updated CPU affinity for {target.Name} (PID {target.Pid})."
            : $"Couldn't set affinity for {target.Name}: {error}";

        // #972: record every mutation this app performs - IsUndoable only when a "before" mask
        // was actually readable, since ProcessControlService.SetAffinity needs a concrete mask
        // to restore, not just "success".
        ChangeJournalService.Append(new ChangeJournalEntry
        {
            Kind = ChangeKind.ProcessAffinityChange,
            Target = $"{target.Name} (PID {target.Pid})",
            ActionDescription = "Changed CPU affinity",
            BeforeValue = before?.ToString(),
            AfterValue = mask.ToString(),
            TriggeredBy = "Processes tab",
            Success = success,
            IsUndoable = success && before is not null,
            NotUndoableReason = before is null ? "The prior affinity mask couldn't be read." : null,
            Pid = target.Pid,
            ProcessName = target.Name,
        });
    }

    /// <summary>Round 7 #6: applies the toolbar-selected priority class to SelectedProcess - see
    /// ProcessControlService.SetPriority.</summary>
    private void SetPriority()
    {
        var target = SelectedProcess;
        if (target is null || !Enum.TryParse<ProcessPriorityClass>(SelectedPriorityName, out var priority)) return;

        string? before = Enum.TryParse<ProcessPriorityClass>(target.PriorityClassName, out var beforePriority)
            ? beforePriority.ToString()
            : null;

        var (success, error) = ProcessControlService.SetPriority(target.Pid, priority);
        StatusMessage = success
            ? $"Set {target.Name} (PID {target.Pid}) priority to {priority}."
            : $"Couldn't change priority for {target.Name}: {error}";

        // #972: record every mutation this app performs - IsUndoable only when a "before"
        // priority was actually known, same reasoning as ApplyAffinity above.
        ChangeJournalService.Append(new ChangeJournalEntry
        {
            Kind = ChangeKind.ProcessPriorityChange,
            Target = $"{target.Name} (PID {target.Pid})",
            ActionDescription = "Changed priority",
            BeforeValue = before,
            AfterValue = priority.ToString(),
            TriggeredBy = "Processes tab",
            Success = success,
            IsUndoable = success && before is not null,
            NotUndoableReason = before is null ? "The prior priority couldn't be read." : null,
            Pid = target.Pid,
            ProcessName = target.Name,
        });

        if (success) _ = RefreshAsync();
    }

    /// <summary>#406: toggles SelectedProcess's image name in the pinned leak-watch list.</summary>
    private void ToggleLeakWatch()
    {
        var target = SelectedProcess;
        if (target is null) return;

        bool wasWatched = _leakWatch.IsWatched(target.Name);
        bool ok = _leakWatch.Toggle(target.Name);

        StatusMessage = !ok
            ? $"Can't watch {target.Name}: the leak-watch list is full - unwatch something else first."
            : wasWatched
                ? $"Stopped watching {target.Name} for leaks."
                : $"Watching {target.Name} for leaks - see the Memory tab's Leak watch list.";
    }

    /// <summary>#404: fires a toast the first tick a process's GDI or USER handle count reaches
    /// the quota warning threshold, and again if it drops back under and later re-crosses -
    /// edge-triggered per pid so a sustained warning doesn't spam a toast every tick. Pids no
    /// longer present (process exited) are dropped from both tracking sets so they don't leak
    /// forever.</summary>
    private void CheckGdiUserQuotaAlerts()
    {
        var livePids = new HashSet<int>();
        foreach (var row in Processes)
        {
            livePids.Add(row.Pid);

            if (row.IsGdiQuotaWarning)
            {
                if (_gdiAlertedPids.Add(row.Pid))
                    ToastService.Show("GDI object quota warning",
                        $"{row.Name} (PID {row.Pid}) is using {row.GdiHandleCount:N0} of {GdiQuotaService.GdiQuota:N0} GDI objects - 80%+ of its quota.");
            }
            else
            {
                _gdiAlertedPids.Remove(row.Pid);
            }

            if (row.IsUserQuotaWarning)
            {
                if (_userAlertedPids.Add(row.Pid))
                    ToastService.Show("USER object quota warning",
                        $"{row.Name} (PID {row.Pid}) is using {row.UserHandleCount:N0} of {GdiQuotaService.UserQuota:N0} USER objects - 80%+ of its quota.");
            }
            else
            {
                _userAlertedPids.Remove(row.Pid);
            }
        }

        _gdiAlertedPids.RemoveWhere(pid => !livePids.Contains(pid));
        _userAlertedPids.RemoveWhere(pid => !livePids.Contains(pid));
    }

    public void Dispose()
    {
        _timer.Stop();
        _monitor.Dispose();

        // Round 17, item 63: dispose every still-tracked Process handle (see TrackForExit).
        foreach (var proc in _trackedProcesses.Values)
        {
            try { proc.Dispose(); } catch { /* best-effort */ }
        }
        _trackedProcesses.Clear();
        _dotNetCounters.Dispose();
        _processHistory.Flush();
    }
}
