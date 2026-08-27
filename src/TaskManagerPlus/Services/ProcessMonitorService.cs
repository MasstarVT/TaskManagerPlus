using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Samples running processes and computes per-process CPU% the same way Task
/// Manager does: (CPU time consumed since last sample) / (wall time elapsed *
/// logical processor count).
/// </summary>
public sealed class ProcessMonitorService
{
    private sealed class CpuSample
    {
        public TimeSpan CpuTime;
        public DateTime SampledAtUtc;

        /// <summary>Total (read+write) I/O bytes as of the last sample (#26) - piggybacks on
        /// the same per-pid sample/elapsed-time bookkeeping the CPU% calculation already does,
        /// rather than a second dictionary.</summary>
        public ulong IoBytes;
    }

    private readonly Dictionary<int, CpuSample> _lastSamples = new();
    private readonly Dictionary<int, string> _ownerCache = new();
    private readonly Dictionary<int, string?> _commandLineCache = new();
    // Parent process ID never changes after launch, same caching shape as command line (#52).
    private readonly Dictionary<int, int> _parentPidCache = new();
    // Keyed by file path, not pid: many processes share the same executable (svchost.exe,
    // browser renderer processes, ...), and a signature check reads the file from disk, so
    // caching per-path avoids repeating that I/O for every process using the same binary.
    private readonly Dictionary<string, string> _signatureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private DateTime _lastGlobalSampleUtc = DateTime.UtcNow;

    /// <summary>Well-known high-privilege service accounts - flagged distinctly from an
    /// ordinary signed-in user account when auditing the process list for something unexpected.</summary>
    private static readonly string[] HighPrivilegeAccounts = { "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE" };

    /// <summary>
    /// Builds the current snapshot of processes. Safe to call from a background thread.
    /// </summary>
    public List<ProcessRow> Sample()
    {
        var now = DateTime.UtcNow;
        var processes = Process.GetProcesses();
        var rows = new List<ProcessRow>(processes.Length);
        var seenPids = new HashSet<int>(processes.Length);

        foreach (var proc in processes)
        {
            try
            {
                int pid = proc.Id;
                seenPids.Add(pid);

                TimeSpan cpuTime;
                try
                {
                    cpuTime = proc.TotalProcessorTime;
                }
                catch (Exception)
                {
                    // Access denied (protected/system process) - still list it, just no CPU%.
                    cpuTime = TimeSpan.Zero;
                }

                ulong ioBytesNow = ReadIoBytes(proc);

                double cpuPercent = 0;
                double diskBytesPerSec = 0;
                if (_lastSamples.TryGetValue(pid, out var last))
                {
                    var elapsed = (now - last.SampledAtUtc).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        var cpuDeltaMs = (cpuTime - last.CpuTime).TotalMilliseconds;
                        cpuPercent = Math.Max(0, cpuDeltaMs / elapsed / _logicalProcessors * 100.0);

                        if (ioBytesNow >= last.IoBytes)
                            diskBytesPerSec = (ioBytesNow - last.IoBytes) / (elapsed / 1000.0);
                    }
                }
                _lastSamples[pid] = new CpuSample { CpuTime = cpuTime, SampledAtUtc = now, IoBytes = ioBytesNow };

                long memoryBytes = 0;
                int threadCount = 0;
                int handleCount = 0;
                DateTime? startTime = null;
                string? filePath = null;
                try { memoryBytes = proc.WorkingSet64; } catch { /* ignore */ }
                try { threadCount = proc.Threads.Count; } catch { /* ignore */ }
                try { handleCount = proc.HandleCount; } catch { /* ignore */ }
                try { startTime = proc.StartTime; } catch { /* ignore, protected process */ }
                try { filePath = proc.MainModule?.FileName; } catch { /* ignore, protected/x-bit mismatch */ }

                string status = "Running";
                try
                {
                    if (proc.Responding == false)
                        status = "Not responding";
                }
                catch { /* ignore */ }

                string owner = GetOwnerCached(pid);

                rows.Add(new ProcessRow
                {
                    Pid = pid,
                    Name = SafeName(proc),
                    CpuPercent = Math.Round(Math.Min(cpuPercent, 100.0 * _logicalProcessors), 1),
                    MemoryBytes = memoryBytes,
                    DiskBytesPerSec = Math.Round(diskBytesPerSec, 0),
                    Status = status,
                    User = owner,
                    ThreadCount = threadCount,
                    HandleCount = handleCount,
                    StartTime = startTime,
                    FilePath = filePath,
                    CommandLine = GetCommandLineCached(pid),
                    SignatureStatus = GetSignatureStatusCached(filePath),
                    IsHighPrivilege = HighPrivilegeAccounts.Contains(owner, StringComparer.OrdinalIgnoreCase),
                    ParentPid = GetParentPidCached(pid),
                });
            }
            catch (Exception)
            {
                // Process exited mid-enumeration or is otherwise inaccessible - skip it.
            }
            finally
            {
                proc.Dispose();
            }
        }

        // #52: resolve each row's parent name from this same batch (a second pass, since the
        // parent may appear later in the enumeration order than its child) - falls back to
        // "(exited)" for a parent that's no longer running rather than leaving it blank.
        var namesByPid = rows.ToDictionary(r => r.Pid, r => r.Name);
        foreach (var row in rows)
        {
            row.ParentName = row.ParentPid > 0 && namesByPid.TryGetValue(row.ParentPid, out var parentName)
                ? parentName
                : "(exited)";
        }

        // Drop cached samples/owners/command lines for processes that no longer exist. The
        // signature cache is keyed by file path, not pid, so it isn't pruned here - it's small
        // (one entry per distinct executable seen) and a stale entry just saves a re-check if
        // the same binary starts again later.
        PruneStaleEntries(_lastSamples, seenPids);
        PruneStaleEntries(_ownerCache, seenPids);
        PruneStaleEntries(_commandLineCache, seenPids);
        PruneStaleEntries(_parentPidCache, seenPids);

        _lastGlobalSampleUtc = now;
        return rows;
    }

    /// <summary>Total (read+write) I/O bytes for a process via the native GetProcessIoCounters
    /// call (#26) - .NET's Process class doesn't expose this. Best-effort: a protected/
    /// inaccessible process just reports 0, the same as every other per-process field here that
    /// can be access-denied.</summary>
    private static ulong ReadIoBytes(Process proc)
    {
        try
        {
            if (GetProcessIoCounters(proc.Handle, out var counters))
                return counters.ReadTransferCount + counters.WriteTransferCount;
        }
        catch
        {
            // Access denied, or the process exited mid-call - leave it at 0.
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private static void PruneStaleEntries<TValue>(Dictionary<int, TValue> dict, HashSet<int> livePids)
    {
        if (dict.Count == 0) return;
        List<int>? toRemove = null;
        foreach (var pid in dict.Keys)
        {
            if (!livePids.Contains(pid))
                (toRemove ??= new List<int>()).Add(pid);
        }
        if (toRemove is null) return;
        foreach (var pid in toRemove)
            dict.Remove(pid);
    }

    private static string SafeName(Process proc)
    {
        try { return proc.ProcessName; }
        catch { return "(unknown)"; }
    }

    private string GetOwnerCached(int pid)
    {
        if (_ownerCache.TryGetValue(pid, out var cached))
            return cached;

        string owner = "SYSTEM";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Handle FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                var args = new object[] { string.Empty, string.Empty };
                var result = (uint)mo.InvokeMethod("GetOwner", args);
                if (result == 0)
                    owner = (string)args[0];
            }
        }
        catch
        {
            owner = "N/A";
        }

        _ownerCache[pid] = owner;
        return owner;
    }

    /// <summary>Command-line arguments a process was launched with, fetched via WMI (.NET's
    /// Process class doesn't expose this) and cached per-pid since a running process's command
    /// line never changes after launch.</summary>
    private string? GetCommandLineCached(int pid)
    {
        if (_commandLineCache.TryGetValue(pid, out var cached))
            return cached;

        string? commandLine = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
                commandLine = mo["CommandLine"] as string;
        }
        catch
        {
            // Access denied or the process exited mid-query - leave it null.
        }

        _commandLineCache[pid] = commandLine;
        return commandLine;
    }

    /// <summary>Parent process ID (#52), fetched via WMI (like command line, .NET's Process class
    /// doesn't expose this) and cached per-pid since it never changes after launch.</summary>
    private int GetParentPidCached(int pid)
    {
        if (_parentPidCache.TryGetValue(pid, out var cached))
            return cached;

        int parentPid = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
                parentPid = Convert.ToInt32(mo["ParentProcessId"] ?? 0);
        }
        catch
        {
            // Access denied or the process exited mid-query - leave it 0 ("unknown").
        }

        _parentPidCache[pid] = parentPid;
        return parentPid;
    }

    /// <summary>
    /// "Signed" / "Unsigned" / "Unknown", cached per file path. Uses the legacy
    /// X509Certificate.CreateFromSignedFile check (embedded Authenticode signature only - it
    /// does NOT verify the certificate chain or check revocation, and it can't see catalog
    /// signatures, which many Windows system files rely on instead of an embedded one, so a
    /// small number of legitimate system binaries will show as "Unsigned" here). That's a real
    /// limitation, but a full WinVerifyTrust chain-and-catalog check needs native interop this
    /// app doesn't otherwise take on - this is the same "good enough for a quick visual flag,
    /// not a security verdict" tradeoff as the rest of this tab's process list.
    /// </summary>
    private string GetSignatureStatusCached(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "Unknown";
        if (_signatureCache.TryGetValue(filePath, out var cached)) return cached;

        string status;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            status = cert is not null ? "Signed" : "Unsigned";
        }
        catch (FileNotFoundException)
        {
            status = "Unknown";
        }
        catch (UnauthorizedAccessException)
        {
            status = "Unknown";
        }
        catch
        {
            // CreateFromSignedFile throws CryptographicException for a file with no embedded
            // signature at all - the expected, common case for an unsigned binary.
            status = "Unsigned";
        }

        _signatureCache[filePath] = status;
        return status;
    }

    /// <summary>Ends a single process.</summary>
    public static (bool Success, string? Error) EndProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Ends a process and its entire descendant tree.</summary>
    public static (bool Success, string? Error) EndProcessTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
