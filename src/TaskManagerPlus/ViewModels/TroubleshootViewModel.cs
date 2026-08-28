using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #901: backs the Troubleshoot tab - a symptom-picker landing grid plus, once a symptom is
/// selected, a scripted <see cref="TroubleshootRun"/> of ordered <see cref="DiagnosticStep"/>s run
/// against existing services (EventLogService, NetworkDiagnosticsService, PowerPlanService,
/// SensorMonitorService, ProcessMonitorService (via ProcessesViewModel), DiskFragmentationService,
/// ...) instead of making a user guess which of the other twelve tabs to open first. Takes the
/// shared <see cref="PerformanceViewModel"/> and <see cref="ProcessesViewModel"/> (the same
/// instances MainViewModel already composes for the CPU/Memory/Storage/Network/Processes tabs) so
/// branches like "My PC is slow" and "My disk sits at 100%" read already-polled live data instead
/// of re-sampling from scratch - see CLAUDE.md's shared-sampler note.
///
/// #914: every symptom (902-913) is one <see cref="TroubleshootBranchDefinition"/> in
/// <see cref="_branches"/> - a title/description plus a step-list factory and a verdict function -
/// rather than a hand-written case in a switch statement. A step's own
/// <see cref="DiagnosticStep.ShouldRun"/> predicate (reading an earlier step's already-recorded
/// Status off the same run, via <see cref="StepById"/>) expresses branching that would otherwise be
/// procedural if/else in this ViewModel - see #911's disk branch (fragmentation only gated by media
/// type) and #912's battery branch (report/srum/plan all gated on "a battery was actually found").
/// Adding a 14th symptom is adding one more <see cref="TroubleshootBranchDefinition"/> to the list
/// built in the constructor, not a new hand-written method plus a new switch case.
///
/// Every automatic step is run sequentially and raced against its own
/// <see cref="DiagnosticStep.Timeout"/> via Task.WhenAny + a per-step CancellationTokenSource (see
/// <see cref="RunOneStepAsync"/>), so one hung WMI/tool call can never freeze the rest of a run -
/// the same "never let one call hang the UI forever" rule SensorMonitorService/
/// HandleInspectionService already document for their own background work. A step marked
/// <see cref="DiagnosticStep.IsManual"/> (e.g. #909's wpr/tracerpt DPC capture, #911's
/// full-volume fragmentation analyze pass) is skipped by that automatic loop entirely and instead
/// runs on demand via <see cref="RunManualStepCommand"/> - consistent with CLAUDE.md's "on-demand
/// vs. polled" convention for anything heavier than a trivial read.
///
/// #915: every finished run is persisted by <see cref="TroubleshootRunHistoryService"/> to
/// AppPaths.SettingsDirectory\Runs\ - <see cref="PastRuns"/> backs the tab's "Past runs" panel,
/// which can reopen a saved run read-only (<see cref="OpenSavedRunCommand"/>) or re-run the same
/// symptom fresh (<see cref="RerunSavedCommand"/>).
/// </summary>
public sealed class TroubleshootViewModel : ObservableObject, IDisposable
{
    private readonly PerformanceViewModel _performance;
    private readonly ProcessesViewModel _processes;
    private readonly List<TroubleshootBranchDefinition> _branches = new();
    private bool _isRunning;

    // #938-949: the Timeline panel - a sibling "page" of this tab, reached from the landing page's
    // "Timeline" button the same way "Past runs" already is (see ShowLanding/ShowPastRuns below).
    public TimelineViewModel Timeline { get; }

    // #950-958: the Baselines panel - a third sibling "page", same shape as Timeline above (see
    // ShowBaselines/IsShowingBaselines).
    public BaselineViewModel Baselines { get; }

    // #959-966: the Background Health panel - a fourth sibling "page", same shape as Timeline/
    // Baselines above (see ShowBackgroundHealth/IsShowingBackgroundHealth).
    public BackgroundHealthViewModel BackgroundHealth { get; }

    // #973: the "Changes made by this app" panel - a fifth sibling "page", same shape as
    // Timeline/Baselines/BackgroundHealth above (see ShowChangeJournal/IsShowingChangeJournal).
    // No live ViewModel dependencies of its own - it reads change-journal.jsonl on demand and
    // undoes through the plain static Services/*.cs methods directly, so a plain parameterless
    // ChangeJournalViewModel() is enough.
    public ChangeJournalViewModel ChangeJournal { get; } = new();

    // suggestions.md #981-987: the "Evidence Bundle" panel - a sixth sibling "page", same shape as
    // Timeline/Baselines/BackgroundHealth/ChangeJournal above (see ShowEvidenceBundle/
    // IsShowingEvidenceBundle). Takes a small CollectContext record (rather than each ViewModel
    // individually) so EvidenceBundleService's collectors have exactly the live-state references
    // they need (#981's AppFindings collector reuses RulesEngineService.BuildMetricBag/Evaluate,
    // the same call SummaryViewModel's Health Check card already makes).
    public EvidenceBundleViewModel EvidenceBundle { get; }

    // suggestions.md #990: the "Glossary" panel - a seventh sibling "page", same shape as
    // Timeline/Baselines/BackgroundHealth/ChangeJournal/EvidenceBundle above.
    public GlossaryViewModel Glossary { get; } = new();

    // suggestions.md #999: the "Network activity" disclosure panel - an eighth sibling "page".
    public NetworkActivityViewModel NetworkActivity { get; } = new();

    // suggestions.md #995: the "Bundle review" panel - a ninth sibling "page".
    public BundleReviewViewModel BundleReview { get; } = new();

    public ObservableCollection<SymptomCard> Symptoms { get; } = new();

    /// <summary>#915: saved run transcripts, newest first - populated on demand when the "Past
    /// runs" panel is opened rather than kept live, since the Runs folder is only read from, never
    /// watched.</summary>
    public ObservableCollection<TroubleshootRunRecord> PastRuns { get; } = new();

    private TroubleshootRun? _selectedRun;
    public TroubleshootRun? SelectedRun
    {
        get => _selectedRun;
        private set
        {
            if (SetProperty(ref _selectedRun, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowPastRuns));
                OnPropertyChanged(nameof(ShowTimeline));
                OnPropertyChanged(nameof(ShowBaselines));
                OnPropertyChanged(nameof(ShowBackgroundHealth));
                OnPropertyChanged(nameof(ShowChangeJournal));
                OnPropertyChanged(nameof(ShowEvidenceBundle));
                OnPropertyChanged(nameof(ShowGlossary));
                OnPropertyChanged(nameof(ShowNetworkActivity));
                OnPropertyChanged(nameof(ShowBundleReview));
            }
        }
    }

    private bool _isShowingPastRuns;
    public bool IsShowingPastRuns
    {
        get => _isShowingPastRuns;
        private set
        {
            if (SetProperty(ref _isShowingPastRuns, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowPastRuns));
            }
        }
    }

    // #938: the Timeline panel is a third sibling "page", same shape as IsShowingPastRuns above.
    private bool _isShowingTimeline;
    public bool IsShowingTimeline
    {
        get => _isShowingTimeline;
        private set
        {
            if (SetProperty(ref _isShowingTimeline, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowTimeline));
            }
        }
    }

    // #950-958: landing page <-> Baselines panel, mirroring IsShowingTimeline above.
    private bool _isShowingBaselines;
    public bool IsShowingBaselines
    {
        get => _isShowingBaselines;
        private set
        {
            if (SetProperty(ref _isShowingBaselines, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowBaselines));
            }
        }
    }

    // #959-966: landing page <-> Background Health panel, mirroring IsShowingBaselines above.
    private bool _isShowingBackgroundHealth;
    public bool IsShowingBackgroundHealth
    {
        get => _isShowingBackgroundHealth;
        private set
        {
            if (SetProperty(ref _isShowingBackgroundHealth, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowBackgroundHealth));
            }
        }
    }

    // #973: landing page <-> "Changes made by this app" panel, mirroring IsShowingBackgroundHealth above.
    private bool _isShowingChangeJournal;
    public bool IsShowingChangeJournal
    {
        get => _isShowingChangeJournal;
        private set
        {
            if (SetProperty(ref _isShowingChangeJournal, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowChangeJournal));
                if (value) ChangeJournal.Refresh();
            }
        }
    }

    // suggestions.md #981-987: landing page <-> Evidence Bundle panel, mirroring
    // IsShowingChangeJournal above.
    private bool _isShowingEvidenceBundle;
    public bool IsShowingEvidenceBundle
    {
        get => _isShowingEvidenceBundle;
        private set
        {
            if (SetProperty(ref _isShowingEvidenceBundle, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowEvidenceBundle));
            }
        }
    }

    // suggestions.md #990: landing page <-> Glossary panel, mirroring IsShowingEvidenceBundle above.
    private bool _isShowingGlossary;
    public bool IsShowingGlossary
    {
        get => _isShowingGlossary;
        private set
        {
            if (SetProperty(ref _isShowingGlossary, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowGlossary));
            }
        }
    }

    // suggestions.md #999: landing page <-> Network activity panel.
    private bool _isShowingNetworkActivity;
    public bool IsShowingNetworkActivity
    {
        get => _isShowingNetworkActivity;
        private set
        {
            if (SetProperty(ref _isShowingNetworkActivity, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowNetworkActivity));
            }
        }
    }

    // suggestions.md #995: landing page <-> Bundle review panel.
    private bool _isShowingBundleReview;
    public bool IsShowingBundleReview
    {
        get => _isShowingBundleReview;
        private set
        {
            if (SetProperty(ref _isShowingBundleReview, value))
            {
                OnPropertyChanged(nameof(ShowLanding));
                OnPropertyChanged(nameof(ShowBundleReview));
            }
        }
    }

    /// <summary>True for the symptom-card landing grid.</summary>
    public bool ShowLanding => SelectedRun is null && !IsShowingPastRuns && !IsShowingTimeline && !IsShowingBaselines && !IsShowingBackgroundHealth && !IsShowingChangeJournal && !IsShowingEvidenceBundle
        && !IsShowingGlossary && !IsShowingNetworkActivity && !IsShowingBundleReview;

    /// <summary>True for the "Past runs" list.</summary>
    public bool ShowPastRuns => SelectedRun is null && IsShowingPastRuns;

    /// <summary>True for the Timeline panel.</summary>
    public bool ShowTimeline => SelectedRun is null && IsShowingTimeline;

    /// <summary>True for the Baselines panel.</summary>
    public bool ShowBaselines => SelectedRun is null && IsShowingBaselines;

    /// <summary>True for the Background Health panel.</summary>
    public bool ShowBackgroundHealth => SelectedRun is null && IsShowingBackgroundHealth;

    /// <summary>True for the "Changes made by this app" panel.</summary>
    public bool ShowChangeJournal => SelectedRun is null && IsShowingChangeJournal;

    /// <summary>True for the Evidence Bundle panel.</summary>
    public bool ShowEvidenceBundle => SelectedRun is null && IsShowingEvidenceBundle;

    /// <summary>True for the Glossary panel.</summary>
    public bool ShowGlossary => SelectedRun is null && IsShowingGlossary;

    /// <summary>True for the Network activity panel.</summary>
    public bool ShowNetworkActivity => SelectedRun is null && IsShowingNetworkActivity;

    /// <summary>True for the Bundle review panel.</summary>
    public bool ShowBundleReview => SelectedRun is null && IsShowingBundleReview;

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    public RelayCommand RunSymptomCommand { get; }
    public RelayCommand BackToSymptomsCommand { get; }
    public RelayCommand ShowPastRunsCommand { get; }
    public RelayCommand HidePastRunsCommand { get; }
    public RelayCommand OpenSavedRunCommand { get; }
    public RelayCommand RerunSavedCommand { get; }
    public AsyncRelayCommand RunManualStepCommand { get; }

    // #938: landing page <-> Timeline panel, mirroring ShowPastRunsCommand/HidePastRunsCommand.
    public RelayCommand ShowTimelineCommand { get; }
    public RelayCommand HideTimelineCommand { get; }

    // #950-958: landing page <-> Baselines panel, mirroring ShowTimelineCommand/HideTimelineCommand.
    public RelayCommand ShowBaselinesCommand { get; }
    public RelayCommand HideBaselinesCommand { get; }

    // #959-966: landing page <-> Background Health panel, mirroring ShowBaselinesCommand/HideBaselinesCommand.
    public RelayCommand ShowBackgroundHealthCommand { get; }
    public RelayCommand HideBackgroundHealthCommand { get; }

    // #973: landing page <-> "Changes made by this app" panel, mirroring ShowBackgroundHealthCommand/HideBackgroundHealthCommand.
    public RelayCommand ShowChangeJournalCommand { get; }
    public RelayCommand HideChangeJournalCommand { get; }

    // suggestions.md #981-987: landing page <-> Evidence Bundle panel, mirroring
    // ShowChangeJournalCommand/HideChangeJournalCommand.
    public RelayCommand ShowEvidenceBundleCommand { get; }
    public RelayCommand HideEvidenceBundleCommand { get; }

    // suggestions.md #990: landing page <-> Glossary panel.
    public RelayCommand ShowGlossaryCommand { get; }
    public RelayCommand HideGlossaryCommand { get; }

    // suggestions.md #999: landing page <-> Network activity panel.
    public RelayCommand ShowNetworkActivityCommand { get; }
    public RelayCommand HideNetworkActivityCommand { get; }

    // suggestions.md #995: landing page <-> Bundle review panel.
    public RelayCommand ShowBundleReviewCommand { get; }
    public RelayCommand HideBundleReviewCommand { get; }

    public TroubleshootViewModel(PerformanceViewModel performance, ProcessesViewModel processes, LoggingViewModel logging,
        EnergyThermalsViewModel energyThermals, SystemSpecsViewModel systemSpecs, ServicesViewModel services, RulesEngineService rulesEngine,
        BackgroundHealthCollectorService backgroundHealthCollector)
    {
        _performance = performance;
        _processes = processes;
        Timeline = new TimelineViewModel(logging);
        Baselines = new BaselineViewModel(performance, energyThermals, systemSpecs, services, processes, rulesEngine);
        BackgroundHealth = new BackgroundHealthViewModel(backgroundHealthCollector, rulesEngine);
        EvidenceBundle = new EvidenceBundleViewModel(new EvidenceBundleService.CollectContext(
            performance, processes, energyThermals, systemSpecs, services, rulesEngine));

        RegisterBranch("slow", "My PC is slow right now", "Checks CPU/RAM/disk load, top offenders, and background maintenance work.", BuildSlowPcSteps, BuildSlowPcVerdict);
        RegisterBranch("crash", "It crashes or blue-screens", "Checks crash events, minidumps, reliability records, hardware errors, and recent driver installs.", BuildCrashSteps, BuildCrashVerdict);
        RegisterBranch("sleep", "It won't go to sleep / wakes on its own", "Checks active power requests, wake timers, last wake source, and the sleep study report.", BuildSleepSteps, BuildSleepVerdict);
        RegisterBranch("no-internet", "No internet", "Walks the network stack layer by layer: adapter, address, gateway, DNS, then a real connection.", BuildNoInternetSteps, BuildNoInternetVerdict);
        RegisterBranch("fans", "Fans are loud / it runs hot", "Correlates fan/temperature sensors against CPU load and the active power plan.", BuildFansSteps, BuildFansVerdict);
        RegisterBranch("boot", "It boots or signs in slowly", "Reads Windows' own boot-performance diagnostics and matches culprits to Startup/Services.", BuildBootSteps, BuildBootVerdict);
        RegisterBranch("shutdown", "It takes forever to shut down", "Reads shutdown-degradation and profile-unload events, and the service kill timeout.", BuildShutdownSteps, BuildShutdownVerdict);
        RegisterBranch("games", "Games or video stutter", "Checks DPC/interrupt time and context-switch rate, with an opt-in wpr/tracerpt capture to narrow down the offending driver.", BuildGamesSteps, BuildGamesVerdict);
        RegisterBranch("wifi", "Wi-Fi keeps dropping", "Reads the WLAN report's disconnect history, WLAN-AutoConfig events, and the adapter's power-saving setting.", BuildWifiSteps, BuildWifiVerdict);
        RegisterBranch("disk", "My disk sits at 100%", "Distinguishes a hogging process, HDD fragmentation, and background indexing/prefetching as the cause.", BuildDiskSteps, BuildDiskVerdict);
        RegisterBranch("battery", "Battery dies too fast", "Combines the battery report, SRUM energy estimates, and the active power plan into a health headline.", BuildBatterySteps, BuildBatteryVerdict);
        RegisterBranch("device", "A device keeps disconnecting or isn't recognized", "Finds devices reporting a problem, joins them to recent PnP/USB events, and checks USB selective-suspend.", BuildDeviceSteps, BuildDeviceVerdict);

        foreach (var branch in _branches)
            Symptoms.Add(new SymptomCard { Id = branch.SymptomId, Title = branch.Title, Description = branch.Description });

        RunSymptomCommand = new RelayCommand(
            id => { if (id is string sid) _ = RunSymptomAsync(sid); },
            id => id is string sid && !IsRunning && _branches.Any(b => b.SymptomId == sid));
        BackToSymptomsCommand = new RelayCommand(_ => SelectedRun = null, _ => !IsRunning);

        ShowPastRunsCommand = new RelayCommand(_ => { RefreshPastRuns(); IsShowingPastRuns = true; }, _ => !IsRunning && SelectedRun is null);
        HidePastRunsCommand = new RelayCommand(_ => IsShowingPastRuns = false);

        ShowTimelineCommand = new RelayCommand(_ => IsShowingTimeline = true, _ => !IsRunning && SelectedRun is null);
        HideTimelineCommand = new RelayCommand(_ => IsShowingTimeline = false);
        ShowBaselinesCommand = new RelayCommand(_ => IsShowingBaselines = true, _ => !IsRunning && SelectedRun is null);
        HideBaselinesCommand = new RelayCommand(_ => IsShowingBaselines = false);
        ShowBackgroundHealthCommand = new RelayCommand(_ => IsShowingBackgroundHealth = true, _ => !IsRunning && SelectedRun is null);
        HideBackgroundHealthCommand = new RelayCommand(_ => IsShowingBackgroundHealth = false);
        ShowChangeJournalCommand = new RelayCommand(_ => IsShowingChangeJournal = true, _ => !IsRunning && SelectedRun is null);
        HideChangeJournalCommand = new RelayCommand(_ => IsShowingChangeJournal = false);
        ShowEvidenceBundleCommand = new RelayCommand(_ => IsShowingEvidenceBundle = true, _ => !IsRunning && SelectedRun is null);
        HideEvidenceBundleCommand = new RelayCommand(_ => IsShowingEvidenceBundle = false);
        ShowGlossaryCommand = new RelayCommand(_ => IsShowingGlossary = true, _ => !IsRunning && SelectedRun is null);
        HideGlossaryCommand = new RelayCommand(_ => IsShowingGlossary = false);
        ShowNetworkActivityCommand = new RelayCommand(_ => IsShowingNetworkActivity = true, _ => !IsRunning && SelectedRun is null);
        HideNetworkActivityCommand = new RelayCommand(_ => IsShowingNetworkActivity = false);
        ShowBundleReviewCommand = new RelayCommand(_ => IsShowingBundleReview = true, _ => !IsRunning && SelectedRun is null);
        HideBundleReviewCommand = new RelayCommand(_ => IsShowingBundleReview = false);
        OpenSavedRunCommand = new RelayCommand(param =>
        {
            if (param is not TroubleshootRunRecord record) return;
            IsShowingPastRuns = false;
            SelectedRun = TroubleshootRunHistoryService.ToRun(record);
        });
        RerunSavedCommand = new RelayCommand(
            param => { if (param is TroubleshootRunRecord record) { IsShowingPastRuns = false; _ = RunSymptomAsync(record.SymptomId); } },
            param => param is TroubleshootRunRecord record && !IsRunning && _branches.Any(b => b.SymptomId == record.SymptomId));

        RunManualStepCommand = new AsyncRelayCommand(
            async param =>
            {
                if (param is not DiagnosticStep step || !step.IsManualPending) return;
                await RunOneStepAsync(step);
                if (SelectedRun is { VerdictBuilder: { } builder } run)
                {
                    run.VerdictText = SafeBuildVerdict(builder, run);
                    TroubleshootRunHistoryService.Save(run);
                }
            },
            param => param is DiagnosticStep step && step.IsManualPending);
    }

    private void RegisterBranch(string id, string title, string description, Func<List<DiagnosticStep>> buildSteps, Func<TroubleshootRun, string> buildVerdict)
    {
        _branches.Add(new TroubleshootBranchDefinition
        {
            SymptomId = id,
            Title = title,
            Description = description,
            BuildSteps = buildSteps,
            BuildVerdict = buildVerdict,
        });
    }

    private void RefreshPastRuns()
    {
        PastRuns.Clear();
        foreach (var record in TroubleshootRunHistoryService.ListSaved())
            PastRuns.Add(record);
    }

    private async Task RunSymptomAsync(string symptomId)
    {
        if (IsRunning) return;
        var branch = _branches.FirstOrDefault(b => b.SymptomId == symptomId);
        if (branch is null) return;

        var run = new TroubleshootRun { SymptomId = symptomId, DisplayName = branch.Title, VerdictBuilder = branch.BuildVerdict };
        foreach (var step in branch.BuildSteps()) run.Steps.Add(step);

        SelectedRun = run;
        IsShowingPastRuns = false;
        IsRunning = true;
        try
        {
            foreach (var step in run.Steps)
            {
                // #909/#911: a manual/opt-in step (a heavier capture the automatic sequence
                // shouldn't fire unasked) stays Pending until the user triggers it directly via
                // RunManualStepCommand - never run here.
                if (step.IsManual) continue;

                // #914: a step can declare it only applies given an earlier step's result
                // already sitting on this run (e.g. "only check the WLAN report if an adapter was
                // actually found") - evaluated here rather than as a hand-written if/else per branch.
                if (step.ShouldRun is not null && !step.ShouldRun(run))
                {
                    step.Status = DiagnosticStepStatus.Skipped;
                    step.ResultText = "Skipped - not applicable given an earlier step's result.";
                    continue;
                }

                await RunOneStepAsync(step);

                if (step.StopOnFailure && step.Status == DiagnosticStepStatus.Failed)
                {
                    SkipRemaining(run, step);
                    break;
                }
            }

            run.VerdictText = SafeBuildVerdict(branch.BuildVerdict, run);
        }
        finally
        {
            run.IsRunning = false;
            run.FinishedAt = DateTime.Now;
            IsRunning = false;
            TroubleshootRunHistoryService.Save(run);
        }
    }

    /// <summary>Runs one step's Check, racing it against its own Timeout via Task.WhenAny + a
    /// per-step CancellationTokenSource - shared by the automatic run loop above and
    /// RunManualStepCommand below, so a manual/opt-in step gets exactly the same "can never hang
    /// the UI forever" treatment as every automatic one.</summary>
    private static async Task RunOneStepAsync(DiagnosticStep step)
    {
        step.Status = DiagnosticStepStatus.Running;
        var stepStartUtc = DateTime.UtcNow;

        using var cts = new CancellationTokenSource();
        Task<DiagnosticStepResult> checkTask;
        try
        {
            checkTask = step.Check(cts.Token);
        }
        catch (Exception ex)
        {
            // A check that throws synchronously (shouldn't happen given every TroubleshootService
            // method wraps its own body, but a defensive catch here means a bug in one check still
            // can't take the whole run down).
            ApplyResult(step, DiagnosticStepResult.Fail($"Check threw an unexpected error: {ex.Message}"));
            step.Duration = DateTime.UtcNow - stepStartUtc;
            return;
        }

        var delayTask = Task.Delay(step.Timeout, cts.Token);
        var winner = await Task.WhenAny(checkTask, delayTask);
        cts.Cancel(); // release whichever of the two is still pending

        if (winner != checkTask)
        {
            step.Status = DiagnosticStepStatus.TimedOut;
            step.ResultText = $"Timed out after {step.Timeout.TotalSeconds:0.#}s.";
            step.Duration = DateTime.UtcNow - stepStartUtc;
            // Don't await the abandoned check - just make sure a later fault on it can't surface
            // as an unobserved task exception.
            _ = checkTask.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return;
        }

        try
        {
            var result = await checkTask;
            ApplyResult(step, result);
        }
        catch (Exception ex)
        {
            ApplyResult(step, DiagnosticStepResult.Fail($"Check threw an unexpected error: {ex.Message}"));
        }
        step.Duration = DateTime.UtcNow - stepStartUtc;
    }

    private static void ApplyResult(DiagnosticStep step, DiagnosticStepResult result)
    {
        step.Status = result.Status;
        step.ResultText = result.Summary;
        step.Evidence = result.Evidence ?? Array.Empty<string>();
    }

    private static void SkipRemaining(TroubleshootRun run, DiagnosticStep from)
    {
        bool afterFrom = false;
        foreach (var s in run.Steps)
        {
            if (ReferenceEquals(s, from)) { afterFrom = true; continue; }
            if (afterFrom && s.Status == DiagnosticStepStatus.Pending)
            {
                s.Status = DiagnosticStepStatus.Skipped;
                s.ResultText = "Skipped - an earlier layer already failed.";
            }
        }
    }

    private static string SafeBuildVerdict(Func<TroubleshootRun, string> buildVerdict, TroubleshootRun run)
    {
        try { return buildVerdict(run); }
        catch { return "Couldn't build a summary verdict from this run's results - see the step details above."; }
    }

    private static DiagnosticStep? StepById(TroubleshootRun run, string id) => run.Steps.FirstOrDefault(s => s.Id == id);

    // ==================================================================================
    // Branch definitions - one per symptom, registered in the constructor via RegisterBranch
    // (#914). Each pair below builds the ordered step list plus a verdict function that reads the
    // *completed* steps' Status/ResultText/Evidence to produce a "most likely cause" summary once
    // the run finishes (or, for a manual/opt-in step, once that step finishes too - see
    // RunManualStepCommand).
    // ==================================================================================

    // ---------------------------- 902: My PC is slow right now ----------------------------

    private List<DiagnosticStep> BuildSlowPcSteps() => new()
    {
        new DiagnosticStep
        {
            Id = "slow.resources", Label = "CPU, RAM, and disk (last ~10 seconds)",
            Description = "Averages the shared performance sampler's last ~10 one-second samples, not an instantaneous reading.",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckResourceAverages(_performance.CpuHistory, _performance.RamHistory, _performance.DiskHistory)),
        },
        new DiagnosticStep
        {
            Id = "slow.offenders", Label = "Top CPU/RAM offenders (10s average)",
            Description = "The Processes tab's already-tracked 10-second CPU average, not an instantaneous spike.",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckTopOffenders(_processes.Processes.ToList())),
        },
        new DiagnosticStep
        {
            Id = "slow.disk", Label = "Disk queue length & latency",
            Description = "LogicalDisk\\Avg. Disk sec/Transfer and Current Disk Queue Length, already sampled by the shared sampler.",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckDiskLatency(_performance.DiskQueueLength, _performance.DiskReadLatencyMs, _performance.DiskWriteLatencyMs)),
        },
        new DiagnosticStep
        {
            Id = "slow.paging", Label = "Memory paging rate (thrashing check)",
            Description = "Memory\\Pages Input/sec, sampled fresh over about a second.",
            Timeout = TimeSpan.FromSeconds(6),
            Check = ct => TroubleshootService.CheckMemoryThrashingAsync(ct),
        },
        new DiagnosticStep
        {
            Id = "slow.maintenance", Label = "Background maintenance processes",
            Description = "Checks for Windows Defender scanning, servicing worker, and search indexing.",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckBackgroundMaintenanceProcesses(_processes.Processes.ToList())),
        },
    };

    private static string BuildSlowPcVerdict(TroubleshootRun run)
    {
        var disk = StepById(run, "slow.disk");
        var paging = StepById(run, "slow.paging");
        var offenders = StepById(run, "slow.offenders");
        var maintenance = StepById(run, "slow.maintenance");

        if (disk?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: the disk is the bottleneck right now (elevated queue length/latency). " + disk.ResultText;
        if (paging?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: memory pressure (the system is actively paging). " + paging.ResultText;
        if (offenders?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: a single process using most of the CPU. " + offenders.ResultText;
        if (maintenance?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: background maintenance work (Defender/servicing/indexing) is currently running. " + maintenance.ResultText;
        return "Nothing stood out as a clear cause over this sample window - the slowdown may be intermittent or tied to something not covered by this check. Try running this again while the slowness is actually happening.";
    }

    // ---------------------------- 903: It crashes or blue-screens ----------------------------

    private static List<DiagnosticStep> BuildCrashSteps()
    {
        var ctx = new TroubleshootService.CrashContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "crash.events", Label = "BugCheck (1001) & Kernel-Power (41) events",
                Description = "The System/Application log scan Windows' own Reliability Monitor is based on, last 30 days.",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => Task.FromResult(TroubleshootService.CheckCrashEvents(ctx)),
            },
            new DiagnosticStep
            {
                Id = "crash.minidumps", Label = "Minidump files",
                Description = "%SystemRoot%\\Minidump enumeration.",
                Timeout = TimeSpan.FromSeconds(10),
                Check = _ => Task.FromResult(TroubleshootService.CheckMinidumps(ctx)),
            },
            new DiagnosticStep
            {
                Id = "crash.reliability", Label = "Reliability Monitor records (surrounding week)",
                Description = "Win32_ReliabilityRecords entries within a week of the last crash.",
                Timeout = TimeSpan.FromSeconds(15),
                Check = _ => Task.FromResult(TroubleshootService.CheckReliabilityRecords(ctx)),
            },
            new DiagnosticStep
            {
                Id = "crash.whea", Label = "WHEA-Logger hardware-error events (17/18/19)",
                Description = "Windows Hardware Error Architecture events - a sign of actual hardware involvement.",
                Timeout = TimeSpan.FromSeconds(15),
                Check = _ => Task.FromResult(TroubleshootService.CheckWheaEvents()),
            },
            new DiagnosticStep
            {
                Id = "crash.drivers", Label = "Recently-installed drivers (pnputil)",
                Description = "Correlates drivers installed within 7 days before the first crash - a lead, not a verdict.",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => TroubleshootService.CheckRecentDriverCorrelationAsync(ctx),
            },
        };
    }

    private static string BuildCrashVerdict(TroubleshootRun run)
    {
        var events = StepById(run, "crash.events");
        var drivers = StepById(run, "crash.drivers");
        var whea = StepById(run, "crash.whea");

        if (events?.Status != DiagnosticStepStatus.Warning)
            return "No crash-like events (BugCheck 1001 / Kernel-Power 41) were found in the last 30 days.";
        if (drivers?.Status == DiagnosticStepStatus.Warning)
            return drivers.ResultText;
        if (whea?.Status == DiagnosticStepStatus.Warning)
            return "Lead, not a confirmed cause: hardware-error (WHEA) events were logged alongside the crashes - " + whea.ResultText;
        return "Crashes were found, but no clear recently-installed driver or hardware-error correlation stood out. Review the Reliability Monitor records and minidumps above for more detail.";
    }

    // ---------------------------- 904: It won't sleep / wakes on its own ----------------------------

    private static List<DiagnosticStep> BuildSleepSteps() => new()
    {
        new DiagnosticStep
        {
            Id = "sleep.requests", Label = "Active power requests (powercfg /requests)",
            Description = "Apps/drivers currently holding a power request open.",
            Timeout = TimeSpan.FromSeconds(20),
            Check = _ => TroubleshootService.CheckPowerRequestsAsync(),
        },
        new DiagnosticStep
        {
            Id = "sleep.waketimers", Label = "Active wake timers (powercfg /waketimers)",
            Timeout = TimeSpan.FromSeconds(20),
            Check = _ => TroubleshootService.CheckWakeTimersAsync(),
        },
        new DiagnosticStep
        {
            Id = "sleep.lastwake", Label = "Last wake source (powercfg /lastwake)",
            Timeout = TimeSpan.FromSeconds(20),
            Check = _ => TroubleshootService.CheckLastWakeAsync(),
        },
        new DiagnosticStep
        {
            Id = "sleep.study", Label = "Sleep study report (powercfg /sleepstudy)",
            Description = "Modern Standby's own diagnostic report - top DRIPS offenders and exit reasons, best-effort parsed.",
            Timeout = TimeSpan.FromSeconds(35),
            Check = _ => TroubleshootService.CheckSleepStudyAsync(),
        },
    };

    private static string BuildSleepVerdict(TroubleshootRun run)
    {
        var requests = StepById(run, "sleep.requests");
        var waketimers = StepById(run, "sleep.waketimers");
        var lastwake = StepById(run, "sleep.lastwake");
        var study = StepById(run, "sleep.study");

        if (requests?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: an active power request is keeping the system awake. " + requests.ResultText;
        if (waketimers?.Status == DiagnosticStepStatus.Warning)
            return "Most likely cause: an active wake timer is waking the system on its own. " + waketimers.ResultText;
        if (lastwake?.Status == DiagnosticStepStatus.Warning)
            return "The last wake source couldn't be identified by Windows - " + lastwake.ResultText;
        if (study?.Status == DiagnosticStepStatus.Warning)
            return study.ResultText;
        return "No obvious blocker was found in powercfg's own diagnostics - review the raw output for each step above.";
    }

    // ---------------------------- 905: No internet (layered) ----------------------------

    private static List<DiagnosticStep> BuildNoInternetSteps()
    {
        var ctx = new TroubleshootService.NetworkLayerContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "net.adapter", Label = "Adapter link state",
                Description = "Win32_NetworkAdapter.NetConnectionStatus - is any adapter actually Connected?",
                Timeout = TimeSpan.FromSeconds(10), StopOnFailure = true,
                Check = _ => Task.FromResult(TroubleshootService.CheckAdapterLinkState()),
            },
            new DiagnosticStep
            {
                Id = "net.address", Label = "DHCP / APIPA address check",
                Description = "A 169.254.x.x address means DHCP never answered.",
                Timeout = TimeSpan.FromSeconds(10), StopOnFailure = true,
                Check = _ => Task.FromResult(TroubleshootService.CheckApipaAddress()),
            },
            new DiagnosticStep
            {
                Id = "net.gateway", Label = "Default gateway ping",
                Timeout = TimeSpan.FromSeconds(10), StopOnFailure = true,
                Check = _ => TroubleshootService.CheckGatewayAsync(ctx),
            },
            new DiagnosticStep
            {
                Id = "net.dns", Label = "DNS resolution",
                Timeout = TimeSpan.FromSeconds(10), StopOnFailure = true,
                Check = _ => TroubleshootService.CheckDnsAsync(ctx),
            },
            new DiagnosticStep
            {
                Id = "net.tcp", Label = "Outbound TCP connection",
                Timeout = TimeSpan.FromSeconds(8), StopOnFailure = true,
                Check = ct => TroubleshootService.CheckOutboundTcpAsync(ct),
            },
        };
    }

    private static string BuildNoInternetVerdict(TroubleshootRun run)
    {
        var failed = run.Steps.FirstOrDefault(s => s.Status == DiagnosticStepStatus.Failed);
        if (failed is null)
            return "Internet connectivity looks fine end-to-end - adapter, address, gateway, DNS, and an outbound connection all checked out.";
        return $"Stopped at \"{failed.Label}\": {failed.ResultText}";
    }

    // ---------------------------- 906: Fans are loud / it runs hot ----------------------------

    private List<DiagnosticStep> BuildFansSteps() => new()
    {
        new DiagnosticStep
        {
            Id = "fans.correlation", Label = "Fan/temperature sensors vs. CPU load",
            Timeout = TimeSpan.FromSeconds(10),
            Check = _ => Task.FromResult(TroubleshootService.CheckFanTempCorrelation(_performance.CpuCurrentPercent)),
        },
        new DiagnosticStep
        {
            Id = "fans.minstate", Label = "Minimum processor state (active power plan)",
            Timeout = TimeSpan.FromSeconds(20),
            Check = _ => TroubleshootService.CheckMinProcessorStateAsync(),
        },
        new DiagnosticStep
        {
            Id = "fans.stuckclock", Label = "CPU stuck at a high performance state while idle",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckStuckHighPerformanceState(_performance.CpuCurrentPercent, _performance.CpuVsBasePercent)),
        },
        new DiagnosticStep
        {
            Id = "fans.pinning", Label = "A background process pinning a core",
            Timeout = TimeSpan.FromSeconds(5),
            Check = _ => Task.FromResult(TroubleshootService.CheckCorePinningProcess(_processes.Processes.ToList())),
        },
    };

    private static string BuildFansVerdict(TroubleshootRun run)
    {
        var correlation = StepById(run, "fans.correlation");
        var minState = StepById(run, "fans.minstate");
        var stuckClock = StepById(run, "fans.stuckclock");
        var pinning = StepById(run, "fans.pinning");

        bool hotWithoutLoad = correlation?.Status == DiagnosticStepStatus.Warning &&
            correlation.ResultText.Contains("hot without load", StringComparison.OrdinalIgnoreCase);
        bool hotBecauseBusy = correlation?.Status == DiagnosticStepStatus.Warning &&
            correlation.ResultText.Contains("hot because busy", StringComparison.OrdinalIgnoreCase);

        if (hotBecauseBusy)
        {
            string extra = pinning?.Status == DiagnosticStepStatus.Warning ? " " + pinning.ResultText : string.Empty;
            return "Verdict: hot because busy - fans/temperatures are tracking real CPU load." + extra;
        }
        if (hotWithoutLoad)
        {
            var reasons = new List<string>();
            if (minState?.Status == DiagnosticStepStatus.Warning) reasons.Add(minState.ResultText);
            if (stuckClock?.Status == DiagnosticStepStatus.Warning) reasons.Add(stuckClock.ResultText);
            if (pinning?.Status == DiagnosticStepStatus.Warning) reasons.Add(pinning.ResultText);
            return reasons.Count > 0
                ? "Verdict: hot without load. Possible contributor(s): " + string.Join(" ", reasons)
                : "Verdict: hot without load, and no specific power-plan/process contributor was found - worth checking dust/airflow or a stuck fan curve directly.";
        }
        return "Fan speeds and temperatures look normal right now - if the noise/heat is intermittent, try running this again while it's happening.";
    }

    // ---------------------------- 907: It boots or signs in slowly ----------------------------

    private static List<DiagnosticStep> BuildBootSteps()
    {
        var ctx = new TroubleshootService.BootShutdownContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "boot.events", Label = "Boot-performance degradation events (100-110)",
                Description = "Microsoft-Windows-Diagnostics-Performance/Operational - degraded boot, slow services, slow startup apps, slow group policy, slow profile load.",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => Task.FromResult(TroubleshootService.CheckBootDegradationEvents(ctx)),
            },
            new DiagnosticStep
            {
                Id = "boot.join", Label = "Match culprits to Startup/Services",
                Description = "Joins named culprits from the events above to the Startup tab's items and the Services tab's rows.",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => TroubleshootService.CheckBootCulpritJoinAsync(ctx),
            },
        };
    }

    private static string BuildBootVerdict(TroubleshootRun run)
    {
        var join = StepById(run, "boot.join");
        var events = StepById(run, "boot.events");

        if (join?.Status == DiagnosticStepStatus.Warning)
            return join.ResultText;
        if (events?.Status == DiagnosticStepStatus.Warning)
            return "Boot-degradation events were found, but no specific named delay could be confidently matched to a Startup/Services item. " + events.ResultText;
        return "No boot-performance degradation events were found in the last 30 days.";
    }

    // ---------------------------- 908: It takes forever to shut down ----------------------------

    private static List<DiagnosticStep> BuildShutdownSteps()
    {
        var ctx = new TroubleshootService.BootShutdownContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "shutdown.events", Label = "Shutdown-degradation events (200-208)",
                Description = "Microsoft-Windows-Diagnostics-Performance/Operational.",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => Task.FromResult(TroubleshootService.CheckShutdownDegradationEvents(ctx)),
            },
            new DiagnosticStep
            {
                Id = "shutdown.profile", Label = "User Profile Service events (1530/1534)",
                Description = "Registry handles left open at logoff can delay sign-out/shutdown.",
                Timeout = TimeSpan.FromSeconds(15),
                Check = _ => Task.FromResult(TroubleshootService.CheckProfileUnloadEvents()),
            },
            new DiagnosticStep
            {
                Id = "shutdown.timeout", Label = "WaitToKillServiceTimeout",
                Description = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\WaitToKillServiceTimeout.",
                Timeout = TimeSpan.FromSeconds(5),
                Check = _ => Task.FromResult(TroubleshootService.CheckWaitToKillServiceTimeout()),
            },
            new DiagnosticStep
            {
                Id = "shutdown.summary", Label = "Which step blocked shutdown the longest",
                Timeout = TimeSpan.FromSeconds(5),
                Check = _ => Task.FromResult(TroubleshootService.SummarizeShutdownCulprits(ctx)),
            },
        };
    }

    private static string BuildShutdownVerdict(TroubleshootRun run)
    {
        var summary = StepById(run, "shutdown.summary");
        var profile = StepById(run, "shutdown.profile");

        if (summary?.Status == DiagnosticStepStatus.Warning)
            return summary.ResultText;
        if (profile?.Status == DiagnosticStepStatus.Warning)
            return "No clear timed culprit from the degradation events, but profile-unload events were found. " + profile.ResultText;
        return "No shutdown-degradation or profile-unload events were found in the last 30 days.";
    }

    // ---------------------------- 909: Games or video stutter ----------------------------

    private static List<DiagnosticStep> BuildGamesSteps() => new()
    {
        new DiagnosticStep
        {
            Id = "games.dpc", Label = "DPC/interrupt time & context switches",
            Description = "Processor Information\\% DPC Time / % Interrupt Time and System\\Context Switches/sec, sampled fresh over about a second.",
            Timeout = TimeSpan.FromSeconds(6),
            Check = ct => TroubleshootService.CheckDpcInterruptTimeAsync(ct),
        },
        new DiagnosticStep
        {
            Id = "games.capture", Label = "Capture DPC/interrupt activity (wpr + tracerpt)",
            Description = "Opt-in ~10-second system trace to narrow down which driver is accumulating DPC/interrupt time. Heavier than the check above - only run this while it's actively stuttering.",
            Timeout = TimeSpan.FromSeconds(150),
            IsManual = true,
            Check = ct => TroubleshootService.CaptureDpcOffenderAsync(ct),
        },
    };

    private static string BuildGamesVerdict(TroubleshootRun run)
    {
        var capture = StepById(run, "games.capture");
        if (capture?.Status == DiagnosticStepStatus.Warning)
            return capture.ResultText;
        var dpc = StepById(run, "games.dpc");
        if (dpc?.Status == DiagnosticStepStatus.Warning)
            return dpc.ResultText + " Run the capture step above for a closer look.";
        return "DPC/interrupt time looks normal over this sample window - if it's stuttering right now, try running this again while it's happening, or use the capture step above.";
    }

    // ---------------------------- 910: Wi-Fi keeps dropping ----------------------------

    private static List<DiagnosticStep> BuildWifiSteps()
    {
        var ctx = new TroubleshootService.WifiContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "wifi.adapter", Label = "Wireless adapter presence",
                Timeout = TimeSpan.FromSeconds(10),
                Check = _ => TroubleshootService.CheckWirelessAdapterPresenceAsync(ctx),
            },
            new DiagnosticStep
            {
                Id = "wifi.report", Label = "WLAN report disconnect history",
                Description = "netsh wlan show wlanreport - the adapter's own disconnect-reason timeline.",
                Timeout = TimeSpan.FromSeconds(25),
                ShouldRun = run => StepById(run, "wifi.adapter")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => TroubleshootService.CheckWlanReportAsync(ctx),
            },
            new DiagnosticStep
            {
                Id = "wifi.events", Label = "WLAN-AutoConfig events (8000-8003/11000-series)",
                Timeout = TimeSpan.FromSeconds(15),
                ShouldRun = run => StepById(run, "wifi.adapter")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => Task.FromResult(TroubleshootService.CheckWlanAutoConfigEvents()),
            },
            new DiagnosticStep
            {
                Id = "wifi.power", Label = "Wireless adapter power-saving setting",
                Timeout = TimeSpan.FromSeconds(15),
                ShouldRun = run => StepById(run, "wifi.adapter")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => TroubleshootService.CheckWirelessPowerSavingAsync(),
            },
        };
    }

    private static string BuildWifiVerdict(TroubleshootRun run)
    {
        var adapter = StepById(run, "wifi.adapter");
        if (adapter?.Status != DiagnosticStepStatus.Passed)
            return "No wireless adapter was found on this system.";

        var report = StepById(run, "wifi.report");
        var events = StepById(run, "wifi.events");
        var power = StepById(run, "wifi.power");

        var parts = new List<string>();
        if (report?.Status == DiagnosticStepStatus.Warning) parts.Add(report.ResultText);
        if (events?.Status == DiagnosticStepStatus.Warning) parts.Add(events.ResultText);
        if (power?.Status == DiagnosticStepStatus.Warning) parts.Add(power.ResultText);

        return parts.Count > 0
            ? string.Join(" ", parts)
            : "No disconnect history, WLAN-AutoConfig events, or an aggressive power-saving setting stood out - if it's dropping intermittently, try running this again after it happens.";
    }

    // ---------------------------- 911: My disk sits at 100% ----------------------------

    private List<DiagnosticStep> BuildDiskSteps()
    {
        var ctx = new TroubleshootService.DiskBottleneckContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "disk.latency", Label = "Disk latency vs. throughput",
                Description = "LogicalDisk\\Avg. Disk sec/Transfer vs. Disk Bytes/sec, sampled fresh over about a second.",
                Timeout = TimeSpan.FromSeconds(6),
                Check = ct => TroubleshootService.CheckDiskLatencyVsThroughputAsync(ct),
            },
            new DiagnosticStep
            {
                Id = "disk.process", Label = "Top disk I/O process",
                Description = "Reuses the already-sampled Processes list's per-process disk I/O figures.",
                Timeout = TimeSpan.FromSeconds(5),
                Check = _ => Task.FromResult(TroubleshootService.CheckTopDiskIoProcess(_processes.Processes.ToList())),
            },
            new DiagnosticStep
            {
                Id = "disk.mediatype", Label = "System drive media type (HDD/SSD)",
                Timeout = TimeSpan.FromSeconds(10),
                Check = _ => Task.FromResult(TroubleshootService.CheckSystemDriveMediaType(ctx)),
            },
            new DiagnosticStep
            {
                Id = "disk.fragmentation", Label = "Fragmentation analysis (HDD only)",
                Description = "An analyze-only defrag.exe pass across the whole volume - opt-in, since even an analyze pass can take a while.",
                Timeout = TimeSpan.FromSeconds(130),
                IsManual = true,
                Check = _ => TroubleshootService.CheckFragmentationManualAsync(ctx),
            },
            new DiagnosticStep
            {
                Id = "disk.background", Label = "Background indexing/prefetch (SysMain/Windows Search)",
                Timeout = TimeSpan.FromSeconds(5),
                Check = _ => Task.FromResult(TroubleshootService.CheckBackgroundWriter(_processes.Processes.ToList())),
            },
        };
    }

    private static string BuildDiskVerdict(TroubleshootRun run)
    {
        var process = StepById(run, "disk.process");
        var fragmentation = StepById(run, "disk.fragmentation");
        var background = StepById(run, "disk.background");
        var latency = StepById(run, "disk.latency");

        if (process?.Status == DiagnosticStepStatus.Warning)
            return "Verdict: a specific process is driving disk I/O. " + process.ResultText;
        if (fragmentation?.Status == DiagnosticStepStatus.Warning)
            return "Verdict: HDD fragmentation. " + fragmentation.ResultText;
        if (background?.Status == DiagnosticStepStatus.Warning)
            return "Verdict: background indexing/prefetching. " + background.ResultText;
        if (latency?.Status == DiagnosticStepStatus.Warning)
            return latency.ResultText + " Run the fragmentation step above if this is a spinning disk.";
        return "Disk activity looks normal right now - if it's pegged at 100% currently, try running this again while it's happening.";
    }

    // ---------------------------- 912: Battery dies too fast ----------------------------

    private static List<DiagnosticStep> BuildBatterySteps()
    {
        var ctx = new TroubleshootService.BatteryContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "battery.presence", Label = "Battery presence",
                Timeout = TimeSpan.FromSeconds(10),
                Check = _ => Task.FromResult(TroubleshootService.CheckBatteryPresence(ctx)),
            },
            new DiagnosticStep
            {
                Id = "battery.report", Label = "Battery report (powercfg /batteryreport)",
                Description = "Design vs. full-charge capacity, and recent usage.",
                Timeout = TimeSpan.FromSeconds(25),
                ShouldRun = run => StepById(run, "battery.presence")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => TroubleshootService.CheckBatteryReportAsync(),
            },
            new DiagnosticStep
            {
                Id = "battery.srum", Label = "Per-app energy estimate (powercfg /srumutil)",
                Description = "Less universally present than the battery report - degrades gracefully if unavailable on this Windows build.",
                Timeout = TimeSpan.FromSeconds(30),
                ShouldRun = run => StepById(run, "battery.presence")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => TroubleshootService.CheckSrumEnergyAsync(),
            },
            new DiagnosticStep
            {
                Id = "battery.plan", Label = "Active power plan",
                Timeout = TimeSpan.FromSeconds(15),
                ShouldRun = run => StepById(run, "battery.presence")?.Status == DiagnosticStepStatus.Passed,
                Check = _ => TroubleshootService.CheckActivePowerPlanForBatteryAsync(),
            },
        };
    }

    private static string BuildBatteryVerdict(TroubleshootRun run)
    {
        var presence = StepById(run, "battery.presence");
        if (presence?.Status != DiagnosticStepStatus.Passed)
            return "No battery detected - this looks like a desktop, so battery life isn't applicable.";

        var report = StepById(run, "battery.report");
        var srum = StepById(run, "battery.srum");
        var plan = StepById(run, "battery.plan");

        var parts = new List<string>();
        if (report is not null && report.Status is DiagnosticStepStatus.Warning or DiagnosticStepStatus.Passed && report.ResultText.Length > 0)
            parts.Add(report.ResultText);
        if (srum?.Status == DiagnosticStepStatus.Warning) parts.Add(srum.ResultText);
        if (plan?.Status == DiagnosticStepStatus.Warning) parts.Add(plan.ResultText);

        return parts.Count > 0
            ? string.Join(" ", parts)
            : "Battery health and active power plan both look normal - nothing obvious stood out.";
    }

    // ---------------------------- 913: A device keeps disconnecting or isn't recognized ----------------------------

    private static List<DiagnosticStep> BuildDeviceSteps()
    {
        var ctx = new TroubleshootService.PnpProblemContext();
        return new List<DiagnosticStep>
        {
            new DiagnosticStep
            {
                Id = "device.pnp", Label = "Devices reporting a problem (Device Manager)",
                Description = "Win32_PnPEntity entries with a nonzero ConfigManagerErrorCode.",
                Timeout = TimeSpan.FromSeconds(15),
                Check = _ => Task.FromResult(TroubleshootService.CheckPnpProblemDevices(ctx)),
            },
            new DiagnosticStep
            {
                Id = "device.events", Label = "Kernel-PnP/USBHUB3 events (last 7 days)",
                Timeout = TimeSpan.FromSeconds(20),
                Check = _ => Task.FromResult(TroubleshootService.CheckDeviceDisconnectEvents(ctx)),
            },
            new DiagnosticStep
            {
                Id = "device.power", Label = "USB root hub power setting",
                Description = "Whether \"allow the computer to turn off this device to save power\" is enabled on any USB root hub.",
                Timeout = TimeSpan.FromSeconds(10),
                Check = _ => Task.FromResult(TroubleshootService.CheckUsbRootHubPowerSetting()),
            },
        };
    }

    private static string BuildDeviceVerdict(TroubleshootRun run)
    {
        var pnp = StepById(run, "device.pnp");
        var events = StepById(run, "device.events");
        var power = StepById(run, "device.power");

        if (pnp?.Status == DiagnosticStepStatus.Warning)
        {
            string extra = events?.Status == DiagnosticStepStatus.Warning ? " " + events.ResultText : string.Empty;
            string powerNote = power?.Status == DiagnosticStepStatus.Warning ? " " + power.ResultText : string.Empty;
            return pnp.ResultText + extra + powerNote;
        }
        if (power?.Status == DiagnosticStepStatus.Warning)
            return "No device currently reports a Device Manager problem, but " + power.ResultText;
        return "No device currently reports a problem in Device Manager, and USB root hub power settings look fine.";
    }

    public void Dispose()
    {
        Baselines.Dispose();
        BackgroundHealth.Dispose();
    }
}
