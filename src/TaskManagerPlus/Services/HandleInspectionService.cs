using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Per-process open-handle count broken out by object type (#12 - "File", "Key" (registry),
/// "Event", "Section", "Mutant", "Semaphore", "Thread", ...), the closest honest equivalent this
/// app can offer to Task Manager's own handle count without a much larger undertaking. Windows has
/// no per-type handle count API - the only way to get one is to walk the *system-wide* handle table
/// (NtQuerySystemInformation, SystemExtendedHandleInformation) and filter down to the target pid,
/// then for each of its handles duplicate it into this process and ask NtQueryObject what type it is.
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

    // #244: object types worth walking for a cross-process hang chain - the common cross-process
    // blocking primitives (an ALPC port under an RPC/COM call, a named mutex, a shared-memory
    // section, or a shared file handle). Deliberately not every type NtQueryObject can return -
    // walking Event/Semaphore/Thread/Key handles too would multiply the cost of an already
    // expensive scan for little extra signal, since those are rarely what's actually blocking a
    // hung UI thread.
    private static readonly string[] HangChainTypesOfInterest = { "ALPC Port", "Mutant", "Section", "File" };

    /// <summary>One other process found to be holding a handle to the same kernel object as the
    /// target process (#244) - the same "compare the native OBJECT pointer across processes'
    /// handle-table entries" technique Process Explorer/Handle.exe use to answer "who else has this
    /// open", built entirely on the system handle walk this file already does for
    /// ReadHandleTypeCounts. A real, if best-effort, cross-process pointer, not a guess: two
    /// processes' SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX.Object values only match when they reference the
    /// literal same kernel object.</summary>
    public sealed record HandleShareMatch(int Pid, string ObjectType, string? ObjectName);

    /// <summary>
    /// #244: finds other processes sharing an ALPC port/mutex/section/file handle with <paramref
    /// name="pid"/> - the "cross-process hang chain" signal HungWindowService.ResolveHangChain
    /// builds its "X is waiting on Y" guess from. Reuses ReadSystemHandles/ResolveHandleType's exact
    /// pattern (including the abandoned-worker-thread timeout on every NtQueryObject call - see the
    /// class remarks) rather than re-deriving the handle-walk. Capped and best-effort like
    /// ReadHandleTypeCounts: a handle whose type can't be resolved in time is skipped, not treated
    /// as a match.
    /// </summary>
    public static List<HandleShareMatch> FindHandleSharers(int pid, int maxHandlesToCheck = 100)
    {
        var results = new List<HandleShareMatch>();
        IntPtr sourceProcess = IntPtr.Zero;
        try
        {
            var all = ReadSystemHandles();
            if (all.Count == 0) return results;

            var mine = all.Where(h => (long)h.UniqueProcessId == pid).Take(maxHandlesToCheck * 3).ToList();
            if (mine.Count == 0) return results;

            sourceProcess = OpenProcess(ProcessDupHandle, false, pid);
            if (sourceProcess == IntPtr.Zero) return results;

            var overallDeadline = DateTime.UtcNow + OverallTimeout;
            int checkedCount = 0;
            var seen = new HashSet<(int Pid, string Type)>();

            foreach (var entry in mine)
            {
                if (checkedCount >= maxHandlesToCheck || DateTime.UtcNow > overallDeadline) break;
                checkedCount++;

                string typeName = ResolveHandleType(sourceProcess, entry.HandleValue);
                if (!HangChainTypesOfInterest.Contains(typeName, StringComparer.OrdinalIgnoreCase)) continue;

                // Same native OBJECT pointer, different owning process - a real cross-process share
                // of this exact kernel object, not a coincidence of type alone.
                foreach (var other in all)
                {
                    if ((long)other.UniqueProcessId == pid || other.Object != entry.Object) continue;
                    if (!seen.Add(((int)other.UniqueProcessId, typeName))) continue;

                    string? name = typeName == "File"
                        ? TryResolveObjectName(sourceProcess, entry.HandleValue)
                        : null;
                    results.Add(new HandleShareMatch((int)other.UniqueProcessId, typeName, name));
                }
            }
        }
        catch
        {
            // Best-effort - an empty result just means "no shared-object chain found", the same
            // degrade-gracefully shape as ReadHandleTypeCounts above.
        }
        finally
        {
            if (sourceProcess != IntPtr.Zero) CloseHandle(sourceProcess);
        }
        return results;
    }

    /// <summary>Best-effort file-path resolution for a single File-type handle (ObjectNameInformation,
    /// class 1), used only to hand a real path to FileLockLookupService.FindProcessesWithFileOpen
    /// for a second, independently-sourced confirmation of #244's chain guess - same abandoned-
    /// worker-thread-with-timeout pattern as ResolveHandleType, since NtQueryObject's hang risk
    /// applies here too (a named pipe with no listener is the classic case for ObjectNameInformation
    /// as much as ObjectTypeInformation).</summary>
    private static string? TryResolveObjectName(IntPtr sourceProcess, IntPtr handleValue)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int dupStatus = NtDuplicateObject(sourceProcess, handleValue, GetCurrentProcess(),
                out dup, 0, 0, DuplicateSameAccess);
            if (dupStatus != 0 || dup == IntPtr.Zero) return null;
        }
        catch
        {
            return null;
        }

        // NtQueryObject normally returns an NT device path here ("\Device\HarddiskVolumeN\...."),
        // not a drive-letter path - only the raw NT-namespace read happens inside the timed worker
        // below (QueryDosDevice itself isn't hang-prone, so the drive-letter conversion happens
        // afterwards, outside the timing-sensitive section).
        string? rawPath = null;
        var worker = new Thread(() =>
        {
            try
            {
                int size = 0x1000;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryObject(dup, ObjectNameInformation, buffer, size, out _);
                    if (status == 0)
                    {
                        ushort length = (ushort)Marshal.ReadInt16(buffer, 0);
                        IntPtr strPtr = Marshal.ReadIntPtr(buffer, 8);
                        rawPath = length > 0 && strPtr != IntPtr.Zero
                            ? Marshal.PtrToStringUni(strPtr, length / 2)
                            : null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { /* leave rawPath null */ }
        })
        { IsBackground = true };

        worker.Start();
        bool finished = worker.Join(PerHandleTimeout);
        if (!finished) return null;

        try { CloseHandle(dup); } catch { /* ignore */ }
        if (string.IsNullOrWhiteSpace(rawPath)) return null;

        return rawPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
            ? ConvertDeviceToDrivePath(rawPath)
            : rawPath;
    }

    /// <summary>Converts an NT device path ("\Device\HarddiskVolumeN\...") to a drive-letter path
    /// via QueryDosDevice - the standard technique (comparing each drive letter's own device-path
    /// prefix, since there's no direct "reverse" API) - so FileLockLookupService.FindProcessesWithFileOpen
    /// (which needs File.Exists to succeed) can actually use the result. QueryDosDevice/
    /// DriveInfo.GetDrives aren't the hang-prone part of this (NtQueryObject already is, handled by
    /// the timed worker above), so this runs unguarded.</summary>
    private static string? ConvertDeviceToDrivePath(string devicePath)
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string driveLetter = drive.Name.TrimEnd('\\');
                var target = new StringBuilder(260);
                if (QueryDosDevice(driveLetter, target, target.Capacity) == 0) continue;

                string prefix = target.ToString();
                // The match must end on a path-segment boundary: "\Device\HarddiskVolume1" is a
                // string prefix of "\Device\HarddiskVolume10\..." too, and matching it would
                // splice the wrong drive letter onto a mangled remainder on machines with 10+
                // volumes.
                if (!devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (devicePath.Length == prefix.Length || devicePath[prefix.Length] == '\\')
                    return driveLetter + devicePath[prefix.Length..];
            }
        }
        catch
        {
            // Best-effort - a failed conversion just means #244's File cross-check falls back to
            // the primary shared-object-pointer match alone.
        }
        return null;
    }

    public static List<(string TypeName, int Count)> ReadHandleTypeCounts(int pid)
    {
        var counts = new Dictionary<string, int>();
        IntPtr sourceProcess = IntPtr.Zero;
        try
        {
            var entries = ReadSystemHandles().Where(h => (long)h.UniqueProcessId == pid).Take(MaxHandlesToResolve * 2).ToList();
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
    /// timeout - see the class remarks for why NtQueryObject can't be trusted to always return.
    /// Internal (not private) so #411's SharedMemoryInspectionService can reuse the exact same
    /// duplicate-then-query-with-timeout pattern to find "Section" handles specifically, rather
    /// than re-deriving it.</summary>
    internal static string ResolveHandleType(IntPtr sourceProcess, IntPtr handleValue)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int dupStatus = NtDuplicateObject(sourceProcess, handleValue, GetCurrentProcess(),
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

    /// <summary>#411: same duplicate-then-query-with-timeout shape as ResolveHandleType above, but
    /// asks for the object's *name* (OBJECT_NAME_INFORMATION, also a leading UNICODE_STRING -
    /// identical layout trick) instead of its type - used to identify which named Section a
    /// handle refers to, once ResolveHandleType has already confirmed it's a Section. Most
    /// handles (anonymous sections, most other object types) have no name at all, which
    /// NtQueryObject reports as a zero-length string, not an error - returned here as null rather
    /// than "(unresolved)" so callers can tell "no name" apart from "the query failed/timed out".</summary>
    internal static string? ResolveHandleName(IntPtr sourceProcess, IntPtr handleValue)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int dupStatus = NtDuplicateObject(sourceProcess, handleValue, GetCurrentProcess(),
                out dup, 0, 0, DuplicateSameAccess);
            if (dupStatus != 0 || dup == IntPtr.Zero) return null;
        }
        catch
        {
            return null;
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
                    int status = NtQueryObject(dup, ObjectNameInformation, buffer, size, out _);
                    if (status == 0)
                    {
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

        if (finished)
        {
            try { CloseHandle(dup); } catch { /* ignore */ }
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        return null; // timed out - treat the same as "no name available", not worth surfacing as an error.
    }

    /// <summary>#411: the section's maximum size in bytes (SECTION_BASIC_INFORMATION.MaximumSize),
    /// once ResolveHandleType has already confirmed a handle is a Section - null on any failure
    /// (access denied, already closed, or the query itself fails). Unlike ResolveHandleType/
    /// ResolveHandleName above, NtQuerySection isn't documented to ever block indefinitely the way
    /// NtQueryObject can on certain handle types (a named pipe with no listener), so this runs
    /// inline rather than on its own abandoned thread - it's still wrapped in try/catch so a
    /// surprise failure degrades to "size unknown" rather than throwing.</summary>
    internal static long? ResolveSectionSizeBytes(IntPtr sourceProcess, IntPtr handleValue)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int dupStatus = NtDuplicateObject(sourceProcess, handleValue, GetCurrentProcess(),
                out dup, 0, 0, DuplicateSameAccess);
            if (dupStatus != 0 || dup == IntPtr.Zero) return null;

            int size = Marshal.SizeOf<SECTION_BASIC_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySection(dup, SectionBasicInformation, buffer, size, out _);
                if (status != 0) return null;
                var info = Marshal.PtrToStructure<SECTION_BASIC_INFORMATION>(buffer);
                return info.MaximumSize > 0 ? info.MaximumSize : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dup != IntPtr.Zero) { try { CloseHandle(dup); } catch { /* ignore */ } }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECTION_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public uint AllocationAttributes;
        public long MaximumSize;
    }

    private const int SectionBasicInformation = 0;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySection(IntPtr sectionHandle, int sectionInformationClass, IntPtr sectionInformation, int sectionInformationLength, out int returnLength);

    /// <summary>#411: system-wide handle table, reduced to just (pid, handle value) pairs - the
    /// two fields SharedMemoryInspectionService needs, without exposing the raw marshaled struct
    /// (and its ObjectTypeIndex/GrantedAccess fields, which are unstable across Windows versions
    /// and only meaningful to ReadHandleTypeCounts' own per-pid filtering above) outside this
    /// class.</summary>
    internal static List<(int ProcessId, ushort HandleValue)> ReadSystemHandlesAll() =>
        // Keeps the historical (int, ushort) tuple shape SharedMemoryInspectionService binds to.
        // The extended handle table carries pointer-sized handle values; the rare handle above
        // 0xFFFF (a process holding >16k handles) is skipped rather than truncated, since a
        // truncated value would resolve a DIFFERENT handle in the source process.
        ReadSystemHandles()
            .Where(e => (ulong)e.HandleValue <= ushort.MaxValue)
            .Select(e => ((int)e.UniqueProcessId, (ushort)e.HandleValue))
            .ToList();

    /// <summary>Internal (not private) so LsassHandleWatchService (#857) can reuse this same
    /// system-wide handle-table walk for its "who holds a handle to lsass.exe" scan, rather than
    /// duplicating the NtQuerySystemInformation/SystemExtendedHandleInformation P/Invoke plumbing a second
    /// time - see that class's remarks for how it uses these raw entries.</summary>
    /// <summary>Hard ceiling on the system handle table snapshot, in bytes. A healthy machine's
    /// whole table is a few megabytes (roughly 40 bytes x a few hundred thousand handles), but a
    /// process leaking handles drags it up without limit - a machine with a dozen runaway
    /// processes measured 62 million live handles, which is a multi-hundred-MB snapshot, and this
    /// method was faithfully allocating a managed array that size (three live at once accounted for
    /// 1.49 GB - 93% - of this app's entire heap). A diagnostic tool has to stay small on exactly
    /// the sick machine it exists to diagnose, so past this ceiling the walk reports nothing
    /// rather than trying: callers already treat an empty result as "couldn't read the handle
    /// table", which is CLAUDE.md's degrade-never-fabricate rule and is far better than pinning
    /// a gigabyte-plus to answer a "quick flag, not a verdict" question.</summary>
    private const int MaxHandleTableBytes = 48 << 20; // 48 MB - ~1.2 million handles

    internal static List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> ReadSystemHandles()
    {
        int size = 1 << 20; // 1 MB starting guess, grown below if needed
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (size > MaxHandleTableBytes) return new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

                // SYSTEM_HANDLE_INFORMATION_EX header: ULONG_PTR NumberOfHandles + ULONG_PTR Reserved.
                int headerSize = IntPtr.Size * 2;
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

                // Never trust the count past what this buffer actually holds. The kernel fills in
                // as much as fits, so a count larger than the buffer would have sent the loop
                // below reading unallocated memory - and sized the managed List to match.
                long maxEntries = (size - headerSize) / entrySize;
                if (count > maxEntries) count = maxEntries;
                if (count < 0) count = 0;

                var list = new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>((int)count);
                IntPtr entryPtr = IntPtr.Add(buffer, headerSize);
                for (int i = 0; i < count; i++)
                    list.Add(Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(IntPtr.Add(entryPtr, i * entrySize)));
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
    }

    // Class 64 - the extended table. The legacy SystemHandleInformation (class 16) carries 16-bit
    // UniqueProcessId/HandleValue fields, which silently truncate the >65535 pids Win10/11 assign
    // routinely - crediting a process's handles to whatever pid equals (pid & 0xFFFF).
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;   // ULONG_PTR
        public IntPtr HandleValue;       // ULONG_PTR
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
