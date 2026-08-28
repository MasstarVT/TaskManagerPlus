using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #290: Windows Update/servicing on-demand activity read - Microsoft-Windows-WindowsUpdateClient/
/// Operational scan/install events, so a long CBS servicing pass is named rather than left as an
/// anonymous busy disk. The cheap wuauserv service-state + worker-process cost read lives in
/// BackgroundActivityService.ReadWindowsUpdateProcessState instead, since that part is cheap enough
/// for the 2s light tick.
/// </summary>
public static class WindowsUpdateActivityService
{
    private const string OperationalLog = "Microsoft-Windows-WindowsUpdateClient/Operational";
    private const int MaxRows = 40;

    public static async Task<WindowsUpdateEventResult> ReadRecentActivityAsync(TimeSpan window) => await Task.Run(() =>
    {
        var rows = new List<WindowsUpdateEventRow>();
        try
        {
            long maxAgeMs = (long)window.TotalMilliseconds;
            var query = new EventLogQuery(OperationalLog, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && rows.Count < MaxRows && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    rows.Add(new WindowsUpdateEventRow
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        Description = message.Length > 200 ? message[..200] + "…" : message,
                    });
                }
            }

            return new WindowsUpdateEventResult
            {
                IsAvailable = true,
                StatusText = rows.Count == 0
                    ? "No Windows Update client events found in the lookback window."
                    : $"{rows.Count} Windows Update client event(s) in the lookback window.",
                RecentEvents = rows,
            };
        }
        catch (Exception ex)
        {
            return new WindowsUpdateEventResult
            {
                IsAvailable = false,
                StatusText = $"{OperationalLog} isn't available on this system, or the read failed ({ex.Message}).",
            };
        }
    });
}
