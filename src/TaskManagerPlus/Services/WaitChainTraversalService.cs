using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #271: Wait Chain Traversal ("Analyze Wait Chain") - the exact in-box API Resource Monitor's own
/// "Analyze Wait Chain" right-click action uses, via advapi32.dll's OpenThreadWaitChainSession/
/// GetThreadWaitChain/CloseThreadWaitChainSession. No Win32/WMI equivalent exists for walking a live
/// blocking chain across critical sections, mutexes, ALPC, COM and SMB - same "documented exception
/// to prefer-a-known-tool" tier as SchedulerService/HandleInspectionService.
///
/// WAITCHAIN_NODE_INFO is a native struct with two overlapping unions (LockObject/ThreadObject)
/// starting at the same offset - the classic C++-union interop problem. Modeling it as a marshaled
/// C# struct is a long-documented .NET interop trap (Microsoft's own "Bugslayer: Wait Chain
/// Traversal", MSDN Magazine, July 2007): a WCHAR[128] name field mixed with LayoutKind.Explicit
/// throws a TypeLoadException the moment a reference-type array field overlaps another field, and
/// the only historical workaround needs `unsafe` fixed buffers, which this project doesn't use
/// anywhere else. Instead, this reads the raw 280-byte-per-node buffer directly via
/// Marshal.ReadInt32/PtrToStringUni at fixed offsets - the same "skip the marshaled struct, decode
/// raw offsets by hand" technique HandleInspectionService already uses for OBJECT_TYPE_INFORMATION's
/// UNICODE_STRING. Every offset and the 280-byte total size below is Microsoft's own documented
/// layout (confirmed via WinDBG `dt -v -r 1` against the real type in the article above - 280 bytes
/// identically on 32-bit and 64-bit Windows):
///
///   0x000 ObjectType   (WCT_OBJECT_TYPE, 4 bytes)
///   0x004 ObjectStatus (WCT_OBJECT_STATUS, 4 bytes)
///   0x008 union start:
///     LockObject.ObjectName    WCHAR[128]      (256 bytes, 0x008-0x108)
///     LockObject.Timeout       LARGE_INTEGER   (8 bytes,   0x108-0x110) - "not implemented in v1"
///     LockObject.Alertable     BOOL            (4 bytes,   0x110-0x114) - per Microsoft's own header
///     ThreadObject.ProcessId       DWORD (0x008), ThreadId DWORD (0x00C)
///     ThreadObject.WaitTime         DWORD (0x010), ContextSwitches DWORD (0x014)
///
/// Always call Analyze via Task.Run from a UI-triggered action, never synchronously on the
/// dispatcher thread - GetThreadWaitChain can itself take a moment to walk a large chain. Every
/// failure (access denied, thread already exited, API unavailable pre-Vista) degrades to a
/// "couldn't analyze" result, never a fabricated chain.
/// </summary>
public static class WaitChainTraversalService
{
    private const int NodeSize = 280; // sizeof(WAITCHAIN_NODE_INFO) - see class remarks.
    private const int WctMaxNodeCount = 16; // WCT_MAX_NODE_COUNT, Microsoft's own documented cap.

    // Cross-process visibility flags for GetThreadWaitChain - without these, WCT only reports
    // chains inside the target thread's own process, missing exactly the cross-process blocking
    // (COM, ALPC, critical sections shared via DuplicateHandle) that makes WCT worth using here.
    private const int WctOutOfProcComFlag = 0x1;
    private const int WctOutOfProcCsFlag = 0x2;
    private const int WctOutOfProcFlag = 0x4;
    private const int WctGetInfoAllFlags = WctOutOfProcComFlag | WctOutOfProcCsFlag | WctOutOfProcFlag;

    // WCT_OBJECT_TYPE
    private const int WctCriticalSectionType = 1;
    private const int WctSendMessageType = 2;
    private const int WctMutexType = 3;
    private const int WctAlpcType = 4;
    private const int WctComType = 5;
    private const int WctThreadWaitType = 6;
    private const int WctProcessWaitType = 7;
    private const int WctThreadType = 8;
    private const int WctComActivationType = 9;
    private const int WctSocketIoType = 11;
    private const int WctSmbIoType = 12;

    // WCT_OBJECT_STATUS
    private const int WctStatusNoAccess = 1;
    private const int WctStatusRunning = 2;
    private const int WctStatusBlocked = 3;
    private const int WctStatusPidOnly = 4;
    private const int WctStatusPidOnlyRpcss = 5;
    private const int WctStatusOwned = 6;
    private const int WctStatusNotOwned = 7;
    private const int WctStatusAbandoned = 8;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(int flags, IntPtr callback);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetThreadWaitChain(IntPtr wctSessionHandle, IntPtr context, int flags,
        int threadId, ref int nodeCount, IntPtr nodeInfoArray, out bool isCycle);

    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr wctSessionHandle);

    /// <summary>Synchronous, and can take a moment on a large/cross-process chain - always invoke
    /// via Task.Run from the caller (see class remarks), never on the UI dispatcher thread.</summary>
    public static WaitChainTraversalResult Analyze(int pid, int threadId)
    {
        if (threadId <= 0)
            return Fail("No thread ID was available to analyze - the process may have already exited.");

        IntPtr session = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            session = OpenThreadWaitChainSession(0 /* WCT_SYNC_OPEN_FLAG */, IntPtr.Zero);
            if (session == IntPtr.Zero)
                return Fail("Couldn't open a Wait Chain Traversal session (unsupported Windows version, or access denied).");

            int nodeCount = WctMaxNodeCount;
            buffer = Marshal.AllocHGlobal(NodeSize * WctMaxNodeCount);

            bool ok = GetThreadWaitChain(session, IntPtr.Zero, WctGetInfoAllFlags, threadId, ref nodeCount, buffer, out bool isCycle);
            if (!ok)
                return Fail($"Couldn't analyze thread {threadId} (pid {pid}) - it may have already exited, or access was denied.");
            if (nodeCount <= 0)
                return Fail("No wait chain was returned - this thread doesn't appear to be blocked right now.");

            var nodes = new List<WaitChainNodeRow>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                IntPtr nodePtr = IntPtr.Add(buffer, i * NodeSize);
                int objectType = Marshal.ReadInt32(nodePtr, 0x0);
                int objectStatus = Marshal.ReadInt32(nodePtr, 0x4);
                nodes.Add(DescribeNode(nodePtr, objectType, objectStatus, i));
            }

            return new WaitChainTraversalResult
            {
                Success = true,
                IsDeadlock = isCycle,
                Nodes = nodes,
                StatusText = isCycle
                    ? $"Deadlock detected - this chain forms a cycle ({nodeCount} node(s))."
                    : $"Wait chain resolved - {nodeCount} node(s), no cycle detected.",
            };
        }
        catch (Exception ex)
        {
            return Fail($"Wait chain analysis failed: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (session != IntPtr.Zero) CloseThreadWaitChainSession(session);
        }
    }

    private static WaitChainNodeRow DescribeNode(IntPtr nodePtr, int objectType, int objectStatus, int indentLevel)
    {
        string statusText = ObjectStatusText(objectStatus);

        if (objectType == WctThreadType)
        {
            int procId = Marshal.ReadInt32(nodePtr, 0x8);
            int tid = Marshal.ReadInt32(nodePtr, 0xC);
            int waitTimeMs = Marshal.ReadInt32(nodePtr, 0x10);
            int contextSwitches = Marshal.ReadInt32(nodePtr, 0x14);
            string procName = ProcessNameLookup.TryGetProcessName(procId) is { } n ? $"{n} (pid {procId})" : $"pid {procId}";
            return new WaitChainNodeRow
            {
                IndentLevel = indentLevel,
                IsThreadNode = true,
                Description = $"{procName}, thread {tid} — {statusText}, waited {waitTimeMs} ms, {contextSwitches} context switch(es)",
            };
        }

        // Lock-object node - the name lives in the WCHAR[128] at offset 0x8. Timeout/Alertable are
        // documented "not implemented in v1" by Microsoft's own header, so they're not read here.
        string? name = null;
        try
        {
            name = Marshal.PtrToStringUni(IntPtr.Add(nodePtr, 0x8), 128)?.TrimEnd('\0');
        }
        catch
        {
            // A raw/garbled name just falls back to the bare object-type text below - best-effort.
        }

        string typeText = ObjectTypeText(objectType);
        string label = string.IsNullOrWhiteSpace(name) ? typeText : $"{typeText}: {name}";
        return new WaitChainNodeRow { IndentLevel = indentLevel, IsThreadNode = false, Description = $"{label} ({statusText})" };
    }

    private static string ObjectTypeText(int t) => t switch
    {
        WctCriticalSectionType => "Critical section",
        WctSendMessageType => "SendMessage",
        WctMutexType => "Mutex",
        WctAlpcType => "ALPC port",
        WctComType => "COM call",
        WctThreadWaitType => "Thread wait",
        WctProcessWaitType => "Process wait",
        WctComActivationType => "COM activation",
        WctSocketIoType => "Socket I/O",
        WctSmbIoType => "SMB I/O",
        _ => "Unknown object",
    };

    private static string ObjectStatusText(int s) => s switch
    {
        WctStatusNoAccess => "access denied",
        WctStatusRunning => "running",
        WctStatusBlocked => "blocked",
        WctStatusPidOnly or WctStatusPidOnlyRpcss => "process ID only (thread not resolved)",
        WctStatusOwned => "owned",
        WctStatusNotOwned => "not owned",
        WctStatusAbandoned => "abandoned (owning thread exited without releasing it)",
        _ => "status unknown",
    };

    private static WaitChainTraversalResult Fail(string message) => new() { Success = false, IsDeadlock = false, StatusText = message };
}
