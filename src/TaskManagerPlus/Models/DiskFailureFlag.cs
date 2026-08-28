namespace TaskManagerPlus.Models;

/// <summary>#324/#328: lightweight per-disk failure-prediction + driver health-status pair, cheap
/// enough to poll every few sampler ticks - see SystemSpecsService.ReadDiskFailureFlags. Deliberately
/// narrower than the full DiskInfo the System tab's one-time inventory sweep builds (no Model/Size/
/// MediaType/wear lookup), since this is read repeatedly rather than once.</summary>
public sealed class DiskFailureFlag
{
    public int Index { get; init; }

    /// <summary>MSStorageDriver_FailurePredictStatus.PredictFailure - null when the class/driver
    /// doesn't report it at all (common on NVMe and some AHCI/RAID stacks).</summary>
    public bool? PredictFailure { get; init; }

    /// <summary>MSFT_PhysicalDisk.HealthStatus ("Healthy"/"Warning"/"Unhealthy"), null when the
    /// Storage Management API namespace/class is unavailable.</summary>
    public string? DriverHealthStatus { get; init; }
}
