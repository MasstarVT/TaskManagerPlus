using LibreHardwareMonitor.Hardware;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Wraps LibreHardwareMonitorLib for real temperature/fan/voltage/power sensor readings -
/// Windows exposes no reliable built-in API for these (WMI's `Win32_TemperatureProbe` /
/// `MSAcpi_ThermalZoneTemperature` are unimplemented by most OEM firmware, which is why every
/// other tab in this app avoids them entirely). The underlying driver benefits from the
/// elevation this app already runs with (`app.manifest` → `requireAdministrator`), but can still
/// fail to load under Smart App Control or a restrictive driver-signing policy - the same class
/// of problem CLAUDE.md already documents for unsigned local builds.
///
/// Opening the driver never throws past this class; callers should treat <see cref="IsAvailable"/>
/// being false, or specific sensors being absent, as a normal and expected state to render
/// gracefully (an empty/"unavailable" tab), not an error to surface.
/// </summary>
public sealed class SensorMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public SensorMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
        };

        try
        {
            _computer.Open();
            _isAvailable = true;
        }
        catch
        {
            _isAvailable = false;
        }
    }

    /// <summary>
    /// Polls every enabled hardware tree and flattens all sensor readings. Safe to call even
    /// when <see cref="IsAvailable"/> is false (returns an empty list). Don't poll faster than
    /// ~1-2s - walking every sensor on every hardware component isn't free, unlike the flat
    /// PerformanceCounter reads the rest of the app uses.
    /// </summary>
    public List<SensorReading> Sample()
    {
        var readings = new List<SensorReading>();
        if (!_isAvailable) return readings;

        try
        {
            foreach (var hardware in _computer.Hardware)
                SampleHardware(hardware, readings);
        }
        catch
        {
            // A hardware/driver hiccup mid-poll shouldn't take the whole tab down - return
            // whatever was gathered before the failure (possibly nothing).
        }

        return readings;
    }

    private static void SampleHardware(IHardware hardware, List<SensorReading> readings)
    {
        hardware.Update();

        foreach (var sensor in hardware.Sensors)
        {
            readings.Add(new SensorReading
            {
                HardwareName = hardware.Name,
                HardwareType = hardware.HardwareType,
                SensorName = sensor.Name,
                Type = sensor.SensorType,
                Value = sensor.Value,
                Identifier = sensor.Identifier.ToString(),
            });
        }

        foreach (var sub in hardware.SubHardware)
            SampleHardware(sub, readings);
    }

    public void Dispose()
    {
        if (!_isAvailable) return;
        try { _computer.Close(); } catch { /* best-effort */ }
    }
}
