using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #665/#667: USB-specific event-log diagnostics - over-current/port-reset-failure records (#665)
/// and a per-device surprise-removal/re-arrival count (#667). Kept as its own small service
/// (rather than folded into the already-large EventLogService, which backs the Stability tab's
/// crash/reliability history) since this is a genuinely different subject area, following this
/// app's existing convention of each shelled-out/event-log-reading service owning its own small
/// helpers rather than a single do-everything class.
///
/// Both reads are keyword-matched against the formatted event message rather than filtered by a
/// specific numeric EventID. Every other undocumented-format event scan in this app
/// (EventLogService.ReadThermalCriticalEvents is the closest precedent) already makes this same
/// choice for exactly this reason: the precise event IDs Windows uses for USB over-current/port-
/// reset-failure and PnP surprise-removal are not published, versioned Microsoft constants, and
/// they are known to vary by driver/Windows build in a way the *wording* of the formatted message
/// does not. A keyword scan that finds nothing on a given system degrades to an honest "none
/// found" (the same outcome a wrong hardcoded EventID would produce anyway) rather than risking a
/// silently-wrong ID that happens to compile but never matches anything real.
/// </summary>
public static class UsbEventLogService
{
    private const int LookbackDays = 14;
    private const int MaxEventsPerQuery = 500;

    // Confirmed real, stable provider names: the modern USB 3.x hub class driver's own operational
    // trace log, and the legacy USB 2.0 hub driver's classic System-log source name.
    private const string UsbHub3OperationalLog = "Microsoft-Windows-USB-USBHUB3/Operational";
    private const string LegacyUsbHubSource = "usbhub";

    private static readonly string[] OverCurrentKeywords =
    {
        "over-current", "overcurrent", "over current", "exceeded the power", "power limit", "power limits",
    };

    private static readonly string[] PortResetKeywords =
    {
        "reset failed", "failed to reset", "port reset", "reset error",
    };

    private static readonly string[] RemovalKeywords =
    {
        "surprise removal", "without first being prepared for removal", "unexpectedly removed", "disappeared from the system",
    };

    private static readonly string[] ArrivalKeywords =
    {
        "device arrival", "new device was detected", "device was recognized", "device was configured",
    };

    // Most Kernel-PnP/usbhub/disk diagnostic messages embed the full device instance path
    // ("USB\VID_xxxx&PID_xxxx\..."). Extracting it lets these events be joined back onto
    // UsbPowerService's own PNPDeviceID-keyed device list by the same normalized-prefix technique
    // UsbPowerService.Normalize already established, without needing a second, separate lookup.
    private static readonly Regex UsbInstanceIdRegex = new(@"USB\\[A-Za-z0-9_&]+\\[^\s,;""']+", RegexOptions.Compiled);

    /// <summary>#665: over-current and port-reset-failure records from the USBHUB3 operational log
    /// plus the legacy System-log usbhub source.</summary>
    public static List<UsbPowerEvent> ReadOverCurrentEvents()
    {
        var keywords = OverCurrentKeywords.Concat(PortResetKeywords).ToArray();
        var result = new List<UsbPowerEvent>();
        result.AddRange(ReadLogByKeyword(UsbHub3OperationalLog, PathType.LogName, null, keywords));
        result.AddRange(ReadLogByKeyword("System", PathType.LogName, LegacyUsbHubSource, keywords));
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    /// <summary>#667: counts surprise-removal + device-arrival events per normalized USB instance
    /// ID prefix over the lookback window - the raw per-event list plus a lookup dictionary keyed
    /// the same way UsbPowerService.Normalize keys its own MSPower_DeviceEnable join, so
    /// EnergyThermalsViewModel can attach a count onto each row of the existing USB device grid.</summary>
    public static (List<UsbPowerEvent> Events, Dictionary<string, int> CountsByNormalizedInstance) ReadReenumerationEvents()
    {
        var keywords = RemovalKeywords.Concat(ArrivalKeywords).ToArray();
        var events = new List<UsbPowerEvent>();
        events.AddRange(ReadLogByKeyword("System", PathType.LogName, "Microsoft-Windows-Kernel-PnP", keywords));
        events.AddRange(ReadLogByKeyword("System", PathType.LogName, "disk", keywords));

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e.DeviceIdHint.Length == 0) continue;
            string normalized = Normalize(e.DeviceIdHint);
            counts[normalized] = counts.TryGetValue(normalized, out var c) ? c + 1 : 1;
        }

        return (events.OrderByDescending(e => e.TimeCreated).ToList(), counts);
    }

    private static List<UsbPowerEvent> ReadLogByKeyword(string logName, PathType pathType, string? providerFilter, string[] keywords)
    {
        var result = new List<UsbPowerEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string filter = providerFilter is null
                ? $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]"
                : $"*[System[Provider[@Name='{providerFilter}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";

            var query = new EventLogQuery(logName, pathType, filter) { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxEventsPerQuery && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { continue; } // can't keyword-match without the formatted message

                    if (!keywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

                    var idMatch = UsbInstanceIdRegex.Match(message);
                    result.Add(new UsbPowerEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        ProviderName = record.ProviderName ?? string.Empty,
                        DeviceIdHint = idMatch.Success ? idMatch.Value : string.Empty,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Log/provider unavailable (operational log not enabled, older Windows build, access
            // denied, ...) - degrade to "none found from this source", same as every other
            // event-log read in this app.
        }
        return result;
    }

    private static string Normalize(string id) => id.ToLowerInvariant().Replace('\\', '_').Replace(' ', '_');

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
