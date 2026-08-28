using System.Globalization;
using System.IO;
using System.Text;

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

public static class LogReplayService
{
    public static (LogReplayResult? Result, string? Error) Parse(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return (null, "File has no data rows.");

            var header = ParseCsvLine(lines[0]);
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
                var fields = ParseCsvLine(lines[i]);
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

    private static double ReadDouble(List<string> fields, int index)
        => index >= 0 && index < fields.Count && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Same small quoted-CSV line parser ScheduledTaskService already uses for schtasks' output -
    // this app's own LoggingService escapes with the identical rule (wrap in quotes, double an
    // embedded quote), so one hand-rolled parser covers both without a CSV library dependency.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
