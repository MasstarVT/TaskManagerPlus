using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #901: backs the Troubleshoot tab - a symptom-picker landing grid plus, once a symptom is
/// selected, a scripted <see cref="TroubleshootRun"/> of ordered <see cref="DiagnosticStep"/>s run
/// against existing services (EventLogService, NetworkDiagnosticsService, PowerPlanService,
/// SensorMonitorService, ...) instead of making a user guess which of the other twelve tabs to
/// open first. Takes the shared <see cref="PerformanceViewModel"/> and <see cref="ProcessesViewModel"/>
/// (the same instances MainViewModel already composes for the CPU/Memory/Storage/Network/Processes
/// tabs) so the "My PC is slow" branch reads already-polled live data instead of re-sampling from
/// scratch - see CLAUDE.md's shared-sampler note.
///
/// Every step is run sequentially and raced against its own <see cref="DiagnosticStep.Timeout"/>
/// via Task.WhenAny + a per-step CancellationTokenSource, so one hung WMI/tool call can never
/// freeze the rest of a run - the same "never let one call hang the UI forever" rule
/// SensorMonitorService/HandleInspectionService already document for their own background work.
///
/// This round wires up 902-908 (7 branches: slow PC, crashes, won't sleep, no internet, loud
/// fans/hot, slow boot/sign-in, slow shutdown). "Games stutter" and "Battery dies fast" are shown
/// on the landing grid (per #901's spec) but marked unavailable - a later round wires their
/// branches up the same way these seven were built, reusing this same DiagnosticStep/TroubleshootRun
/// engine rather than a new one.
/// </summary>
public sealed class TroubleshootViewModel : ObservableObject
{
    private readonly PerformanceViewModel _performance;
    private readonly ProcessesViewModel _processes;
    private bool _isRunning;

    public ObservableCollection<SymptomCard> Symptoms { get; } = new();

    private TroubleshootRun? _selectedRun;
    public TroubleshootRun? SelectedRun { get => _selectedRun; private set => SetProperty(ref _selectedRun, value); }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    public RelayCommand RunSymptomCommand { get; }
    public RelayCommand BackToSymptomsCommand { get; }

    public TroubleshootViewModel(PerformanceViewModel performance, ProcessesViewModel processes)
    {
        _performance = performance;
        _processes = processes;

        Symptoms.Add(new SymptomCard { Id = "slow", Title = "My PC is slow right now", Description = "Checks CPU/RAM/disk load, top offenders, and background maintenance work." });
        Symptoms.Add(new SymptomCard { Id = "crash", Title = "It crashes or blue-screens", Description = "Checks crash events, minidumps, reliability records, hardware errors, and recent driver installs." });
        Symptoms.Add(new SymptomCard { Id = "sleep", Title = "It won't go to sleep / wakes on its own", Description = "Checks active power requests, wake timers, last wake source, and the sleep study report." });
        Symptoms.Add(new SymptomCard { Id = "no-internet", Title = "No internet", Description = "Walks the network stack layer by layer: adapter, address, gateway, DNS, then a real connection." });
        Symptoms.Add(new SymptomCard { Id = "fans", Title = "Fans are loud / it runs hot", Description = "Correlates fan/temperature sensors against CPU load and the active power plan." });
        Symptoms.Add(new SymptomCard { Id = "boot", Title = "It boots or signs in slowly", Description = "Reads Windows' own boot-performance diagnostics and matches culprits to Startup/Services." });
        Symptoms.Add(new SymptomCard { Id = "shutdown", Title = "It takes forever to shut down", Description = "Reads shutdown-degradation and profile-unload events, and the service kill timeout." });
        Symptoms.Add(new SymptomCard { Id = "games", Title = "Games stutter", Description = "Not available yet.", IsAvailable = false });
        Symptoms.Add(new SymptomCard { Id = "battery", Title = "Battery dies fast", Description = "Not available yet.", IsAvailable = false });

        RunSymptomCommand = new RelayCommand(
            id => { if (id is string sid) _ = RunSymptomAsync(sid); },
            id => id is string sid && !IsRunning && Symptoms.Any(s => s.Id == sid && s.IsAvailable));
        BackToSymptomsCommand = new RelayCommand(_ => SelectedRun = null, _ => !IsRunning);
    }

    private async Task RunSymptomAsync(string symptomId)
    {
        if (IsRunning) return;
        var card = Symptoms.FirstOrDefault(s => s.Id == symptomId);
        if (card is null || !card.IsAvailable) return;

        var (steps, buildVerdict) = BuildBranch(symptomId);
        var run = new TroubleshootRun { SymptomId = symptomId, DisplayName = card.Title };
        foreach (var step in steps) run.Steps.Add(step);

        SelectedRun = run;
        IsRunning = true;
        try
        {
            foreach (var step in run.Steps)
            {
                step.Status = DiagnosticStepStatus.Running;

                using var cts = new CancellationTokenSource();
                Task<DiagnosticStepResult> checkTask;
                try
                {
                    checkTask = step.Check(cts.Token);
                }
                catch (Exception ex)
                {
                    // A check that throws synchronously (shouldn't happen given every
                    // TroubleshootService method wraps its own body, but a defensive catch here
                    // means a bug in one check still can't take the whole run down).
                    ApplyResult(step, DiagnosticStepResult.Fail($"Check threw an unexpected error: {ex.Message}"));
                    if (step.StopOnFailure) { SkipRemaining(run, step); break; }
                    continue;
                }

                var delayTask = Task.Delay(step.Timeout, cts.Token);
                var winner = await Task.WhenAny(checkTask, delayTask);
                cts.Cancel(); // release whichever of the two is still pending

                if (winner != checkTask)
                {
                    step.Status = DiagnosticStepStatus.TimedOut;
                    step.ResultText = $"Timed out after {step.Timeout.TotalSeconds:0.#}s.";
                    // Don't await the abandoned check - just make sure a later fault on it can't
                    // surface as an unobserved task exception.
                    _ = checkTask.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    if (step.StopOnFailure) { SkipRemaining(run, step); break; }
                    continue;
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

                if (step.StopOnFailure && step.Status == DiagnosticStepStatus.Failed)
                {
                    SkipRemaining(run, step);
                    break;
                }
            }

            run.VerdictText = SafeBuildVerdict(buildVerdict, run);
        }
        finally
        {
            run.IsRunning = false;
            run.FinishedAt = DateTime.Now;
            IsRunning = false;
        }
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
    // Branch definitions - one per symptom. Each returns its ordered step list plus a
    // verdict function that reads the *completed* steps' Status/ResultText/Evidence to
    // produce a "most likely cause" summary once the run finishes.
    // ==================================================================================

    private (List<DiagnosticStep> Steps, Func<TroubleshootRun, string> Verdict) BuildBranch(string symptomId) => symptomId switch
    {
        "slow" => (BuildSlowPcSteps(), BuildSlowPcVerdict),
        "crash" => (BuildCrashSteps(), BuildCrashVerdict),
        "sleep" => (BuildSleepSteps(), BuildSleepVerdict),
        "no-internet" => (BuildNoInternetSteps(), BuildNoInternetVerdict),
        "fans" => (BuildFansSteps(), BuildFansVerdict),
        "boot" => (BuildBootSteps(), BuildBootVerdict),
        "shutdown" => (BuildShutdownSteps(), BuildShutdownVerdict),
        _ => (new List<DiagnosticStep>(), _ => "This symptom isn't available yet."),
    };

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
}
