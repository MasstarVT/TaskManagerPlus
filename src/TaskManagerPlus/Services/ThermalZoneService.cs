using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #601: reads Windows' own built-in "Thermal Zone Information" perf-counter category - a genuine
/// ACPI-sourced throttle signal Windows itself acts on, completely independent of
/// SensorMonitorService/LibreHardwareMonitorLib, so it keeps working when SensorsAvailable is
/// false (Smart App Control, no driver support, ...). Same dynamic-instance-discovery shape as
/// GpuMonitorService's "GPU Engine"/"GPU Adapter Memory" reads: instances (thermal zones) can
/// appear/disappear, so counters are created lazily and disposed once their instance is gone,
/// rather than assuming a fixed zone list at startup.
///
/// Every individual counter read is wrapped separately - a zone that doesn't populate one of the
/// four counters (not every BIOS/zone exposes "Throttle Percentage" or "High Precision
/// Temperature") just reports null for that field, never a fabricated value.
/// </summary>
public sealed class ThermalZoneService : IDisposable
{
    private const string CategoryName = "Thermal Zone Information";

    private readonly Dictionary<string, PerformanceCounter?> _tempCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter?> _highPrecisionTempCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter?> _throttlePercentCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter?> _passiveLimitCounters = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable { get; }

    public ThermalZoneService()
    {
        IsAvailable = CategoryExists();
    }

    private static bool CategoryExists()
    {
        try { return PerformanceCounterCategory.Exists(CategoryName); }
        catch { return false; }
    }

    public List<ThermalZoneReading> Sample()
    {
        var result = new List<ThermalZoneReading>();
        if (!IsAvailable) return result;

        string[] instances;
        try { instances = new PerformanceCounterCategory(CategoryName).GetInstanceNames(); }
        catch { return result; }

        var seen = new HashSet<string>(instances, StringComparer.OrdinalIgnoreCase);
        PruneStale(_tempCounters, seen);
        PruneStale(_highPrecisionTempCounters, seen);
        PruneStale(_throttlePercentCounters, seen);
        PruneStale(_passiveLimitCounters, seen);

        foreach (var instance in instances)
        {
            // Windows reports raw ACPI temperature in tenths of a Kelvin - the same unit
            // MSAcpi_ThermalZoneTemperature uses, converted here to °C for display.
            double? rawTemp = ReadOrCreate(_tempCounters, instance, "Temperature");
            double? rawHighPrecisionTemp = ReadOrCreate(_highPrecisionTempCounters, instance, "High Precision Temperature");
            double? throttlePercent = ReadOrCreate(_throttlePercentCounters, instance, "Throttle Percentage");
            double? passiveLimit = ReadOrCreate(_passiveLimitCounters, instance, "% Passive Limit");

            if (rawTemp is null && rawHighPrecisionTemp is null && throttlePercent is null && passiveLimit is null)
                continue; // nothing usable at all for this zone this tick

            result.Add(new ThermalZoneReading
            {
                ZoneName = instance,
                TemperatureC = rawTemp is { } t ? t / 10.0 - 273.15 : null,
                HighPrecisionTemperatureC = rawHighPrecisionTemp is { } hp ? hp / 10.0 - 273.15 : null,
                ThrottlePercent = throttlePercent,
                PassiveLimitPercent = passiveLimit,
            });
        }

        return result;
    }

    private static double? ReadOrCreate(Dictionary<string, PerformanceCounter?> cache, string instance, string counterName)
    {
        if (!cache.TryGetValue(instance, out var counter))
        {
            try { counter = new PerformanceCounter(CategoryName, counterName, instance, readOnly: true); }
            catch { counter = null; } // this zone doesn't expose this particular counter
            cache[instance] = counter;
        }

        if (counter is null) return null;
        try { return counter.NextValue(); }
        catch { return null; }
    }

    private static void PruneStale(Dictionary<string, PerformanceCounter?> cache, HashSet<string> seen)
    {
        foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            cache[stale]?.Dispose();
            cache.Remove(stale);
        }
    }

    public void Dispose()
    {
        foreach (var c in _tempCounters.Values) c?.Dispose();
        foreach (var c in _highPrecisionTempCounters.Values) c?.Dispose();
        foreach (var c in _throttlePercentCounters.Values) c?.Dispose();
        foreach (var c in _passiveLimitCounters.Values) c?.Dispose();
    }
}
