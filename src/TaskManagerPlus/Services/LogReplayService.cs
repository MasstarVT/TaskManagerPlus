using System.Globalization;
using System.IO;
using System.Text;
using TaskManagerPlus.Common;

namespace TaskManagerPlus.Services;

/// <summary>One previously-logged row's headline figures, re-charted (#96) - a log file viewer
/// built into the app so a past logging session can be inspected without an external tool like
/// Excel. Only pulls out the handful of columns this app's own built-in chart can show
/// (Timestamp/CPU/RAM/Disk) by header name - it works on any CSV this app itself wrote, in any
/// column order, since LoggingViewModel's column set has changed release to release and a log
/// file from an older version may not have every column a current one does.
///
/// Round 20, item 95: TemperatureC/PowerW are the same best-effort by-header-name pull, added for
/// the Stability tab's crash-log correlation (CrashCorrelationService.BuildLogCorrelationAsync) -
/// null (not a list of zeros) when this particular log file has no sensor column of that kind at
/// all, since "no data" and "measured zero" mean different things to that feature. Column names are
/// dynamic per-machine sensor labels (LoggingViewModel.BuildHeaders), so this picks whichever
/// "(°C)"/"(W)" column's name contains "CPU" first, falling back to the first column of that unit
/// found at all - a package/die sensor is the most useful single temperature/power reading for "was
/// it hot / was it under load", and every machine has a different exact sensor name for it.</summary>
public sealed record LogReplayResult(
    List<DateTime> Timestamps,
    List<double> CpuPercent,
    List<double> RamPercent,
    List<double> DiskPercent,
    int RowCount,
    List<double>? TemperatureC = null,
    List<double>? PowerW = null);

/// <summary>#300: one column of a re-loaded #296/#299 flight-recorder incident CSV -
/// FlightRecorderService.Headers' exact column set, parsed by name the same way Parse() above
/// reads this app's regular logging CSVs.</summary>
public sealed record FlightRecorderReplayResult(
    List<DateTime> Timestamps, List<double> CpuPercent, List<double> MaxCoreDpcPercent,
    List<double> ProcessorQueueLength, List<double> HardFaultsPerSec, List<double?> FrameTimeMs,
    List<double?> InputDelayMs, List<string> ForegroundProcessName, List<string> ForegroundWindowTitle,
    List<string> TopProcessesText, int RowCount);

public static class LogReplayService
{
    public static (LogReplayResult? Result, string? Error) Parse(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return (null, "File has no data rows.");

            var header = CsvLine.Split(lines[0]);
            int Idx(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            int iTime = Idx("Timestamp"), iCpu = Idx("CPU Total (%)"), iRam = Idx("RAM (%)"), iDisk = Idx("Disk Active (%)");
            if (iTime < 0) return (null, "This doesn't look like a Task Manager Plus log file (no Timestamp column).");

            int iTemp = FindBestSensorColumn(header, "(°C)");
            int iPower = FindBestSensorColumn(header, "(W)");

            var timestamps = new List<DateTime>();
            var cpu = new List<double>();
            var ram = new List<double>();
            var disk = new List<double>();
            var temperature = iTemp >= 0 ? new List<double>() : null;
            var power = iPower >= 0 ? new List<double>() : null;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var fields = CsvLine.Split(lines[i]);
                if (fields.Count <= iTime) continue;
                if (!DateTime.TryParse(fields[iTime], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) continue;

                timestamps.Add(ts);
                cpu.Add(ReadDouble(fields, iCpu));
                ram.Add(ReadDouble(fields, iRam));
                disk.Add(ReadDouble(fields, iDisk));
                temperature?.Add(ReadDouble(fields, iTemp));
                power?.Add(ReadDouble(fields, iPower));
            }

            if (timestamps.Count == 0) return (null, "No valid data rows found.");

            return (new LogReplayResult(timestamps, cpu, ram, disk, timestamps.Count, temperature, power), null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't read that file: {ex.Message}");
        }
    }

    /// <summary>Item 95: picks the most useful column ending in the given unit suffix (e.g.
    /// "(°C)") - preferring one whose sensor name mentions "CPU" (a package/die reading is the
    /// single most relevant one for "was it hot"), falling back to the first match of that unit at
    /// all. -1 when this file has no column of that unit.</summary>
    private static int FindBestSensorColumn(List<string> header, string unitSuffix)
    {
        int firstMatch = -1;
        for (int i = 0; i < header.Count; i++)
        {
            if (!header[i].EndsWith(unitSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (firstMatch < 0) firstMatch = i;
            if (header[i].Contains("CPU", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return firstMatch;
    }

    /// <summary>
    /// #300: "Built on the existing LogReplayService chart-replay code" - extended here (a second
    /// parse method reusing CsvLine.Split/the same by-header-name column lookup as Parse() above)
    /// rather than reusing Parse() literally: that method's return shape (Timestamp/CPU/RAM/Disk)
    /// is a fixed 4-column CSV chart shape tied to this app's regular logging CSV, and #296's
    /// flight-recorder ring buffer is a genuinely different schema (DPC%, queue length, hard
    /// faults, frame time, input delay, foreground app, top processes). Reusing the CSV-parsing
    /// convention (quoted-field escaping, tolerant column lookup by header name so an older export
    /// still loads) is the practical/honest level of reuse here - see FlightRecorderService.ToCsv
    /// for the writer side this reads back.
    /// </summary>
    public static (FlightRecorderReplayResult? Result, string? Error) ParseFlightRecorderCsv(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return (null, "File has no data rows.");

            var header = CsvLine.Split(lines[0]);
            int Idx(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            int iTime = Idx("Timestamp"), iCpu = Idx("CPU (%)"), iDpc = Idx("Max core DPC (%)"),
                iQueue = Idx("Processor queue length"), iFaults = Idx("Hard faults/sec"),
                iFrame = Idx("Frame time (ms)"), iInput = Idx("Input delay (ms)"),
                iFgProc = Idx("Foreground process"), iFgTitle = Idx("Foreground window title"),
                iTop = Idx("Top processes");
            if (iTime < 0) return (null, "This doesn't look like a flight-recorder incident CSV (no Timestamp column).");

            var timestamps = new List<DateTime>();
            var cpu = new List<double>(); var dpc = new List<double>(); var queue = new List<double>();
            var faults = new List<double>(); var frame = new List<double?>(); var input = new List<double?>();
            var fgProc = new List<string>(); var fgTitle = new List<string>(); var top = new List<string>();

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var fields = CsvLine.Split(lines[i]);
                if (fields.Count <= iTime) continue;
                if (!DateTime.TryParse(fields[iTime], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) continue;

                timestamps.Add(ts);
                cpu.Add(ReadDouble(fields, iCpu));
                dpc.Add(ReadDouble(fields, iDpc));
                queue.Add(ReadDouble(fields, iQueue));
                faults.Add(ReadDouble(fields, iFaults));
                frame.Add(ReadNullableDouble(fields, iFrame));
                input.Add(ReadNullableDouble(fields, iInput));
                fgProc.Add(ReadString(fields, iFgProc));
                fgTitle.Add(ReadString(fields, iFgTitle));
                top.Add(ReadString(fields, iTop));
            }

            if (timestamps.Count == 0) return (null, "No valid data rows found.");

            return (new FlightRecorderReplayResult(timestamps, cpu, dpc, queue, faults, frame, input, fgProc, fgTitle, top, timestamps.Count), null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't read that file: {ex.Message}");
        }
    }

    private static string ReadString(List<string> fields, int index) => index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    private static double? ReadNullableDouble(List<string> fields, int index)
        => index >= 0 && index < fields.Count && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double ReadDouble(List<string> fields, int index)
        => index >= 0 && index < fields.Count && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Same small quoted-CSV line parser ScheduledTaskService already uses for schtasks' output -
    // this app's own LoggingService escapes with the identical rule (wrap in quotes, double an
    // embedded quote), so one hand-rolled parser covers both without a CSV library dependency.
}
