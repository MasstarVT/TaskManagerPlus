namespace TaskManagerPlus.Common;

/// <summary>
/// Shared byte- and time-span-formatting helpers. The byte side has two variants because the
/// existing call sites want two different presentations of the same unit ladder: a plain capacity
/// ("Unknown" for <= 0, used for specs/tiles) and a throughput rate ("/s" suffix, 0 is a normal
/// value for an idle link/disk). Extracted here once a 4th chart Y-axis needed byte formatting, on
/// top of the near-identical private copies PerformanceViewModel and SystemSpecsViewModel already
/// had. The span side (#1086) has three variants for the same reason - the ten-plus private
/// copies it replaced split into three distinct output shapes, each with several word-for-word
/// consumers: an age/uptime ladder that floors at minutes, and two seconds-precision duration
/// ladders whose top unit accumulates (hours or minutes) instead of rolling over.
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

    /// <summary>Formats an age/uptime-scale span as "3d 4h" / "5h 12m" / "42m" - the two most
    /// significant units, floored, never finer than a minute (clamped so a span that has just
    /// crossed zero reads "0m", not a negative count).</summary>
    public static string FormatSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }

    /// <summary>Formats an hours-scale duration as "26h 5m" / "42m 10s" - hours accumulate rather
    /// than rolling into days (a 26-hour span is "26h 5m", not "1d 2h"), and seconds appear only
    /// below one hour.</summary>
    public static string FormatSpanHours(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalMinutes}m {span.Seconds}s";

    /// <summary>Formats a minutes-scale duration as "90m 5s" / "42s" - minutes accumulate rather
    /// than rolling into hours (a 90-minute span is "90m 5s", not "1h 30m"), with a bare floored
    /// second count below one minute.</summary>
    public static string FormatSpanMinutes(TimeSpan span)
        => span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{(int)span.TotalSeconds}s";
}
