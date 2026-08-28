using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Backs the Responsiveness tab's hung-window grid (#235/#236/#243/#244) plus the foreground-stall
/// recorder (#238). Window enumeration/hang detection has no WMI/tool equivalent, so raw P/Invoke
/// here is the documented exception to CLAUDE.md's "prefer a known tool" rule (same tier as
/// ForegroundContextService/HandleInspectionService).
///
/// #235: EnumWindows + IsWindowVisible + GetWindowThreadProcessId + IsHungAppWindow - the exact API
/// Explorer/real Task Manager use to draw "(Not Responding)", but per-window rather than
/// per-process (unlike ProcessRow.NotRespondingSeconds). IsHungAppWindow itself doesn't block, so
/// SampleWindows runs directly on whatever thread calls it (the light tick's UI thread).
///
/// #236: SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG|SMTO_BLOCK, 250ms) against each window -
/// genuinely blocking-ish, so RunProbeCycleAsync always runs inside Task.Run on its own slower
/// cadence (the ResponsivenessViewModel drives that timer), never on the UI thread.
///
/// #238: a SetWinEventHook(EVENT_SYSTEM_FOREGROUND) hook tracks the current foreground app; the
/// #236 probe loop attributes any stall over StallThresholdMs to whichever app was foreground at
/// the time. The hook delegate is kept alive as an instance field (_winEventDelegate) - a
/// WinEventDelegate with no surviving managed reference is a well-known WPF/WinForms P/Invoke
/// gotcha: the GC can collect the delegate (and the native thunk backing it) while the hook is
/// still installed, crashing the process the next time Windows calls back into it.
///
/// #243/#244: best-effort wait-reason hint and cross-process chain guess for a currently-hung
/// window - see DescribeWaitState/ResolveHangChain. Both explicitly "quick flag, not a verdict":
/// a wait reason isn't a full stack trace, and the chain guess is a kernel-object-sharing match,
/// not a confirmed deadlock analysis.
/// </summary>
public sealed class HungWindowService : IDisposable
{
    private const uint WM_NULL = 0x0000;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int ProbeTimeoutMs = 250;

    // #237: hang-start times for currently-hung windows, keyed by hwnd - read/written from both the
    // light-tick UI thread (SampleWindows) and the probe loop's background thread (RunProbeCycleAsync),
    // so this needs to be genuinely thread-safe, not just "usually fine".
    private readonly ConcurrentDictionary<IntPtr, DateTime> _hangStart = new();

    // #236: last known probe response time per window - written by the background probe loop, read
    // by the UI-thread light tick.
    private readonly ConcurrentDictionary<IntPtr, double> _lastResponseMs = new();

    // #244: cached chain-resolution text per window, resolved at most once per hang instance (see
    // RunProbeCycleAsync) rather than re-walking the handle table every probe cycle.
    private readonly ConcurrentDictionary<IntPtr, string?> _chainCache = new();

    // #238: ranked per-app stall accumulation - a plain Dictionary behind a lock rather than a
    // ConcurrentDictionary since RecordStall/GetRankedStalls both need to touch several fields of
    // the same row atomically.
    private readonly Dictionary<string, ForegroundStallRow> _stallsByApp = new(StringComparer.OrdinalIgnoreCase);

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _hookHandle = IntPtr.Zero;
    private volatile int _foregroundPid;
    private volatile string _foregroundProcessName = string.Empty;

    /// <summary>#238: default 500ms per the task spec - configurable so a user on a slower machine
    /// can raise it past their normal baseline response time.</summary>
    public double StallThresholdMs { get; set; } = 500;

    /// <summary>Internal (not private) so ShellResponsivenessService (#246) can reuse the same
    /// top-level-window enumeration, filtered down to explorer.exe's own frames, instead of
    /// re-deriving the EnumWindows/GetWindowThreadProcessId walk.</summary>
    internal readonly record struct RawWindow(IntPtr Hwnd, int Pid, int ThreadId, string Title, bool Visible, bool IsHung);

    public void StartForegroundHook()
    {
        if (_hookHandle != IntPtr.Zero) return;
        try
        {
            _winEventDelegate = OnWinEvent;
            _hookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        catch
        {
            // Best-effort - #238's per-app attribution just won't have a foreground app to
            // attribute to; the rest of this service still works.
        }
    }

    public void StopForegroundHook()
    {
        if (_hookHandle == IntPtr.Zero) return;
        try { UnhookWinEvent(_hookHandle); } catch { /* ignore */ }
        _hookHandle = IntPtr.Zero;
        _winEventDelegate = null; // safe to release once unhooked - no more callbacks will arrive
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out int pid);
            _foregroundPid = pid;
            _foregroundProcessName = TryGetProcessName(pid) ?? string.Empty;
        }
        catch { /* best-effort */ }
    }

    /// <summary>#235/#237: the always-on scan - enumerates top-level windows, flags hung ones, and
    /// reports any window that was hung last call but has now recovered (#237's hang-log source).</summary>
    public (List<HungWindowRow> Windows, List<HangLogEntry> Recovered) SampleWindows()
    {
        var raw = EnumerateRaw();
        var now = DateTime.Now;
        var rows = new List<HungWindowRow>(raw.Count);
        var stillHung = new HashSet<IntPtr>();

        foreach (var w in raw)
        {
            TimeSpan? hungFor = null;
            string? waitHint = null;
            string? chain = null;

            if (w.IsHung)
            {
                stillHung.Add(w.Hwnd);
                var start = _hangStart.GetOrAdd(w.Hwnd, now);
                hungFor = now - start;

                // #243: a single system-wide process/thread snapshot - the same cost class
                // ProcessMonitorService already pays every tick for every process, so this is cheap
                // enough to compute inline here for the (typically 0-2) currently-hung windows.
                waitHint = DescribeWaitState(w.Pid, w.ThreadId);

                // #244: NOT cheap (a handle-table walk) - only ever resolved off-thread by
                // RunProbeCycleAsync below, cached here by hwnd.
                _chainCache.TryGetValue(w.Hwnd, out chain);
            }

            _lastResponseMs.TryGetValue(w.Hwnd, out var respMs);

            rows.Add(new HungWindowRow
            {
                Hwnd = w.Hwnd,
                Pid = w.Pid,
                ThreadId = w.ThreadId,
                ProcessName = TryGetProcessName(w.Pid) ?? "(exited)",
                WindowTitle = w.Title,
                IsHung = w.IsHung,
                ResponseMs = _lastResponseMs.ContainsKey(w.Hwnd) ? respMs : null,
                HungFor = hungFor,
                WaitHintText = waitHint,
                ChainText = chain,
            });
        }

        // Any window this service was tracking as hung, but that didn't show up hung (or at all)
        // this pass, has recovered - hand it back so the caller can append it to the hang log.
        var recovered = new List<HangLogEntry>();
        foreach (var hwnd in _hangStart.Keys)
        {
            if (stillHung.Contains(hwnd)) continue;
            if (!_hangStart.TryRemove(hwnd, out var start)) continue;
            _chainCache.TryRemove(hwnd, out _);

            var match = raw.FirstOrDefault(w => w.Hwnd == hwnd);
            recovered.Add(new HangLogEntry
            {
                AppName = TryGetProcessName(match.Pid) ?? "(unknown)",
                WindowTitle = match.Title ?? string.Empty,
                StartTime = start,
                DurationSeconds = Math.Max(0, (now - start).TotalSeconds),
            });
        }

        return (rows, recovered);
    }

    /// <summary>#236/#238/#244: the slower, background-only cycle - probes every visible top-level
    /// window's message pump and records the round-trip time, attributes stalls over
    /// StallThresholdMs to the foreground app (#238), and resolves at most one still-hung window's
    /// cross-process chain per cycle (#244). Always call this from inside Task.Run/a background
    /// timer - never the UI thread.</summary>
    public Task RunProbeCycleAsync(System.Threading.CancellationToken ct) => Task.Run(() =>
    {
        var raw = EnumerateRaw();
        int fgPidSnapshot = _foregroundPid;
        string fgNameSnapshot = _foregroundProcessName;

        foreach (var w in raw)
        {
            if (ct.IsCancellationRequested) break;
            if (w.Hwnd == IntPtr.Zero || !IsWindow(w.Hwnd)) continue;

            double ms = ProbeWindowMs(w.Hwnd);
            _lastResponseMs[w.Hwnd] = ms;

            if (ms >= StallThresholdMs && fgPidSnapshot != 0 && w.Pid == fgPidSnapshot && !string.IsNullOrEmpty(fgNameSnapshot))
                RecordStall(fgNameSnapshot, ms);
        }

        // #244: resolve at most one still-hung, not-yet-resolved window's chain per cycle - capped
        // so a burst of simultaneously-hung windows can't multiply this loop's cost.
        var pending = raw.FirstOrDefault(w => w.IsHung && _hangStart.ContainsKey(w.Hwnd) && !_chainCache.ContainsKey(w.Hwnd));
        if (pending.Hwnd != IntPtr.Zero)
        {
            string appName = TryGetProcessName(pending.Pid) ?? pending.Title;
            _chainCache[pending.Hwnd] = ResolveHangChain(pending.Pid, appName);
        }

        // Prune response-time entries for windows that no longer exist - otherwise this dictionary
        // would grow unbounded over a long-running session as windows come and go (_hangStart/
        // _chainCache are already pruned as each hang recovers, in SampleWindows above).
        var liveHwnds = new HashSet<IntPtr>(raw.Select(w => w.Hwnd));
        foreach (var key in _lastResponseMs.Keys)
            if (!liveHwnds.Contains(key)) _lastResponseMs.TryRemove(key, out _);
    }, ct);

    /// <summary>#236: one window's message-pump round-trip time, capped at ProbeTimeoutMs -
    /// internal (not private) so ShellResponsivenessService (#246) can reuse the exact same
    /// probe logic/cadence against just Shell_TrayWnd/Progman/explorer's own frames, rather than
    /// re-deriving the SendMessageTimeout call. Always call this from a background thread - it's
    /// the genuinely blocking-ish part of this whole service.</summary>
    internal static double ProbeWindowMs(IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        IntPtr result = SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, ProbeTimeoutMs, out _);
        sw.Stop();

        // A zero return means SendMessageTimeout itself gave up (SMTO_ABORTIFHUNG kicked in) - treat
        // that the same as hitting the cap, since the window didn't answer in time either way.
        return result == IntPtr.Zero ? ProbeTimeoutMs : Math.Min(sw.Elapsed.TotalMilliseconds, ProbeTimeoutMs);
    }

    private void RecordStall(string appName, double ms)
    {
        lock (_stallsByApp)
        {
            if (!_stallsByApp.TryGetValue(appName, out var row))
                _stallsByApp[appName] = row = new ForegroundStallRow { ProcessName = appName };
            row.StallCount++;
            row.TotalStallMs += ms;
            row.MaxStallMs = Math.Max(row.MaxStallMs, ms);
            row.LastStall = DateTime.Now;
        }
    }

    /// <summary>#238: per-app ranked stall history, worst offenders first.</summary>
    public List<ForegroundStallRow> GetRankedStalls()
    {
        lock (_stallsByApp)
        {
            return _stallsByApp.Values
                .OrderByDescending(r => r.StallCount)
                .ThenByDescending(r => r.MaxStallMs)
                .ToList();
        }
    }

    /// <summary>#243: plain-English decode of the window's owning thread's ThreadState/WaitReason/
    /// StartAddress - explicitly a quick flag, not a full stack trace.</summary>
    private static string? DescribeWaitState(int pid, int threadId)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread t in proc.Threads)
            {
                if (t.Id != threadId) continue;

                string stateText = t.ThreadState switch
                {
                    System.Diagnostics.ThreadState.Running => "running",
                    System.Diagnostics.ThreadState.Ready => "ready to run, waiting for a CPU core",
                    System.Diagnostics.ThreadState.Standby => "about to run (standby)",
                    System.Diagnostics.ThreadState.Terminated => "terminated",
                    System.Diagnostics.ThreadState.Initialized => "initializing",
                    System.Diagnostics.ThreadState.Transition => "in transition, waiting on a resource other than the CPU",
                    System.Diagnostics.ThreadState.Wait => DescribeWaitReason(SafeWaitReason(t)),
                    _ => "in an unknown state",
                };
                string startAddr = "0x" + t.StartAddress.ToInt64().ToString("X");
                return $"Owning thread (tid {threadId}) is {stateText} — start address {startAddr}.";
            }
        }
        catch
        {
            // Process exited, access denied, or the thread already went away - no hint available.
        }
        return null;
    }

    private static ThreadWaitReason? SafeWaitReason(ProcessThread t)
    {
        try { return t.WaitReason; } catch { return null; }
    }

    private static string DescribeWaitReason(ThreadWaitReason? reason) => reason switch
    {
        ThreadWaitReason.LpcReceive => "waiting on WrLpcReceive — blocked receiving an ALPC/RPC reply from another process",
        ThreadWaitReason.LpcReply => "waiting on WrLpcReply — blocked on another process replying to an ALPC/RPC call",
        ThreadWaitReason.UserRequest => "waiting on WrUserRequest — blocked on a user-mode lock/event it's waiting on",
        ThreadWaitReason.Executive => "waiting on WrExecutive — blocked on a general kernel object (event, semaphore, timer, ...)",
        ThreadWaitReason.EventPairHigh or ThreadWaitReason.EventPairLow => "waiting on an event pair — a classic Win32-subsystem call pattern",
        ThreadWaitReason.FreePage or ThreadWaitReason.PageIn or ThreadWaitReason.PageOut => "waiting on the memory manager (paging activity)",
        ThreadWaitReason.SystemAllocation => "waiting on a system memory allocation",
        ThreadWaitReason.ExecutionDelay => "sleeping (a timed delay)",
        ThreadWaitReason.Suspended => "suspended — not scheduled to run",
        ThreadWaitReason.VirtualMemory => "waiting on a virtual-memory operation",
        _ => "waiting (reason unknown)",
    };

    /// <summary>#244: best-effort "X is waiting on Y" guess - see
    /// HandleInspectionService.FindHandleSharers's remarks for the underlying technique, and
    /// FileLockLookupService for the independent Restart-Manager cross-check used for a File match.</summary>
    private static string? ResolveHangChain(int pid, string appName)
    {
        try
        {
            var matches = HandleInspectionService.FindHandleSharers(pid);
            var best = matches.FirstOrDefault(m => m.Pid > 4 && m.Pid != pid);
            if (best is null) return null;

            string otherName = TryGetProcessName(best.Pid) is { } n ? $"{n} (pid {best.Pid})" : $"pid {best.Pid}";

            if (best.ObjectType == "File" && !string.IsNullOrEmpty(best.ObjectName))
            {
                var owners = FileLockLookupService.FindProcessesWithFileOpen(best.ObjectName);
                var rmOwner = owners.FirstOrDefault(o => o.Pid != pid);
                if (rmOwner is not null) otherName = $"{rmOwner.AppName} (pid {rmOwner.Pid})";
            }

            string reason = best.ObjectType switch
            {
                "ALPC Port" => "sharing an ALPC/RPC port",
                "Mutant" => "sharing a named mutex",
                "Section" => "sharing a memory-mapped section",
                "File" => "sharing a file handle",
                _ => $"sharing a {best.ObjectType} handle",
            };
            return $"{appName} is waiting on {otherName} — {reason}. Quick flag, not a confirmed deadlock.";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    internal static List<RawWindow> EnumerateRaw()
    {
        var list = new List<RawWindow>();
        try
        {
            bool Callback(IntPtr hwnd, IntPtr lParam)
            {
                try
                {
                    bool visible = IsWindowVisible(hwnd);
                    int len = GetWindowTextLength(hwnd);
                    string title = string.Empty;
                    if (len > 0)
                    {
                        var sb = new StringBuilder(len + 1);
                        GetWindowText(hwnd, sb, sb.Capacity);
                        title = sb.ToString();
                    }

                    bool isHung = IsHungAppWindow(hwnd);
                    // #235: "hidden message-only stalls are visible" - but an invisible, untitled
                    // window that ISN'T hung is just noise (there are dozens of these on any live
                    // desktop), so it's only included when it's actually flagged.
                    if (!visible && string.IsNullOrEmpty(title) && !isHung) return true;

                    uint tid = GetWindowThreadProcessId(hwnd, out int pid);
                    list.Add(new RawWindow(hwnd, pid, (int)tid, title, visible, isHung));
                }
                catch { /* one bad window shouldn't stop enumeration */ }
                return true;
            }

            EnumWindows(Callback, IntPtr.Zero);
        }
        catch
        {
            // EnumWindows itself failed - degrade to an empty scan, same as every other on-demand
            // enumeration in this app.
        }
        return list;
    }

    public void Dispose() => StopForegroundHook();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
