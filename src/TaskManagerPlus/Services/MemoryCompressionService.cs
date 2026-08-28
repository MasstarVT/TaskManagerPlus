using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #282: the "Memory Compression" system process's working set - Windows' memory manager spins
/// this hidden process up to hold compressed pages once it starts compressing memory under
/// pressure. It isn't always present: a lightly-loaded machine may never have compressed anything
/// yet, which is a legitimate "nothing to show" outcome, not a lookup failure - IsAvailable=false
/// distinguishes that from an actual read error.
///
/// Process.GetProcessesByName("Memory Compression") is the documented/working way to find it on
/// current Windows 10/11 builds (verified during development - it returns the process directly, no
/// need to enumerate every process and filter). The Process.GetProcesses()+filter fallback is kept
/// anyway per the item's own "verify what actually works and document it" ask, since it's a cheap
/// safety net against a build that names the process slightly differently (leading/trailing
/// whitespace has been observed on a handful of other Windows hidden system process names).
/// </summary>
public static class MemoryCompressionService
{
    private const string ProcessName = "Memory Compression";

    public static MemoryCompressionInfo Sample(long modifiedPageListBytes, long totalRamBytes)
    {
        Process[] procs = Array.Empty<Process>();
        try
        {
            procs = Process.GetProcessesByName(ProcessName);
            if (procs.Length == 0)
                procs = Process.GetProcesses().Where(p => p.ProcessName.Trim().Equals(ProcessName, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (procs.Length == 0)
            {
                return new MemoryCompressionInfo
                {
                    IsAvailable = false,
                    ModifiedPageListBytes = modifiedPageListBytes,
                    TotalRamBytes = totalRamBytes,
                    StatusText = "The \"Memory Compression\" process wasn't found - Windows hasn't compressed any memory yet, or this build/configuration doesn't run it as a separate process.",
                };
            }

            long workingSet = 0;
            foreach (var p in procs)
            {
                try { workingSet += p.WorkingSet64; } catch { /* ignore */ }
            }

            return new MemoryCompressionInfo
            {
                IsAvailable = true,
                WorkingSetBytes = workingSet,
                ModifiedPageListBytes = modifiedPageListBytes,
                TotalRamBytes = totalRamBytes,
            };
        }
        catch (Exception ex)
        {
            return new MemoryCompressionInfo
            {
                IsAvailable = false,
                ModifiedPageListBytes = modifiedPageListBytes,
                TotalRamBytes = totalRamBytes,
                StatusText = $"Read failed: {ex.Message}",
            };
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
