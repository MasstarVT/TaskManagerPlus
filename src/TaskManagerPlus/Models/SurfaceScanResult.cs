namespace TaskManagerPlus.Models;

/// <summary>#332/#335: one problem range found by a read-only surface scan - either a hard read
/// error or a read that took longer than the configured stall threshold. OwningFile (#335) starts
/// as "Not resolved" and is filled in afterwards, best-effort, by mapping the LBA to a volume
/// cluster via `fsutil volume querycluster` (see ClusterMappingService) - approximate, since the
/// mapping assumes the scanned disk's whole capacity belongs to one volume (captioned in the UI).</summary>
public sealed class SurfaceScanResult
{
    public long StartLba { get; init; }
    public long EndLba { get; init; }
    public bool IsHardError { get; init; }
    public double ElapsedMs { get; init; }
    public string Note { get; init; } = string.Empty;
    public string OwningFile { get; set; } = "Not resolved";
}
