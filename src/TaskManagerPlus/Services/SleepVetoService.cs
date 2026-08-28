using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #656: pure correlation logic for failed-sleep / vetoed-transition detection - takes the raw
/// Kernel-Power event 42 ("entering sleep") timestamps and the Power-Troubleshooter sleep/wake
/// pairs EnergyThermalsViewModel already reads via EventLogService/WakeHistoryService, and looks
/// for a 42 with no matching resume, or a resume that came back within seconds (a connected-standby
/// session that aborted almost immediately). No subprocess/event-log I/O of its own - kept a pure
/// function over already-fetched data, unlike every I/O-bound service elsewhere in this chunk.
/// </summary>
public static class SleepVetoService
{
    private static readonly TimeSpan MatchTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ImmediateAbortThreshold = TimeSpan.FromSeconds(15);

    public static List<SleepTransitionRecord> Correlate(
        IEnumerable<DateTime> sleepEntryEvents,
        IEnumerable<WakeHistoryEntry> wakeHistory,
        string? possibleVetoingDriverHint)
    {
        var resumePairs = wakeHistory
            .Where(w => w.SleepTime is not null)
            .Select(w => (Sleep: w.SleepTime!.Value, Wake: w.WakeTime))
            .ToList();

        var result = new List<SleepTransitionRecord>();
        foreach (var attempt in sleepEntryEvents.OrderBy(t => t))
        {
            var candidates = resumePairs
                .Where(p => Math.Abs((p.Sleep - attempt).TotalSeconds) <= MatchTolerance.TotalSeconds)
                .OrderBy(p => Math.Abs((p.Sleep - attempt).TotalSeconds))
                .ToList();

            if (candidates.Count == 0)
            {
                // Never found a matching resume within the lookback window at all - the "never
                // came back" case (or genuinely still on, if this attempt is very recent).
                result.Add(new SleepTransitionRecord
                {
                    SleepAttemptTime = attempt,
                    ResumeTime = null,
                    WasImmediateAbort = false,
                    PossibleVetoingDriverHint = possibleVetoingDriverHint,
                });
                continue;
            }

            var best = candidates[0];
            if (best.Wake - best.Sleep <= ImmediateAbortThreshold)
            {
                result.Add(new SleepTransitionRecord
                {
                    SleepAttemptTime = attempt,
                    ResumeTime = best.Wake,
                    WasImmediateAbort = true,
                    PossibleVetoingDriverHint = possibleVetoingDriverHint,
                });
            }
            // else: a normal sleep/wake cycle - not flagged at all.
        }

        return result.OrderByDescending(r => r.SleepAttemptTime).ToList();
    }
}
