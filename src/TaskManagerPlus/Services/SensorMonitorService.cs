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
    // One LibreHardwareMonitor Computer per process, shared by every instance of this class.
    // LHM's Computer.Open/Close drive process-static state (OpCode's Rdtsc/CpuId delegates and
    // code buffer, the ISA/PCI/EC Mutexes), so closing a second, short-lived Computer - the
    // Troubleshoot "fans/hot" check, the memory-timings read, a CLI dump - broke the Energy &
    // Thermals tab's long-lived instance for the rest of the session: every Update() threw,
    // Sample() returned nothing, and the stress test lost its temperature ceiling. Instances are
    // now ref-counted handles: the first one opens the shared Computer, the last Dispose closes
    // it, and Sample() is serialized because Update() is not safe to run concurrently. Two locks
    // (always taken lifetime -> sample) so a constructor on the UI thread never waits behind
    // another caller's multi-second Sample() - only behind the one-time Open().
    private static readonly object LifetimeLock = new();
    private static readonly object SampleLock = new();
    private static Computer? _shared;
    private static bool _sharedAvailable;
    private static int _refCount;

    private readonly bool _isAvailable;
    private bool _disposed;

    public bool IsAvailable => _isAvailable;

    public SensorMonitorService()
    {
        lock (LifetimeLock)
        {
            if (_refCount == 0)
            {
                lock (SampleLock)
                {
                    _shared = new Computer
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
                        _shared.Open();
                        _sharedAvailable = true;
                    }
                    catch
                    {
                        _sharedAvailable = false;
                    }
                }
            }

            _refCount++;
            _isAvailable = _sharedAvailable;
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

        lock (SampleLock)
        {
            if (_disposed || _shared == null) return readings;
            try
            {
                foreach (var hardware in _shared.Hardware)
                    SampleHardware(hardware, readings);
            }
            catch
            {
                // A hardware/driver hiccup mid-poll shouldn't take the whole tab down - return
                // whatever was gathered before the failure (possibly nothing).
            }
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
        lock (LifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (--_refCount > 0) return;

            lock (SampleLock)
            {
                if (_sharedAvailable)
                {
                    try { _shared?.Close(); } catch { /* best-effort */ }
                }
                _shared = null;
                _sharedAvailable = false;
            }
        }
    }
}
