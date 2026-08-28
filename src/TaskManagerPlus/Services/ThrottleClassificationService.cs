using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #603: pure classification function backing both the CPU tab's throttle-reason dwell breakdown
/// and Energy &amp; Thermals' persisted throttle-episode history (#604) - a single shared formula
/// so the two independent per-tick samplers (CpuViewModel's own 2s timer, EnergyThermalsViewModel's
/// own poll-interval timer) agree on what counts as "throttling" and why, rather than two
/// hand-copied heuristics silently drifting apart over time. Generalizes the "hot AND meaningfully
/// below base clock under load" condition CpuViewModel.IsThrottling/IsPowerLimited and
/// EnergyThermalsViewModel's own throttle-event log already independently derive from the same
/// underlying Performance/EnergyThermals data.
///
/// Same "quick flag, not a verdict" tier as every other heuristic in this app for the
/// Thermal/Power/CoreParked cases - this app has no access to the vendor-proprietary MSR "limit
/// reason" data HWiNFO reads directly. Firmware is the one exception: it's driven by an
/// authoritative Windows event (Kernel-Processor-Power 37/38, #602), not a pattern match.
/// </summary>
public static class ThrottleClassificationService
{
    public static ThrottleReasonClass Classify(
        double? cpuTempC,
        double cpuCurrentPercent,
        double cpuVsBasePercent,
        double? packagePowerW,
        double? packagePowerSessionMaxW,
        int parkedCoreCount,
        int totalCoreCount,
        double? maxThermalZoneThrottlePercent,
        bool firmwareLimitActive)
    {
        // #602: Windows' own firmware-limit event is authoritative - it wins over every other
        // heuristic below whenever it's active, even before the load/clock heuristics themselves
        // would trip.
        if (firmwareLimitActive) return ThrottleReasonClass.Firmware;

        bool highLoad = cpuCurrentPercent >= 60;
        bool belowBase = cpuVsBasePercent <= -8;
        if (!highLoad || !belowBase) return ThrottleReasonClass.None;

        // #601: a thermal zone actively throttling is just as strong a thermal signal as a hot
        // package temperature reading - either one alone is enough to call this Thermal.
        bool hot = cpuTempC is { } t && t >= 85;
        bool zoneThrottling = maxThermalZoneThrottlePercent is { } zp && zp > 0;
        if (hot || zoneThrottling) return ThrottleReasonClass.Thermal;

        bool atPowerCeiling = packagePowerW is { } p && packagePowerSessionMaxW is { } max && max > 0 && p >= max * 0.97;
        if (atPowerCeiling) return ThrottleReasonClass.Power;

        // A meaningful fraction of cores parked (not just one or two, which is normal even under
        // moderate load) while still below base clock under supposedly "high" load points at
        // Windows' own power-plan core parking holding throughput down, not the cooler or a power
        // ceiling.
        if (totalCoreCount > 0 && parkedCoreCount > 0 && parkedCoreCount * 4 >= totalCoreCount)
            return ThrottleReasonClass.CoreParked;

        return ThrottleReasonClass.None;
    }
}
