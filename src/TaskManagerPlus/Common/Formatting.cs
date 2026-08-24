namespace TaskManagerPlus.Common;

/// <summary>
/// Shared byte-formatting helpers. Two variants because the existing call sites want two
/// different presentations of the same unit ladder: a plain capacity ("Unknown" for <= 0,
/// used for specs/tiles) and a throughput rate ("/s" suffix, 0 is a normal value for an idle
/// link/disk). Extracted here once a 4th chart Y-axis needed byte formatting, on top of the
/// near-identical private copies PerformanceViewModel and SystemSpecsViewModel already had.
/// </summary>
public static class Formatting
{
    private static readonly string[] CapacityUnits = { "B", "KB", "MB", "GB", "TB" };
    private static readonly string[] RateUnits = { "B/s", "KB/s", "MB/s", "GB/s" };

    /// <summary>Formats a byte count as a capacity ("1.5 GB"). Returns "Unknown" for values &lt;= 0.</summary>
    public static string FormatBytes(double bytes)
    {
        if (bytes <= 0) return "Unknown";
        return Scale(bytes, CapacityUnits);
    }

    /// <summary>Formats a bytes-per-second rate ("1.5 MB/s"). 0 is a normal value (idle link/disk).</summary>
    public static string FormatByteRate(double bytesPerSec)
        => Scale(Math.Abs(bytesPerSec), RateUnits);

    private static string Scale(double value, string[] units)
    {
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
}
