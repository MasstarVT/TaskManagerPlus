using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>
/// Backs the `--dump-json &lt;path&gt;` command-line flag (#77) - a one-shot snapshot of the
/// current metrics as JSON, written to disk and the process exits, no UI shown. Useful for
/// scripted/remote diagnostics (a scheduled task or remote-support script that wants a machine-
/// readable reading without driving the full GUI). Each service is constructed fresh and disposed
/// right after one sample - this is a one-shot CLI path, not something that needs the long-lived
/// sampler objects the running app keeps. The app's elevation requirement (app.manifest) still
/// applies here: launching with this flag still triggers the same UAC prompt as a normal launch.
/// </summary>
public static class CliDumpService
{
    public static void DumpSnapshot(string outputPath)
    {
        using var hardware = new HardwareMonitorService();
        // Rate-based counters (CPU%, disk/network throughput) read 0 on their very first sample
        // immediately after construction even though the constructor primes several of them - a
        // real reading needs one full tick's worth of elapsed time, which a one-shot CLI dump
        // can't wait around for without meaningfully slowing down a scripted caller. This is a
        // known, documented limitation of this snapshot mode, not a bug.
        var snapshot = hardware.Sample();

        var specsService = new SystemSpecsService();
        var specs = specsService.Query();

        using var sensors = new SensorMonitorService();
        var readings = sensors.Sample();

        var result = new
        {
            timestamp = DateTime.Now.ToString("O"),
            cpu = new
            {
                name = specs.CpuName,
                percent = snapshot.CpuTotalPercent,
                clockGhz = snapshot.CpuCurrentClockGhz,
                baseClockGhz = snapshot.CpuBaseClockGhz,
                logicalProcessors = snapshot.LogicalProcessors,
                physicalCores = snapshot.PhysicalCores,
            },
            memory = new
            {
                usedBytes = snapshot.RamUsedBytes,
                totalBytes = snapshot.RamTotalBytes,
                percent = snapshot.RamPercent,
            },
            disk = new
            {
                activePercent = snapshot.DiskActivePercent,
                readBytesPerSec = snapshot.DiskReadBytesPerSec,
                writeBytesPerSec = snapshot.DiskWriteBytesPerSec,
            },
            network = new
            {
                receiveBytesPerSec = snapshot.NetworkReceiveBytesPerSec,
                sendBytesPerSec = snapshot.NetworkSendBytesPerSec,
            },
            sensors = readings
                .Where(r => r.Value.HasValue)
                .Select(r => new { hardware = r.HardwareName, sensor = r.SensorName, type = r.Type.ToString(), value = r.Value!.Value })
                .ToList(),
            system = new
            {
                os = specs.OsName,
                model = $"{specs.Manufacturer} {specs.Model}".Trim(),
                ramTotalBytes = specs.RamTotalBytes,
            },
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
