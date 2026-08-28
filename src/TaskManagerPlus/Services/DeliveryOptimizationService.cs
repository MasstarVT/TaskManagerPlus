using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #289: Delivery Optimization on-demand activity read - Microsoft-Windows-DeliveryOptimization/
/// Operational event volume, used as a proxy for "how much has DO been doing lately" since a live
/// transfer-volume number (bytes currently in flight) isn't cheaply readable without the DO COM/
/// PowerShell API (Get-DeliveryOptimizationStatus), a much heavier ask than this app's other
/// event-log reads. The cheap service-state + DODownloadMode policy read lives in
/// BackgroundActivityService.ReadDeliveryOptimization instead, since that part is cheap enough for
/// the 2s light tick.
/// </summary>
public static class DeliveryOptimizationService
{
    private const string OperationalLog = "Microsoft-Windows-DeliveryOptimization/Operational";

    public static async Task<DeliveryOptimizationEventResult> ReadRecentActivityAsync(TimeSpan window) => await Task.Run(() =>
    {
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
            DateTime? last = null;
            const int maxEvents = 2000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    last ??= record.TimeCreated;
                }
            }

            return new DeliveryOptimizationEventResult
            {
                IsAvailable = true,
                StatusText = count == 0
                    ? "No Delivery Optimization events found in the lookback window."
                    : $"{count} Delivery Optimization event(s) in the lookback window (most recent: {last:T}). This is event-volume-as-a-proxy, not a live transfer-byte-count - see the card note for why.",
                RecentEventCount = count,
                LastEventTime = last,
            };
        }
        catch (Exception ex)
        {
            return new DeliveryOptimizationEventResult
            {
                IsAvailable = false,
                StatusText = $"{OperationalLog} isn't available on this system, or the read failed ({ex.Message}).",
            };
        }
    });
}
