using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 17, item 60: the crash-count-by-executable dictionary ProcessesViewModel reads once per
/// row on every 1s-ish poll tick - CLAUDE.md is explicit that this lookup must be a cheap in-memory
/// dictionary read, never a fresh event-log query on that tick (ProcessesViewModel is one of the
/// few live-polled ViewModels; StabilityViewModel's own tab is on-demand precisely because an
/// event-log scan isn't cheap enough to repeat that often).
///
/// Populated two ways, matching this chunk's own instruction ("built once on Stability refresh or
/// lazily cached"): primarily by <see cref="UpdateFrom"/>, called from StabilityViewModel after
/// its own on-demand refresh already parsed the crash/hang event lists for its own cards (no
/// second query); and, for a session that never opens the Stability tab at all, a lazy self-refresh
/// fallback kicked off (on a background thread, at most once every <see cref="MinRefreshInterval"/>)
/// the first time <see cref="GetCrashCount"/> is asked for a count with no data loaded yet.
/// </summary>
public static class CrashHistoryCacheService
{
    // Item 60 asks for "crashes (30d)" specifically - independent of whatever longer lookback the
    // Stability tab's own WER-history chart (item 48, 90 days) uses.
    private const int LookbackDays = 30;
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

    private static readonly object Lock = new();
    private static Dictionary<string, int> _countsByExe = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastBuiltUtc = DateTime.MinValue;
    private static bool _refreshInProgress;

    /// <summary>Cheap, synchronous, in-memory lookup - never touches the event log itself. Also
    /// opportunistically kicks off a background self-refresh (see remarks above) when the cache
    /// has never been populated and nothing else in the app is already refreshing it.</summary>
    public static int GetCrashCount(string? processName)
    {
        var key = ApplicationCrashService.NormalizeExeName(processName);
        if (key is null) return 0;

        Dictionary<string, int> snapshot;
        bool stale;
        lock (Lock)
        {
            snapshot = _countsByExe;
            stale = DateTime.UtcNow - _lastBuiltUtc >= MinRefreshInterval;
        }

        // Kicks off at most once every MinRefreshInterval (TryKickOffBackgroundRefresh
        // double-checks staleness itself under the lock) - cheap enough to call unconditionally
        // from every tick's per-row lookup.
        if (stale) TryKickOffBackgroundRefresh();

        return snapshot.TryGetValue(key, out var count) ? count : 0;
    }

    /// <summary>Item 60's primary feed: StabilityViewModel already reads and parses the crash/hang
    /// event lists for its own "Application crashes"/"Application hangs" cards every time it
    /// refreshes - this just re-groups that already-fetched data into the executable-keyed count
    /// dictionary, no second event-log query.</summary>
    public static void UpdateFrom(List<ApplicationCrashEvent> crashes, List<ApplicationHangEvent> hangs)
    {
        var counts = ApplicationCrashService.BuildCrashCountsByExecutable(crashes, hangs);
        lock (Lock)
        {
            _countsByExe = counts;
            _lastBuiltUtc = DateTime.UtcNow;
        }
    }

    private static void TryKickOffBackgroundRefresh()
    {
        lock (Lock)
        {
            if (_refreshInProgress) return;
            if (DateTime.UtcNow - _lastBuiltUtc < MinRefreshInterval) return;
            _refreshInProgress = true;
        }

        Task.Run(() =>
        {
            try
            {
                var eventLog = new EventLogService();
                var crashes = eventLog.ReadApplicationCrashEvents(LookbackDays);
                var hangs = eventLog.ReadApplicationHangEvents(LookbackDays);
                UpdateFrom(crashes, hangs);
            }
            catch
            {
                // Best-effort - leave whatever was cached (possibly nothing) in place; the next
                // GetCrashCount call after MinRefreshInterval elapses will try again.
            }
            finally
            {
                lock (Lock) _refreshInProgress = false;
            }
        });
    }
}
