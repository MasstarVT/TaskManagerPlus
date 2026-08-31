using System.Diagnostics;
using System.Management;

namespace TaskManagerPlus.Services;

/// <summary>
/// #950: CPU/Memory/Disk (+overall "WinSPR") WinSAT sub-scores for a performance baseline.
/// Windows Experience Index itself was removed from the UI after Windows 8, but the underlying
/// `winsat.exe` tool and its `Win32_WinSAT` WMI class (root\cimv2) both still ship on every
/// current Windows release, and WMI reads directly from the same on-disk result cache
/// (%WinDir%\Performance\WinSAT\DataStore) `winsat` itself writes to - so a WMI query is both the
/// "prefer a known Windows API" choice (CLAUDE.md) and, in the common case, free: most machines
/// already carry a valid formal-assessment result from Windows Setup's own first-boot run, with
/// nothing this app needs to trigger.
///
/// <see cref="RunFormalAsync"/> (an actual `winsat formal` run) is the deliberately-slow fallback,
/// reserved for the user's explicit "Capture full baseline" action (see BaselineViewModel) - it
/// drives CPU, memory, disk, and graphics load for roughly a minute, so it must never run from the
/// automatic weekly capture (#952) or any other unattended/background path: doing so would itself
/// spoil the very idle-baseline reading it's part of.
/// </summary>
public static class WinSatService
{
    public sealed class Scores
    {
        public double? Cpu { get; init; }
        public double? Memory { get; init; }
        public double? Disk { get; init; }
        public double? Overall { get; init; }

        /// <summary>False when Windows itself flagged the cached result as incoherent with the
        /// current hardware (WinSATAssessmentState == Incoherent, i.e. something changed since the
        /// last formal run) - surfaced so callers can avoid presenting a stale score as current.</summary>
        public bool IsCoherent { get; init; } = true;
    }

    /// <summary>Reads whatever WinSAT result is already cached on this machine - fast, no process
    /// launch. Returns null when WinSAT has never run on this machine (WinSATAssessmentState ==
    /// NotAvailable) or the WMI class/query itself fails (older/locked-down systems) - degrades to
    /// "not available" rather than a fabricated score, same as every other sensor-style read in
    /// this app.</summary>
    public static Scores? ReadCachedScoresOrNull()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CPUScore, MemoryScore, DiskScore, WinSPRLevel, WinSATAssessmentState FROM Win32_WinSAT");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint state = 0;
                try { state = Convert.ToUInt32(mo["WinSATAssessmentState"] ?? 0u); } catch { /* treat as unknown */ }
                if (state == 3) return null; // NotAvailable - never assessed on this machine

                double? Score(string prop)
                {
                    var raw = mo[prop];
                    if (raw is null) return null;
                    try
                    {
                        double v = Convert.ToDouble(raw);
                        return v < 0 ? null : v; // WinSAT uses -1.0 for "not assessed"
                    }
                    catch { return null; }
                }

                return new Scores
                {
                    Cpu = Score("CPUScore"),
                    Memory = Score("MemoryScore"),
                    Disk = Score("DiskScore"),
                    Overall = Score("WinSPRLevel"),
                    IsCoherent = state != 2, // Incoherent - hardware changed since the last run
                };
            }
        }
        catch
        {
            // WMI class unavailable/access denied - degrade to "not available".
        }
        return null;
    }

    /// <summary>Runs `winsat formal` (the same full formal assessment Windows Setup itself
    /// performs) and re-reads the cache afterward. Only ever called from an explicit user action -
    /// see the class remarks. Elevation is already a given for this whole app (CLAUDE.md), so no
    /// separate elevation handling is needed here. Honors `ct` for cancellation (killing the
    /// winsat process tree, which the pre-ToolRunner version never did - it rethrew and orphaned
    /// the run); the 10-minute ceiling is a safety net well above a real formal assessment's
    /// several-minute runtime, since the only caller passes CancellationToken.None.</summary>
    public static async Task<Scores?> RunFormalAsync(CancellationToken ct)
    {
        try
        {
            var (_, exitCode) = await ToolRunner.RunCapturedAsync("winsat.exe", "formal", 600_000, ct);
            if (exitCode is null)
            {
                ct.ThrowIfCancellationRequested();
                return null; // genuine timeout - the run was killed, so there's no fresh cache to trust
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
        return ReadCachedScoresOrNull();
    }
}
