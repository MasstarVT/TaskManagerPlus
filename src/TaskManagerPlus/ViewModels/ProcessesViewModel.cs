using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ICollectionView ProcessesView { get; }

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
                SelectedProcessEnvironment.Clear();
                SelectedProcessHandleTypes.Clear();
                SelectedProcessHostedServices.Clear();
                FileLockResults.Clear();
                WaitChainNodes.Clear();
                WaitChainStatusText = string.Empty;
                LoadAffinityForSelection();
                RefreshSelectedWaitBreakdown();
            }
        }
    }

    /// <summary>Loaded modules/DLLs for SelectedProcess (#39), populated on demand via
    /// ViewModulesCommand rather than every tick - walking a process's full module list is
    /// comparatively expensive and something Task Manager itself also only does on request.</summary>
    public ObservableCollection<string> SelectedProcessModules { get; } = new();

    /// <summary>Round 7 #3: environment variables for SelectedProcess, populated on demand via
    /// ViewEnvironmentCommand - see ProcessEnvironmentService for why this needs a PEB memory walk
    /// and is best-effort/64-bit-only.</summary>
    public ObservableCollection<string> SelectedProcessEnvironment { get; } = new();

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
    /// selection).</summary>
    public AsyncRelayCommand AnalyzeWaitChainCommand { get; }

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
    public RelayCommand TrimWorkingSetCommand { get; }
    public RelayCommand SuspendCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand ApplyAffinityCommand { get; }
    public RelayCommand SetPriorityCommand { get; }

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

    public ProcessesViewModel()
    {
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterPredicate;

        EndTaskCommand = new RelayCommand(_ => EndSelected(tree: false), _ => SelectedProcess is not null);
        EndProcessTreeCommand = new RelayCommand(_ => EndSelected(tree: true), _ => SelectedProcess is not null);
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshAsync());
        ViewModulesCommand = new RelayCommand(_ => LoadSelectedProcessModules(), _ => SelectedProcess is not null);
        ViewEnvironmentCommand = new RelayCommand(_ => LoadSelectedProcessEnvironment(), _ => SelectedProcess is not null);
        ViewHandleTypesCommand = new RelayCommand(_ => _ = LoadSelectedProcessHandleTypesAsync(), _ => SelectedProcess is not null);
        ViewHostedServicesCommand = new RelayCommand(_ => LoadSelectedProcessHostedServices(), _ => IsSvchostSelected());
        LookupFileLockCommand = new RelayCommand(_ => _ = LookupFileLockAsync(), _ => !string.IsNullOrWhiteSpace(FileLockPath));
        TrimWorkingSetCommand = new RelayCommand(_ => TrimWorkingSet(), _ => SelectedProcess is not null);
        SuspendCommand = new RelayCommand(_ => SetSuspended(true), _ => SelectedProcess is not null);
        ResumeCommand = new RelayCommand(_ => SetSuspended(false), _ => SelectedProcess is not null);
        ApplyAffinityCommand = new RelayCommand(_ => ApplyAffinity(), _ => SelectedProcess is not null && AffinityCores.Count > 0);
        SetPriorityCommand = new RelayCommand(_ => SetPriority(), _ => SelectedProcess is not null);
        // #271: param is the flagged-row ProcessRow when invoked from #272's inline link, or null
        // (falls back to SelectedProcess) from the context menu.
        AnalyzeWaitChainCommand = new AsyncRelayCommand(param => AnalyzeWaitChainAsync(param as ProcessRow ?? SelectedProcess));

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
            var latest = await Task.Run(() => _monitor.Sample());
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
                existing.NotRespondingSeconds = fresh.NotRespondingSeconds;
                existing.ThreadCount = fresh.ThreadCount;
                existing.HandleCount = fresh.HandleCount;
                existing.SignatureStatus = fresh.SignatureStatus;
                existing.IsHighPrivilege = fresh.IsHighPrivilege;
                existing.IsLeakSuspect = fresh.IsLeakSuspect;
                existing.GpuPercent = fresh.GpuPercent;
                existing.PriorityClassName = fresh.PriorityClassName;
                existing.GdiHandleCount = fresh.GdiHandleCount;
                existing.UserHandleCount = fresh.UserHandleCount;
                existing.IsSuspended = fresh.IsSuspended;
                existing.SpawnGroupSize = fresh.SpawnGroupSize;
                existing.DuplicateInstanceCount = fresh.DuplicateInstanceCount;
                existing.IsDuplicateInstanceOutlier = fresh.IsDuplicateInstanceOutlier;
                // #266/#270
                existing.PowerThrottleText = fresh.PowerThrottleText;
                existing.IoPriorityText = fresh.IoPriorityText;
                existing.IsBackgroundIoPriority = fresh.IsBackgroundIoPriority;
                existing.MemoryPriorityText = fresh.MemoryPriorityText;
                // CommandLine/FilePath/StartTime/User/ParentPid/ParentName don't change for the
                // lifetime of a pid - no need to reassign them every tick like the values above
                // that actually vary.
                latestByPid.Remove(existing.Pid);
            }
            else
            {
                Processes.RemoveAt(i);
            }
        }

        // Anything left in latestByPid is a newly-seen process.
        foreach (var row in latestByPid.Values)
            Processes.Add(row);
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

    /// <summary>Loads SelectedProcess's module/DLL list (#39) - a plain synchronous read of
    /// Process.Modules, which is itself fast; the expensive part avoided by making this on-demand
    /// is doing it for every process on every tick, not this one call.</summary>
    private void LoadSelectedProcessModules()
    {
        SelectedProcessModules.Clear();
        var target = SelectedProcess;
        if (target is null) return;

        try
        {
            using var proc = Process.GetProcessById(target.Pid);
            foreach (ProcessModule module in proc.Modules)
                SelectedProcessModules.Add($"{module.ModuleName}  —  {module.FileName}");
        }
        catch (Exception ex)
        {
            // Protected process (access denied) or it exited before this ran - a real,
            // expected limitation worth surfacing inline rather than failing silently.
            SelectedProcessModules.Add($"(couldn't read modules: {ex.Message})");
        }
    }

    /// <summary>Round 7 #3: on-demand environment-variable dump for SelectedProcess - see
    /// ProcessEnvironmentService for the PEB-walk technique and its 64-bit-only limitation.</summary>
    private void LoadSelectedProcessEnvironment()
    {
        SelectedProcessEnvironment.Clear();
        var target = SelectedProcess;
        if (target is null) return;

        foreach (var entry in ProcessEnvironmentService.Read(target.Pid))
            SelectedProcessEnvironment.Add(entry);
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

        var (success, error) = ProcessControlService.SetAffinity(target.Pid, mask);
        StatusMessage = success
            ? $"Updated CPU affinity for {target.Name} (PID {target.Pid})."
            : $"Couldn't set affinity for {target.Name}: {error}";
    }

    /// <summary>Round 7 #6: applies the toolbar-selected priority class to SelectedProcess - see
    /// ProcessControlService.SetPriority.</summary>
    private void SetPriority()
    {
        var target = SelectedProcess;
        if (target is null || !Enum.TryParse<ProcessPriorityClass>(SelectedPriorityName, out var priority)) return;

        var (success, error) = ProcessControlService.SetPriority(target.Pid, priority);
        StatusMessage = success
            ? $"Set {target.Name} (PID {target.Pid}) priority to {priority}."
            : $"Couldn't change priority for {target.Name}: {error}";

        if (success) _ = RefreshAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _monitor.Dispose();
        _dotNetCounters.Dispose();
    }
}
