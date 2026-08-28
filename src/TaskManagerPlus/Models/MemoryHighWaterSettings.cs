namespace TaskManagerPlus.Models;

/// <summary>#429: persisted peak-committed-memory high-water mark, plus the approximate boot time
/// it was captured under - a simple "same boot session or not" proxy (there's no documented boot
/// GUID exposed cheaply; comparing DateTime.Now minus Environment.TickCount64's uptime against a
/// persisted value, within a small tolerance, is good enough to tell "still the same boot" from "a
/// reboot happened since the last save" - see MemoryHighWaterService's remarks). Persisted so a
/// machine that briefly hit its commit limit hours ago still shows the evidence after this app
/// restarts, as long as the machine itself hasn't rebooted since.</summary>
public sealed class MemoryHighWaterSettings
{
    public DateTime LastKnownBootTimeUtc { get; set; }
    public double PeakCommittedGb { get; set; }
    public double CommitLimitGbAtPeak { get; set; }
    public DateTime PeakTimestampUtc { get; set; }

    public static MemoryHighWaterSettings Defaults => new();
}
