using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Per-process open-handle count broken out by object type (#12 - "File", "Key" (registry),
/// "Event", "Section", "Mutant", "Semaphore", "Thread", ...), the closest honest equivalent this
/// app can offer to Task Manager's own handle count without a much larger undertaking. Windows has
/// no per-type handle count API - the only way to get one is to walk the *system-wide* handle table
/// (NtQuerySystemInformation, SystemHandleInformation) and filter down to the target pid, then for
/// each of its handles duplicate it into this process and ask NtQueryObject what type it is.
///
/// This is the same fragile territory the "what has this file open" feature (#9) deliberately
/// avoided by using the documented Restart Manager API instead - here there's no equivalent
/// documented shortcut, so it's done directly but defensively: NtQueryObject is well known to
/// occasionally hang forever on certain handle types (a named pipe with no listener is the classic
/// case), so every single query runs on its own short-lived background thread that this method
/// abandons - never joins past a strict per-handle timeout - rather than risk the whole app UI
/// thread (or even this background Task) hanging. The handle count processed is also capped, since
/// a busy system service can hold tens of thousands of handles and this is an on-demand,
/// button-triggered inspector, not something that needs full coverage. A handle whose type can't be
/// resolved within the timeout is grouped under "(unresolved)" - a real, expected outcome, not a bug.
/// </summary>
public static class HandleInspectionService
{
    private const int MaxHandlesToResolve = 400;
    private static readonly TimeSpan PerHandleTimeout = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(6);

    public static List<(string TypeName, int Count)> ReadHandleTypeCounts(int pid)
    {
        var counts = new Dictionary<string, int>();
        IntPtr sourceProcess = IntPtr.Zero;
        try
        {
            var entries = ReadSystemHandles().Where(h => h.UniqueProcessId == pid).Take(MaxHandlesToResolve * 2).ToList();
            if (entries.Count == 0) return new List<(string, int)>();

            sourceProcess = OpenProcess(ProcessDupHandle, false, pid);
            if (sourceProcess == IntPtr.Zero)
                return new List<(string, int)> { ("(couldn't open process to inspect handles - access denied)", entries.Count) };

            var overallDeadline = DateTime.UtcNow + OverallTimeout;
            int resolved = 0;
            foreach (var entry in entries)
            {
                if (resolved >= MaxHandlesToResolve || DateTime.UtcNow > overallDeadline) break;
                resolved++;

                string typeName = ResolveHandleType(sourceProcess, entry.HandleValue);
                counts[typeName] = counts.TryGetValue(typeName, out var c) ? c + 1 : 1;
            }

            if (entries.Count > resolved)
                counts["(not scanned - handle count exceeds the inspection cap)"] = entries.Count - resolved;
        }
        catch
        {
            // Best-effort - an empty/partial result is fine, never let this crash the caller.
        }
        finally
        {
            if (sourceProcess != IntPtr.Zero) CloseHandle(sourceProcess);
        }

        return counts.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Duplicates one handle and asks its type name, off the calling thread with a strict
    /// timeout - see the class remarks for why NtQueryObject can't be trusted to always return.</summary>
    private static string ResolveHandleType(IntPtr sourceProcess, ushort handleValue)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int dupStatus = NtDuplicateObject(sourceProcess, (IntPtr)handleValue, GetCurrentProcess(),
                out dup, 0, 0, DuplicateSameAccess);
            if (dupStatus != 0 || dup == IntPtr.Zero) return "(unresolved)";
        }
        catch
        {
            return "(unresolved)";
        }

        string? result = null;
        var worker = new Thread(() =>
        {
            try
            {
                int size = 0x1000;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryObject(dup, ObjectTypeInformation, buffer, size, out _);
                    if (status == 0)
                    {
                        // OBJECT_TYPE_INFORMATION starts with a UNICODE_STRING (Length, MaximumLength, Buffer).
                        ushort length = (ushort)Marshal.ReadInt16(buffer, 0);
                        IntPtr strPtr = Marshal.ReadIntPtr(buffer, 8);
                        result = length > 0 && strPtr != IntPtr.Zero
                            ? Marshal.PtrToStringUni(strPtr, length / 2)
                            : null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { /* leave result null */ }
        })
        { IsBackground = true };

        worker.Start();
        bool finished = worker.Join(PerHandleTimeout);

        // Only close our duplicate if the query actually returned - closing a handle mid-hung-call
        // could itself hang or corrupt state. A duplicate leaked because the worker never returned
        // is a small, bounded cost (capped handle count above) versus the alternative of blocking
        // the UI.
        if (finished)
        {
            try { CloseHandle(dup); } catch { /* ignore */ }
            return string.IsNullOrWhiteSpace(result) ? "(unresolved)" : result!;
        }
        return "(unresolved - query timed out)";
    }

    private static List<SYSTEM_HANDLE_TABLE_ENTRY_INFO> ReadSystemHandles()
    {
        int size = 1 << 20; // 1 MB starting guess, grown below if needed
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemHandleInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO>();

                int count = Marshal.ReadInt32(buffer); // ULONG HandleCount (top bytes zero on x64 ULONG)
                var list = new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO>(count);
                int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO>();
                IntPtr entryPtr = IntPtr.Add(buffer, 8); // ULONG HandleCount + 4 bytes padding before the array on x64
                for (int i = 0; i < count; i++)
                    list.Add(Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO>(IntPtr.Add(entryPtr, i * entrySize)));
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO>();
    }

    private const int SystemHandleInformation = 16;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO
    {
        public ushort UniqueProcessId;
        public ushort CreatorBackTraceIndex;
        public byte ObjectTypeIndex;
        public byte HandleAttributes;
        public ushort HandleValue;
        public IntPtr Object;
        public uint GrantedAccess;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr objectInformation, int objectInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtDuplicateObject(IntPtr sourceProcessHandle, IntPtr sourceHandle, IntPtr targetProcessHandle,
        out IntPtr targetHandle, uint desiredAccess, uint attributes, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
