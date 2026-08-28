using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// One row in the Processes grid. Implements INotifyPropertyChanged so the
/// monitor service can update values in place each tick instead of rebuilding
/// (and flickering / losing selection on) the whole collection.
/// </summary>
public sealed class ProcessRow : ObservableObject
{
    public int Pid { get; init; }

    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private double _cpuPercent;
    public double CpuPercent { get => _cpuPercent; set => SetProperty(ref _cpuPercent, value); }

    /// <summary>Rolling ~10-sample average of CpuPercent (#11) - "what's actually been eating
    /// CPU over the last several seconds", steadier than the instantaneous per-tick reading a
    /// bursty process can otherwise hide behind. See ProcessMonitorService.ComputeCpuAverage.</summary>
    private double _cpuPercent10sAvg;
    public double CpuPercent10sAvg { get => _cpuPercent10sAvg; set => SetProperty(ref _cpuPercent10sAvg, value); }

    private long _memoryBytes;
    public long MemoryBytes
    {
        get => _memoryBytes;
        set
        {
            if (SetProperty(ref _memoryBytes, value))
                OnPropertyChanged(nameof(MemoryMb));
        }
    }

    private double _diskBytesPerSec;
    public double DiskBytesPerSec { get => _diskBytesPerSec; set => SetProperty(ref _diskBytesPerSec, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>Seconds this process has continuously reported "Not responding" (window ghosting),
    /// 0 while responding (#2) - "make it more prominent with duration" on top of Task Manager's
    /// own plain Not-responding flag. See ProcessMonitorService for how this is tracked.</summary>
    private int _notRespondingSeconds;
    public int NotRespondingSeconds { get => _notRespondingSeconds; set => SetProperty(ref _notRespondingSeconds, value); }

    private string _user = string.Empty;
    public string User { get => _user; set => SetProperty(ref _user, value); }

    private int _threadCount;
    public int ThreadCount { get => _threadCount; set => SetProperty(ref _threadCount, value); }

    private DateTime? _startTime;
    public DateTime? StartTime { get => _startTime; set => SetProperty(ref _startTime, value); }

    private string? _filePath;
    public string? FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }

    private int _handleCount;
    public int HandleCount { get => _handleCount; set => SetProperty(ref _handleCount, value); }

    private string? _commandLine;
    public string? CommandLine { get => _commandLine; set => SetProperty(ref _commandLine, value); }

    /// <summary>"Signed", "Unsigned", or "Unknown" (couldn't be determined - e.g. file locked,
    /// no access, or the process has already exited). See ProcessMonitorService.GetSignatureStatusCached.</summary>
    private string _signatureStatus = "Unknown";
    public string SignatureStatus { get => _signatureStatus; set => SetProperty(ref _signatureStatus, value); }

    /// <summary>#837: signing certificate's subject CN (falling back to issuer CN, then
    /// "Unknown") - see SignatureCheckService.GetSignerInfo. Populated from the same cached
    /// per-path lookup SignatureStatus already triggers, so this costs nothing extra.</summary>
    private string _publisher = "Unknown";
    public string Publisher { get => _publisher; set => SetProperty(ref _publisher, value); }

    /// <summary>#837: true when the signing certificate's subject and issuer are the same (a
    /// self-signed cert) - worth calling out since a self-signed "Microsoft"-looking publisher
    /// name would otherwise look legitimate at a glance.</summary>
    private bool _isSelfSigned;
    public bool IsSelfSigned { get => _isSelfSigned; set => SetProperty(ref _isSelfSigned, value); }

    /// <summary>#840: set only for the handful of well-known Windows system process names
    /// (svchost, lsass, csrss, ...) when the running image is somewhere other than
    /// System32/SysWOW64 or isn't Microsoft-signed, OR for a near-miss name that looks like a
    /// typo-squat of one of those names (e.g. "scvhost", "svch0st") - see
    /// ProcessTrustService.EvaluateProcessTrust. Null means "nothing to flag" (not evaluated, or
    /// evaluated and clean). "Quick flag, not a verdict" - see ProcessTrustService's remarks.</summary>
    private string? _trustWarning;
    public string? TrustWarning { get => _trustWarning; set => SetProperty(ref _trustWarning, value); }

    /// <summary>True when User is SYSTEM/LOCAL SERVICE/NETWORK SERVICE/an Administrators-group
    /// account - i.e. running with more privilege than an ordinary signed-in user, worth calling
    /// out when auditing for something unexpected among running processes.</summary>
    private bool _isHighPrivilege;
    public bool IsHighPrivilege { get => _isHighPrivilege; set => SetProperty(ref _isHighPrivilege, value); }

    /// <summary>Parent process ID (Win32_Process.ParentProcessId, cached per-pid) and its
    /// resolved name (0/empty when unknown, "(exited)" when the parent no longer exists) - see
    /// ProcessMonitorService.Sample for how the name is resolved from the same batch.</summary>
    private int _parentPid;
    public int ParentPid { get => _parentPid; set => SetProperty(ref _parentPid, value); }

    private string _parentName = string.Empty;
    public string ParentName { get => _parentName; set => SetProperty(ref _parentName, value); }

    /// <summary>True when working-set memory has grown steadily, without ever giving memory
    /// back, over the whole tracked history window - see ProcessMonitorService.ComputeLeakSuspect
    /// for the exact rule and its limitations (a heuristic flag, not a diagnosis).</summary>
    private bool _isLeakSuspect;
    public bool IsLeakSuspect { get => _isLeakSuspect; set => SetProperty(ref _isLeakSuspect, value); }

    /// <summary>Per-process GPU engine utilization, summed across every "GPU Engine" perf-counter
    /// instance for this pid (#36) - see ProcessMonitorService.ReadGpuUsageByPid.</summary>
    private double _gpuPercent;
    public double GpuPercent { get => _gpuPercent; set => SetProperty(ref _gpuPercent, value); }

    /// <summary>Round 7 #2: a best-effort "spawned together" grouping - see
    /// ProcessMonitorService.ComputeSpawnGroups for the exact heuristic (same parent pid + same
    /// executable name + start times clustered within a short window). 1 (or 0 for an unresolved
    /// parent) means "not part of a detected group"; a plain "quick flag, not a verdict" proxy for
    /// actual job-object membership, which Windows exposes no per-process query for.</summary>
    private int _spawnGroupSize;
    public int SpawnGroupSize { get => _spawnGroupSize; set => SetProperty(ref _spawnGroupSize, value); }

    /// <summary>Round 7 #11: how many currently-running processes share this exact executable path,
    /// and whether that count is unusually high (a runaway-launcher-bug smell) - see
    /// ProcessMonitorService.ComputeDuplicateInstances.</summary>
    private int _duplicateInstanceCount;
    public int DuplicateInstanceCount { get => _duplicateInstanceCount; set => SetProperty(ref _duplicateInstanceCount, value); }

    private bool _isDuplicateInstanceOutlier;
    public bool IsDuplicateInstanceOutlier { get => _isDuplicateInstanceOutlier; set => SetProperty(ref _isDuplicateInstanceOutlier, value); }

    /// <summary>Round 7 #7: GDI/USER object handle counts, matching Task Manager's optional Details
    /// columns - see ProcessControlService.ReadGuiResourceCounts (GetGuiResources).</summary>
    private int _gdiHandleCount;
    public int GdiHandleCount { get => _gdiHandleCount; set => SetProperty(ref _gdiHandleCount, value); }

    private int _userHandleCount;
    public int UserHandleCount { get => _userHandleCount; set => SetProperty(ref _userHandleCount, value); }

    /// <summary>Round 7 #6: scheduling priority class (Process.PriorityClass), shown as a column and
    /// changeable via a right-click submenu - see ProcessControlService.SetPriority.</summary>
    private string _priorityClassName = string.Empty;
    public string PriorityClassName { get => _priorityClassName; set => SetProperty(ref _priorityClassName, value); }

    /// <summary>Round 7 #8: true when every thread in the process is currently parked in a
    /// Suspended wait state - see ProcessControlService.IsSuspended for the exact check and its
    /// limitation (no single "process state" flag exists on Windows, this infers it from threads).</summary>
    private bool _isSuspended;
    public bool IsSuspended { get => _isSuspended; set => SetProperty(ref _isSuspended, value); }

    /// <summary>Round 8 #38: private bytes (Process.PrivateMemorySize64) and virtual size
    /// (Process.VirtualMemorySize64), shown alongside MemoryBytes (working set) above - three
    /// genuinely distinct figures, all already exposed by .NET with no extra interop. Modern
    /// Windows doesn't expose a separate "commit charge" for a process beyond private bytes (Task
    /// Manager's own "Commit size" column reads the same underlying figure), so virtual size fills
    /// the third slot instead of a redundant duplicate.</summary>
    private long _privateBytes;
    public long PrivateBytes { get => _privateBytes; set => SetProperty(ref _privateBytes, value); }

    private long _virtualBytes;
    public long VirtualBytes { get => _virtualBytes; set => SetProperty(ref _virtualBytes, value); }

    /// <summary>Round 8 #39: per-process kernel pool usage (Process.NonpagedSystemMemorySize64/
    /// PagedSystemMemorySize64 - already exposed by .NET, no native interop needed) - feeds the
    /// Memory tab's "Top kernel-pool consumers" card.</summary>
    private long _nonpagedPoolBytes;
    public long NonpagedPoolBytes { get => _nonpagedPoolBytes; set => SetProperty(ref _nonpagedPoolBytes, value); }

    private long _pagedPoolBytes;
    public long PagedPoolBytes { get => _pagedPoolBytes; set => SetProperty(ref _pagedPoolBytes, value); }

    public double MemoryMb => MemoryBytes / 1024.0 / 1024.0;

    /// <summary>Round 15, #852(a): integrity level (Untrusted/Low/Medium/Medium+/High/System/
    /// Protected/Unknown) - see ProcessTokenInspectionService.ReadIntegrityLevel. Cheap enough
    /// (one OpenProcessToken + one GetTokenInformation call) to read every tick.</summary>
    private string _integrityLevel = "Unknown";
    public string IntegrityLevel { get => _integrityLevel; set => SetProperty(ref _integrityLevel, value); }

    /// <summary>Round 15, #852(c): TokenIsAppContainer - true for a sandboxed AppContainer process
    /// (most UWP/Store apps, some browser renderer processes) - see
    /// ProcessTokenInspectionService.ReadIsAppContainer.</summary>
    private bool _isAppContainer;
    public bool IsAppContainer { get => _isAppContainer; set => SetProperty(ref _isAppContainer, value); }

    /// <summary>Round 15, #852(b): protection level (None/PPL/Protected, plus a signer subtype like
    /// "PPL (Antimalware)") via NtQueryInformationProcess's PS_PROTECTION byte - see
    /// ProcessTokenInspectionService.ReadProtectionLevel. A single native call, cheap enough for a
    /// per-tick column - see that class's remarks on why the direct PS_PROTECTION read was chosen
    /// over the access-denied-heuristic proxy #852 offers as a fallback.</summary>
    private string _protectionLevel = "Unknown";
    public string ProtectionLevel { get => _protectionLevel; set => SetProperty(ref _protectionLevel, value); }

    /// <summary>Round 16, #854/#855/#856: combined "quick flag" reason text from three cheap
    /// per-tick heuristics - suspicious image location (#854), parent-child anomaly rules (#855),
    /// and living-off-the-land command-line patterns (#856) - all computed in
    /// ProcessMonitorService.Sample from data it already collects (FilePath/ParentName/ParentPid/
    /// IntegrityLevel/CommandLine), with no new syscalls. Null means nothing to flag; more than one
    /// applying is joined with "; ". Rendered with the same "Check this" badge pattern TrustWarning
    /// above already established. "Quick flag, not a verdict" - see each heuristic service's own
    /// remarks.</summary>
    private string? _securityFlagReason;
    public string? SecurityFlagReason { get => _securityFlagReason; set => SetProperty(ref _securityFlagReason, value); }

    /// <summary>Round 17, item 60: how many Application Error (1000) / Application Hang (1002)
    /// events this executable has produced in the last 30 days, from CrashHistoryCacheService's
    /// cheap in-memory cache (never a fresh event-log query on this tick - see that service's own
    /// remarks). 0 both for "genuinely no crashes found" and for "the cache hasn't been built yet"
    /// - CLAUDE.md's "degrade to 0, never fabricate" default, same as every other best-effort
    /// count in this app.</summary>
    private int _crashCount30d;
    public int CrashCount30d { get => _crashCount30d; set => SetProperty(ref _crashCount30d, value); }

    /// <summary>Item 67: measured UI responsiveness in milliseconds - SendMessageTimeout(WM_NULL,
    /// SMTO_ABORTIFHUNG) against this process's main window, turning the binary "Not responding"
    /// status into an actual number so a sluggish-but-not-yet-hung app shows up before Windows
    /// itself would ever flag it. Null (not "0") whenever no measurement is available: the process
    /// has no window, ProcessesViewModel.MeasureResponseTime is off (the default - see its own
    /// remarks on why this is opt-in), or the probe itself timed out (SMTO_ABORTIFHUNG aborted -
    /// effectively "at least this slow", not a real duration). See
    /// ProcessControlService.MeasureUiResponseTimeMs.</summary>
    private int? _responseTimeMs;
    public int? ResponseTimeMs { get => _responseTimeMs; set => SetProperty(ref _responseTimeMs, value); }
}
