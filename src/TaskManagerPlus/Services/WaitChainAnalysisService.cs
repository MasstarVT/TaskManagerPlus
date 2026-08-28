using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Item 64: wraps the Wait Chain Traversal API (advapi32.dll) - exactly what Resource Monitor's
/// own "Analyze Wait Chain" feature uses to answer "which thread is this thread blocked on, and
/// who's holding it", flagging a cycle as a genuine deadlock. No documented tool wraps this
/// (unlike most other native calls in this app - see CLAUDE.md's "prefer a known tool/API" note),
/// so this is exactly the "no tool/WMI class exists" exception that note calls out.
///
/// Manually parses WAITCHAIN_NODE_INFO's raw bytes at fixed offsets (Marshal.ReadInt32/
/// PtrToStringUni) rather than a marshaled struct array - the native struct's C union (a lock-
/// object name in one branch, a thread/process id pair in the other) can't cleanly round-trip
/// through .NET's [StructLayout(Explicit)] marshaling once one union member is a string, the same
/// reason HandleInspectionService.ResolveHandleType manually parses OBJECT_TYPE_INFORMATION
/// instead of a full marshaled struct.
///
/// GetThreadWaitChain walks live OS wait state on another process's threads - like NtQueryObject
/// (see HandleInspectionService's own remarks), it's not guaranteed to return promptly on a
/// sufficiently wedged system, so every call runs on its own abandoned background thread with a
/// strict timeout rather than risk hanging this on-demand, button-triggered analysis.
/// </summary>
public static class WaitChainAnalysisService
{
    private const int NodeSize = 280; // sizeof(WAITCHAIN_NODE_INFO) on x64: two 4-byte enums, then
                                       // the union (256-byte WCHAR[128] name + 8-byte LARGE_INTEGER
                                       // timeout + 4-byte BOOL, padded to the union's own 8-byte
                                       // alignment) - see DescribeNode for the per-field offsets.
    private const int WctObjNameLength = 128; // WCHAR count, not bytes.
    private const int WctMaxNodeCount = 16; // Documented cap on a single GetThreadWaitChain call.
    private const int MaxThreadsToAnalyze = 32; // A hung GUI process rarely has more relevant
                                                 // threads than this; caps the worst case (every
                                                 // thread's own call timing out) to a bounded total.

    private static readonly TimeSpan PerThreadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Analyses every thread of the given process, one GetThreadWaitChain call per
    /// thread. Safe to call from a background thread (ProcessesViewModel does, via Task.Run) -
    /// each individual call is itself bounded (PerThreadTimeout), but the whole method can still
    /// take a few seconds for a process with many threads.</summary>
    public static WaitChainResult Analyze(int pid)
    {
        var result = new WaitChainResult();

        List<int> threadIds;
        try
        {
            using var proc = Process.GetProcessById(pid);
            threadIds = proc.Threads.Cast<ProcessThread>().Select(t => t.Id).Take(MaxThreadsToAnalyze).ToList();
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Couldn't enumerate this process' threads: {ex.Message}";
            return result;
        }

        if (threadIds.Count == 0)
        {
            result.ErrorMessage = "No threads found - the process may have already exited.";
            return result;
        }

        foreach (int tid in threadIds)
        {
            var chain = AnalyzeOneThread(tid);
            if (chain is not null) result.Chains.Add(chain);
        }

        if (result.Chains.Count == 0)
        {
            result.ErrorMessage = "The Wait Chain Traversal API returned no data for any thread of " +
                "this process (it may not currently be blocked on anything the API tracks, the API " +
                "may be unavailable, or every call timed out).";
        }

        return result;
    }

    private static WaitChainThreadResult? AnalyzeOneThread(int threadId)
    {
        WaitChainThreadResult? outcome = null;

        var worker = new Thread(() =>
        {
            IntPtr session = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                session = OpenThreadWaitChainSession(0, IntPtr.Zero);
                if (session == IntPtr.Zero) return; // API unavailable / denied - leave outcome null

                buffer = Marshal.AllocHGlobal(NodeSize * WctMaxNodeCount);
                int nodeCount = WctMaxNodeCount;
                bool isCycle = false;
                bool ok = GetThreadWaitChain(session, IntPtr.Zero, 0, (uint)threadId, ref nodeCount, buffer, ref isCycle);
                if (!ok || nodeCount <= 0) return; // this thread isn't in a chain the API tracks

                var nodes = new List<string>(nodeCount);
                for (int i = 0; i < nodeCount; i++)
                    nodes.Add(DescribeNode(IntPtr.Add(buffer, i * NodeSize)));

                outcome = new WaitChainThreadResult { ThreadId = threadId, IsDeadlockCycle = isCycle, Nodes = nodes };
            }
            catch
            {
                // Best-effort - leave outcome null rather than let a native-call failure escape
                // past this abandoned thread.
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (session != IntPtr.Zero) { try { CloseThreadWaitChainSession(session); } catch { /* ignore */ } }
            }
        })
        { IsBackground = true };

        worker.Start();
        // Never Join() past the timeout - a duplicate/abandoned worker thread here is a small,
        // bounded cost (this call is on-demand, not per-tick) versus the alternative of blocking
        // the caller on a native call that's known to occasionally never return.
        worker.Join(PerThreadTimeout);
        return outcome;
    }

    /// <summary>Formats one WAITCHAIN_NODE_INFO entry - WctThreadType (a running thread, the usual
    /// end of a chain) reads the ThreadObject union member (PID/TID); every other object type
    /// (CriticalSection/Mutex/Alpc/Com/...) reads the LockObject union member's own name, the same
    /// branch Microsoft's own sample code for this API uses.</summary>
    private static string DescribeNode(IntPtr entry)
    {
        int objectType = Marshal.ReadInt32(entry, 0);
        int objectStatus = Marshal.ReadInt32(entry, 4);
        string typeName = ObjectTypeName(objectType);
        string statusName = ObjectStatusName(objectStatus);

        if (objectType == WctThreadType)
        {
            int threadProcessId = Marshal.ReadInt32(entry, 8);
            int threadThreadId = Marshal.ReadInt32(entry, 12);
            return $"{typeName} - PID {threadProcessId}, TID {threadThreadId} ({statusName})";
        }

        string name;
        try
        {
            name = Marshal.PtrToStringUni(IntPtr.Add(entry, 8), WctObjNameLength)?.TrimEnd('\0') ?? string.Empty;
        }
        catch
        {
            name = string.Empty;
        }

        return string.IsNullOrWhiteSpace(name)
            ? $"{typeName} ({statusName})"
            : $"{typeName} \"{name}\" ({statusName})";
    }

    private const int WctThreadType = 7;

    private static string ObjectTypeName(int type) => type switch
    {
        0 => "Execution context",
        1 => "Spinlock",
        2 => "Mutex",
        3 => "ALPC",
        4 => "COM",
        5 => "Thread wait",
        6 => "Process wait",
        7 => "Thread",
        8 => "COM activation",
        10 => "Socket I/O",
        11 => "SMB I/O",
        _ => "Unknown wait object",
    };

    private static string ObjectStatusName(int status) => status switch
    {
        1 => "no access",
        2 => "running",
        3 => "blocked",
        4 => "PID only",
        5 => "PID only (RPCSS)",
        6 => "owned",
        7 => "not owned",
        8 => "abandoned",
        9 => "unknown",
        10 => "error",
        _ => "unknown",
    };

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(int flags, IntPtr callback);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr wctHandle, IntPtr context, int flags, uint threadId,
        ref int nodeCount, IntPtr nodeInfoArray, [MarshalAs(UnmanagedType.Bool)] ref bool isCycle);

    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr wctHandle);
}
