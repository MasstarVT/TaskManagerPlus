using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads the System and Application event logs for crash/stability diagnostics (#1/#3/#4/#5/#6/#8) -
/// the same source Windows' own Reliability Monitor and Event Viewer read from, just filtered down
/// to what's actually actionable for "why did my PC crash/hang/reboot". Every read is wrapped to
/// degrade to "nothing found" rather than throwing: a locked-down policy, a cleared log, or a
/// provider whose message file isn't registered on this machine are all real, expected conditions,
/// not bugs.
/// </summary>
public sealed class EventLogService
{
    private const int LookbackDays = 30;
    private const int MaxEventsPerLog = 60;

    // Kernel-Power 41 = "The system has rebooted without cleanly shutting down first" - the
    // classic signal WhoCrashed/Reliability Monitor use for "this was a real crash". EventLog
    // 6008 is the older, plain-text version of the same event.
    private const int KernelPowerEventId = 41;
    private const int LegacyUncleanShutdownEventId = 6008;
    private const int TdrEventId = 4101;

    // Windows Error Reporting's own "a Blue Screen just happened" summary entry (Application log).
    private const int BlueScreenEventId = 1001;

    private static readonly Regex FaultingModuleRegex = new(@"Faulting module name:\s*([^,\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // #633: "Exception code: 0xNNNNNNNN" from a Windows Error Reporting "Application Error" entry -
    // the other half of the undervolt/overclock instability hint (alongside FaultingModule above).
    private static readonly Regex ExceptionCodeRegex = new(@"Exception code:\s*(0x[0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public StabilitySnapshot Query()
    {
        var events = new List<StabilityEvent>();
        ReadLog("System", events);
        ReadLog("Application", events);
        events = events.OrderByDescending(e => e.TimeCreated).Take(MaxEventsPerLog * 2).ToList();

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var approxLastBoot = DateTime.Now - uptime;

        var shutdownEvents = events
            .Where(e => e.LogName == "System" && e.EventId is KernelPowerEventId or LegacyUncleanShutdownEventId)
            .OrderByDescending(e => e.TimeCreated)
            .ToList();
        var mostRecentShutdownEvent = shutdownEvents.FirstOrDefault();
        bool wasUnexpected = mostRecentShutdownEvent is not null &&
            Math.Abs((mostRecentShutdownEvent.TimeCreated - approxLastBoot).TotalMinutes) < 5;

        var tdrEvents = events.Where(e => e.EventId == TdrEventId).OrderByDescending(e => e.TimeCreated).ToList();

        var crashLikeIds = new HashSet<int> { KernelPowerEventId, LegacyUncleanShutdownEventId, BlueScreenEventId };
        var lastCrash = events.Where(e => crashLikeIds.Contains(e.EventId)).OrderByDescending(e => e.TimeCreated).FirstOrDefault();

        var (lowMemCount, lowMemLast) = ReadLowMemoryEvents();

        return new StabilitySnapshot
        {
            RecentEvents = events.Take(MaxEventsPerLog).ToList(),
            WasLastShutdownUnexpected = wasUnexpected,
            LastUnexpectedShutdown = mostRecentShutdownEvent?.TimeCreated,
            TdrEventCount = tdrEvents.Count,
            LastTdrEvent = tdrEvents.FirstOrDefault()?.TimeCreated,
            LastCrashTime = lastCrash?.TimeCreated,
            Minidumps = ReadMinidumps(shutdownEvents),
            DailyCounts = BuildDailyCounts(events),
            LowMemoryEventCount = lowMemCount,
            LastLowMemoryEvent = lowMemLast,
            ThermalCriticalEvents = ReadThermalCriticalEvents(),
            LastUnexpectedShutdownBugcheckCode = mostRecentShutdownEvent?.BugcheckCode,
        };
    }

    // #602: Kernel-Processor-Power event 37 ("the speed of processor N is being limited by system
    // firmware... for X seconds") and its 38 recovery counterpart - the one authoritative,
    // non-heuristic statement Windows itself makes about firmware-side CPU throttling.
    private const string ProcessorPowerProvider = "Microsoft-Windows-Kernel-Processor-Power";
    private const int FirmwareThrottleEventId = 37;
    private const int FirmwareThrottleRecoveryEventId = 38;

    public List<FirmwareThrottleEvent> ReadFirmwareThrottleEvents()
    {
        var result = new List<FirmwareThrottleEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{ProcessorPowerProvider}'] and (EventID={FirmwareThrottleEventId} or EventID={FirmwareThrottleRecoveryEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    result.Add(new FirmwareThrottleEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        IsRecovery = record.Id == FirmwareThrottleRecoveryEventId,
                        Message = Truncate(message, 240),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable (older Windows builds don't log this provider at all) -
            // degrade to "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #606: the "a thermal zone exceeded its critical/passive trip point" family, plus ACPI
    // thermal-shutdown records - matched by provider + a message keyword rather than a hardcoded
    // event ID, since IDs for this family vary by Windows build (unlike Kernel-Power 41, which is
    // stable across every build this app targets).
    private static readonly string[] ThermalCriticalProviders =
    {
        "Microsoft-Windows-Kernel-Power",
        "Microsoft-Windows-Kernel-Acpi",
        "ACPI",
    };

    private static readonly string[] ThermalCriticalKeywords =
    {
        "thermal zone", "critical temperature", "critical trip point", "thermal shutdown", "thermal event",
    };

    public List<StabilityEvent> ReadThermalCriticalEvents()
    {
        var result = new List<StabilityEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var providerFilter = string.Join(" or ", ThermalCriticalProviders.Select(p => $"Provider[@Name='{p}']"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[({providerFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500; // Kernel-Power alone can log a lot of unrelated entries over 30 days
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { continue; } // can't keyword-match without the formatted message

                    if (!ThermalCriticalKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    result.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = "System",
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #636: WHEA-Logger (Windows Hardware Error Architecture) events - the app's first WHEA
    // surface. Event 1 is a fatal/uncorrected machine check; 17 is a corrected machine check;
    // 18/19/20 are corrected platform/PCIe errors; 47 is a corrected memory error. IDs are stable
    // across the Windows versions this app targets (unlike the thermal-critical family above,
    // which varies by build), so this is filtered by EventID the same way ReadFirmwareThrottleEvents
    // filters Kernel-Processor-Power 37/38.
    private const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    private static readonly int[] WheaEventIds = { 1, 17, 18, 19, 20, 47 };

    private static readonly Regex WheaErrorSourceRegex = new(@"Error Source:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaErrorTypeRegex = new(@"Error Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaBankRegex = new(@"Bank\s*(?:Number)?:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaBusDeviceFunctionRegex = new(
        @"Bus:Device:Function:\s*(?:0x)?([0-9A-Fa-f]+):(?:0x)?([0-9A-Fa-f]+):(?:0x)?([0-9A-Fa-f]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaSegmentRegex = new(@"Segment:\s*(?:0x)?([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#636: parses WHEA-Logger System-log events into a typed list - error source, bank
    /// (machine-check events), and PCIe segment/bus/device/function (platform/PCIe events) where
    /// the formatted message contains them. Every field not found in the message is left null/
    /// empty rather than guessed - WHEA-Logger's message layout is exactly as undocumented/
    /// unversioned as Kernel-Power 41's insertion strings (see ExtractBugcheckCode's remarks).</summary>
    public List<WheaEvent> ReadWheaEvents()
    {
        var result = new List<WheaEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var idFilter = string.Join(" or ", WheaEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{WheaProvider}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    string errorSource = WheaErrorSourceRegex.Match(message) is { Success: true } srcMatch
                        ? srcMatch.Groups[1].Value.Trim() : string.Empty;
                    string errorType = WheaErrorTypeRegex.Match(message) is { Success: true } typeMatch
                        ? typeMatch.Groups[1].Value.Trim() : string.Empty;

                    int? bank = WheaBankRegex.Match(message) is { Success: true } bankMatch && int.TryParse(bankMatch.Groups[1].Value, out var b)
                        ? b : null;

                    int? segment = null, bus = null, device = null, function = null;
                    var bdf = WheaBusDeviceFunctionRegex.Match(message);
                    if (bdf.Success &&
                        int.TryParse(bdf.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var busVal) &&
                        int.TryParse(bdf.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var devVal) &&
                        int.TryParse(bdf.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var funcVal))
                    {
                        bus = busVal; device = devVal; function = funcVal;
                        var segMatch = WheaSegmentRegex.Match(message);
                        if (segMatch.Success && int.TryParse(segMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var segVal))
                            segment = segVal;
                    }

                    result.Add(new WheaEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        IsFatal = record.Id == 1,
                        CategoryText = DescribeWheaCategory(record.Id),
                        ErrorSourceText = errorSource,
                        Bank = bank,
                        BankHintText = MceBankHintLookup.Describe(string.IsNullOrEmpty(errorType) ? errorSource : errorType),
                        PcieSegment = segment,
                        PcieBus = bus,
                        PcieDevice = device,
                        PcieFunction = function,
                        Message = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable (no WHEA-capable hardware ever logged anything, or the
            // provider isn't registered on this Windows build) - degrade to "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static string DescribeWheaCategory(int eventId) => eventId switch
    {
        1 => "Fatal machine check",
        17 => "Corrected machine check",
        18 => "Corrected platform error",
        19 => "Corrected PCIe error",
        20 => "Corrected platform error",
        47 => "Corrected memory error",
        _ => "WHEA event",
    };

    // #626: Kernel-Power event 105 - AC/DC power-source transitions. A stable, well-known event ID
    // commonly used for exactly this purpose (e.g. Task Scheduler's built-in "on AC/DC power
    // change" triggers), unlike the thermal-critical family above.
    private const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    private const int PowerSourceChangeEventId = 105;

    /// <summary>#626: reads Kernel-Power 105 (AC/DC power-source change) events - the raw list;
    /// EnergyThermalsViewModel derives the "N times in the last hour" flapping count from these
    /// timestamps.</summary>
    // #690: Microsoft-Windows-Kernel-PnP is the provider Windows logs every device arrival/removal/
    // configuration event under (not just monitors) - there's no separate, monitor-only event id,
    // so this is scoped down after the fact by matching the event's own formatted message text for
    // "monitor"/"display" (best-effort, same tier as GpuRegistryService's GpuVendorHints text
    // match), rather than a documented monitor-specific event id this app could filter on directly.
    private const string KernelPnpProvider = "Microsoft-Windows-Kernel-PnP";

    /// <summary>#690: Kernel-PnP device arrival/removal/configuration events whose message mentions
    /// a monitor/display, last 30 days - the event-log half of the display connect/disconnect
    /// history (the other half, WM_DISPLAYCHANGE, is captured live in-app by MainWindow and doesn't
    /// need an event-log read at all). Returns an empty list (never throws) when the provider/log
    /// is unavailable or nothing matches.</summary>
    public List<DisplayChangeEvent> ReadMonitorPnpEvents()
    {
        var result = new List<DisplayChangeEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{KernelPnpProvider}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxScanned = 400; // this provider is chatty (every PnP device, not just monitors) - cap the scan, not just the match count
            while (count < maxScanned && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    if (message.Length == 0) continue;
                    if (!message.Contains("monitor", StringComparison.OrdinalIgnoreCase) &&
                        !message.Contains("display", StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new DisplayChangeEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Source = "Kernel-PnP",
                        Description = Truncate(message, 200),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or a policy denies event-log read even elevated - degrade
            // to "no PnP-sourced display history" (WM_DISPLAYCHANGE-sourced entries still show).
        }
        return result;
    }

    public List<PowerSourceChangeEvent> ReadPowerSourceChangeEvents()
    {
        var result = new List<PowerSourceChangeEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{KernelPowerProvider}'] and (EventID={PowerSourceChangeEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    result.Add(new PowerSourceChangeEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Message = Truncate(message, 200),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or (the common desktop case) this event simply never
            // fires because there's no battery/AC adapter to transition between - degrade to
            // "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #654: Kernel-Power event 107 (plain resume marker, no source attribution) and
    // Microsoft-Windows-Power-Troubleshooter event 1 (richer - "Sleep Time:"/"Wake Time:"/"Wake
    // Source:" insertion strings inside the formatted message) - confirmed live on a real dev
    // machine that both log to the System log (not a separate Operational log for the
    // Power-Troubleshooter provider), with event 1's message reading "The system has returned from
    // a low power state." followed by those three labeled lines. See WakeHistoryService for how
    // these are merged with `powercfg /lastwake` into one wake-history table.
    private const string PowerTroubleshooterProvider = "Microsoft-Windows-Power-Troubleshooter";
    private const int ResumeEventId107 = 107; // Kernel-Power
    private const int WakeSourceEventId1 = 1; // Power-Troubleshooter

    private static readonly Regex SleepTimeRegex = new(@"Sleep Time:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WakeTimeRegex = new(@"Wake Time:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WakeSourceRegex = new(@"Wake Source:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<WakeHistoryEntry> ReadWakeHistoryEvents()
    {
        var result = new List<WakeHistoryEntry>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[((Provider[@Name='{KernelPowerProvider}'] and EventID={ResumeEventId107}) or " +
                $"(Provider[@Name='{PowerTroubleshooterProvider}'] and EventID={WakeSourceEventId1})) and " +
                $"TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } timeCreated) continue;

                    if (record.Id == ResumeEventId107 && record.ProviderName == KernelPowerProvider)
                    {
                        result.Add(new WakeHistoryEntry
                        {
                            SleepTime = null,
                            WakeTime = timeCreated,
                            WakeSource = string.Empty,
                            RecordSource = "Kernel-Power 107",
                        });
                        continue;
                    }

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    DateTime? sleepTime = SleepTimeRegex.Match(message) is { Success: true } sleepMatch &&
                        BatteryReportService.TryParseFlexibleDate(sleepMatch.Groups[1].Value, out var st) ? st : null;
                    string wakeSource = WakeSourceRegex.Match(message) is { Success: true } sourceMatch
                        ? sourceMatch.Groups[1].Value.Trim() : string.Empty;

                    // "Wake Time:" in the message is the authoritative wake timestamp when present
                    // (it can differ slightly from the event's own TimeCreated); fall back to
                    // TimeCreated when the message couldn't be formatted at all.
                    DateTime wakeTime = WakeTimeRegex.Match(message) is { Success: true } wakeMatch &&
                        BatteryReportService.TryParseFlexibleDate(wakeMatch.Groups[1].Value, out var wt) ? wt : timeCreated;

                    result.Add(new WakeHistoryEntry
                    {
                        SleepTime = sleepTime,
                        WakeTime = wakeTime,
                        WakeSource = wakeSource,
                        RecordSource = "Power-Troubleshooter",
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.WakeTime).ToList();
    }

    // #656: Kernel-Power event 42 - "The system is entering sleep." A 42 with no matching resume
    // (or one immediately followed by a Power-Troubleshooter event-1 resume within seconds) is the
    // failed-sleep/vetoed-transition signal SleepVetoService.Correlate looks for.
    private const int SleepEntryEventId42 = 42;

    public List<DateTime> ReadSleepEntryEvents()
    {
        var result = new List<DateTime>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{KernelPowerProvider}'] and (EventID={SleepEntryEventId42}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is { } t) result.Add(t);
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(t => t).ToList();
    }

    // ================================================================================
    // #670/#677: GPU driver timeout/reset (TDR) detail, DXGI device-removed crashes, and the
    // "unrecovered reset" correlation against a Kernel-Power 41 bugcheck - all System/Application
    // log reads, same on-demand shape as every query above. TdrEventId (4101) above already backs
    // the flat StabilitySnapshot.TdrEventCount; these read the same event family for actual detail
    // rather than duplicating that counting query.
    // ================================================================================

    // 4101 = "Display driver X stopped responding and has successfully recovered." 4104 is the
    // vendor/DXGKRNL-side sibling some driver stacks log alongside it - both come from the
    // "Display" provider (not a vendor-specific one; the driver module name lives in the message
    // text, not the provider name).
    private const string DisplayProvider = "Display";
    private const int TdrRecoveredEventId4104 = 4104;

    private static readonly string[] KnownGpuDriverModules =
    {
        "nvlddmkm", "amdkmdag", "amdkmdap", "atikmdag", "atikmpag", "igdkmd64", "igdkmdn64", "igfxdrv",
    };

    private static readonly Regex TdrDriverNameRegex = new(
        @"[Dd]isplay driver\s+(\S+?)(\.sys)?\s+stopped responding", RegexOptions.Compiled);

    /// <summary>#670: parses the driver module name and recovery outcome out of each TDR event's
    /// own formatted message - see GpuTdrEvent's remarks for why both are best-effort. Naming the
    /// module is what makes a TDR actionable at a glance instead of just a count.</summary>
    public List<GpuTdrEvent> ReadGpuTdrEvents()
    {
        var result = new List<GpuTdrEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={TdrEventId} or EventID={TdrRecoveredEventId4104}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    string module = ExtractGpuDriverModule(message);
                    bool? recovered = message.Length == 0 ? null
                        : message.Contains("successfully recovered", StringComparison.OrdinalIgnoreCase) ? true
                        : message.Contains("stopped responding", StringComparison.OrdinalIgnoreCase) ? false
                        : null;

                    result.Add(new GpuTdrEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        DriverModule = module,
                        Recovered = recovered,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static string ExtractGpuDriverModule(string message)
    {
        var match = TdrDriverNameRegex.Match(message);
        if (match.Success) return match.Groups[1].Value;

        // Fall back to a plain substring search for a known module name anywhere in the message -
        // covers vendor-specific 4104 wording this app hasn't seen an exact "stopped responding"
        // phrasing for.
        foreach (var known in KnownGpuDriverModules)
            if (message.Contains(known, StringComparison.OrdinalIgnoreCase)) return known;

        return "Unknown";
    }

    // #677: Windows Error Reporting logs an "Application Error" (event 1000) to the Application log
    // for an unhandled crash; a renderer/game that lost its GPU device mid-frame typically names the
    // DXGI_ERROR_DEVICE_REMOVED HRESULT (0x887A0005) directly in that entry's own formatted text -
    // matched by keyword the same way ReadThermalCriticalEvents matches its own message family,
    // since there's no dedicated stable event ID for this specific HRESULT.
    private static readonly string[] DeviceRemovedKeywords = { "DXGI_ERROR_DEVICE_REMOVED", "887a0005" };
    private static readonly Regex FaultingAppNameRegex = new(@"Faulting application name:\s*([^,\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<GpuDeviceRemovedEvent> ReadGpuDeviceRemovedEvents()
    {
        var result = new List<GpuDeviceRemovedEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 2000; // Application log is noisy - keyword-filtered below, so a generous scan cap
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { continue; } // can't keyword-match without the formatted message

                    if (!DeviceRemovedKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string processName = FaultingAppNameRegex.Match(message) is { Success: true } m
                        ? m.Groups[1].Value.Trim() : string.Empty;

                    result.Add(new GpuDeviceRemovedEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        ProviderName = record.ProviderName ?? string.Empty,
                        ProcessName = processName,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #677: a TDR/device-removed event counts as "unrecovered" when a Kernel-Power 41 bugcheck
    // naming 0x116 (VIDEO_TDR_ERROR) or 0x117 (VIDEO_TDR_TIMEOUT_DETECTED) landed within a few
    // minutes of it - the same "nearest event within a short window" correlation ReadMinidumps
    // already uses for minidump-to-bugcheck pairing. This needs its own full-window Kernel-Power 41
    // scan (not RecentEvents, which is capped at 120 total events across both logs and could miss
    // an older 41 entirely on a busy machine).
    private static readonly string[] GpuTdrBugcheckSuffixes = { "116", "117" };

    private List<StabilityEvent> ReadKernelPower41BugcheckEvents()
    {
        var result = new List<StabilityEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={KernelPowerEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    result.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = "System",
                        EventId = record.Id,
                        BugcheckCode = ExtractBugcheckCode(record),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no bugcheck correlation available".
        }
        return result;
    }

    /// <summary>#677: composes ReadGpuTdrEvents/ReadGpuDeviceRemovedEvents with the unrecovered-
    /// reset correlation above into one snapshot for the GPU tab's event list.</summary>
    public GpuResetSummary ReadGpuResetSummary()
    {
        var tdrEvents = ReadGpuTdrEvents();
        var deviceRemoved = ReadGpuDeviceRemovedEvents();
        var bugchecks = ReadKernelPower41BugcheckEvents()
            .Where(e => e.BugcheckCode is not null &&
                        GpuTdrBugcheckSuffixes.Any(s => e.BugcheckCode!.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int unrecovered = 0;
        if (bugchecks.Count > 0)
        {
            var resetTimestamps = tdrEvents.Select(e => e.TimeCreated).Concat(deviceRemoved.Select(e => e.TimeCreated));
            unrecovered = resetTimestamps.Count(t => bugchecks.Any(b => Math.Abs((b.TimeCreated - t).TotalMinutes) < 10));
        }

        return new GpuResetSummary
        {
            TdrEvents = tdrEvents,
            DeviceRemovedEvents = deviceRemoved,
            UnrecoveredResetCount = unrecovered,
        };
    }

    // #673: broader set of GPU-related crash-module hints than KnownGpuDriverModules above - a
    // driver-crash-to-version correlation needs application-side GPU user-mode DLL crashes too
    // (OpenGL/D3D user-mode drivers), not just the kernel-mode TDR module names.
    private static readonly string[] GpuCrashModuleHints =
    {
        "nvlddmkm", "nvoglv", "nvwgf2um", "nvcuda", "nvd3dum",
        "amdkmdag", "amdkmdap", "atidxx", "atiumd", "aticfx", "atig6txx",
        "atikmdag", "atikmpag",
        "igdkmd", "igdumdim", "igd10iumd", "igdusc",
        "dxgkrnl",
    };

    /// <summary>#673: Application-log Level 1/2 crash events whose faulting module names a known
    /// GPU driver component - the same FaultingModule extraction ReadLog already performs for the
    /// general Recent Events grid, re-queried here on its own (full lookback window, not the
    /// general 120-event cap) so a driver-version bucket count isn't silently short on a busy
    /// machine.</summary>
    public List<StabilityEvent> ReadGpuDriverCrashEvents()
    {
        var result = new List<StabilityEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 2000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { continue; }

                    string? module = ExtractFaultingModule(message);
                    if (module is null || !GpuCrashModuleHints.Any(h => module.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    result.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = "Application",
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        FaultingModule = module,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // Round 8 #40: low-memory resource-exhaustion events are logged by a dedicated Windows
    // component at Warning level, not Critical/Error - outside the Level=1|2 filter the main scan
    // above uses - so this is a second, separately targeted query for just this one provider,
    // the same shape ReadServiceStartDurations below already uses for a different provider/event.
    private const string ResourceExhaustionProvider = "Microsoft-Windows-Resource-Exhaustion-Detector";

    private static (int Count, DateTime? Last) ReadLowMemoryEvents()
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{ResourceExhaustionProvider}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            DateTime? last = null;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    last ??= record.TimeCreated;
                }
            }
            return (count, last);
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
            return (0, null);
        }
    }

    /// <summary>#1: Reliability History - buckets the same events already read into one count per
    /// calendar day across the full lookback window, zero-filled for days with no Critical/Error
    /// entries at all (Reliability Monitor's own chart shows the flat baseline too, not just spikes).</summary>
    private static List<DailyEventCount> BuildDailyCounts(List<StabilityEvent> events)
    {
        var counts = events
            .GroupBy(e => e.TimeCreated.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<DailyEventCount>();
        var today = DateTime.Now.Date;
        for (int i = LookbackDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            result.Add(new DailyEventCount { Date = day, Count = counts.TryGetValue(day, out var c) ? c : 0 });
        }
        return result;
    }

    private static void ReadLog(string logName, List<StabilityEvent> into)
    {
        try
        {
            // Level 1 = Critical, Level 2 = Error - Warning/Information would dominate the list
            // with noise that isn't actionable for a crash/stability investigation.
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerLog && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    into.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = logName,
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = Truncate(message, 300),
                        FaultingModule = ExtractFaultingModule(message),
                        ExceptionCode = ExtractExceptionCode(message),
                        BugcheckCode = record.Id == KernelPowerEventId ? ExtractBugcheckCode(record) : null,
                    });
                }
            }
        }
        catch
        {
            // Log unavailable/access denied/doesn't exist - contribute nothing from this log.
        }
    }

    private static string? ExtractFaultingModule(string message)
    {
        var match = FaultingModuleRegex.Match(message);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractExceptionCode(string message)
    {
        var match = ExceptionCodeRegex.Match(message);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Event 41's insertion strings put the bugcheck code first (as a hex-formatted number, e.g.
    /// "0x0000009f") on every Windows version this app targets - not a documented, versioned
    /// contract, so this is wrapped to return null (shown as "Unknown") rather than a
    /// misleading value if the layout ever changes.
    /// </summary>
    private static string? ExtractBugcheckCode(EventRecord record)
    {
        try
        {
            if (record.Properties.Count == 0) return null;
            var raw = record.Properties[0].Value;
            ulong code = Convert.ToUInt64(raw);
            return code == 0 ? null : $"0x{code:X8}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Correlates each minidump file's timestamp with the nearest Kernel-Power 41 event
    /// (within 10 minutes) to recover its bugcheck code (#3) - parsing the bugcheck code directly
    /// out of the .dmp binary format would need a full MINIDUMP-stream reader, a much larger and
    /// more fragile undertaking than reusing the event log entry already read for the shutdown
    /// banner above.</summary>
    private static List<MinidumpInfo> ReadMinidumps(List<StabilityEvent> shutdownEvents)
    {
        var dumps = new List<MinidumpInfo>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (!Directory.Exists(dir)) return dumps;

            var bugchecks = shutdownEvents.Where(e => e.EventId == KernelPowerEventId).ToList();

            foreach (var file in Directory.GetFiles(dir, "*.dmp"))
            {
                var info = new FileInfo(file);
                var nearest = bugchecks
                    .Where(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes) < 10)
                    .OrderBy(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes))
                    .FirstOrDefault();

                dumps.Add(new MinidumpInfo
                {
                    FileName = info.Name,
                    Timestamp = info.LastWriteTime,
                    BugcheckCode = nearest?.BugcheckCode,
                });
            }
        }
        catch
        {
            // Folder missing or access denied - an empty list just means nothing to show.
        }
        return dumps.OrderByDescending(d => d.Timestamp).ToList();
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";

    // Round 7 #13: how long a service "took to start", mined from the System log's own Service
    // Control Manager event 7036 ("service entered the running/stopped state") - the only
    // service-lifecycle event Windows logs by default. There is no explicit "start requested at"
    // timestamp in default logging (that needs Verbose SCM ETW tracing, a much heavier ask than
    // this app's other event-log reads), so this approximates duration as the time between a
    // service's most recent "stopped" 7036 entry and the following "running" 7036 entry for the
    // same service - a real, if approximate, measurement of the same shape EventLogService's
    // WasLastShutdownUnexpected correlation already uses elsewhere. A stopped-to-running gap wider
    // than StartDurationCeiling is treated as "the service just sat stopped for a while, then
    // happened to start" rather than a real start latency, and discarded rather than reported as a
    // wildly inflated duration.
    private static readonly TimeSpan StartDurationCeiling = TimeSpan.FromMinutes(3);

    public List<ServiceStartDuration> ReadServiceStartDurations()
    {
        var byService = new Dictionary<string, List<(DateTime Time, bool Running)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7036) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 4000; // generous - the SCM can log thousands of these over 30 days on a busy machine
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        if (record.Properties.Count < 2) continue;
                        string? name = record.Properties[0].Value as string;
                        string? state = record.Properties[1].Value as string;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(state) || record.TimeCreated is null) continue;

                        bool running = state.Equals("running", StringComparison.OrdinalIgnoreCase);
                        if (!running && !state.Equals("stopped", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!byService.TryGetValue(name, out var list))
                            byService[name] = list = new List<(DateTime, bool)>();
                        list.Add((record.TimeCreated.Value, running));
                    }
                    catch { /* one malformed entry shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to "no start-duration data".
        }

        var result = new List<ServiceStartDuration>();
        foreach (var (name, transitions) in byService)
        {
            var ordered = transitions.OrderBy(t => t.Time).ToList();
            var durations = new List<double>();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (!ordered[i].Running || ordered[i - 1].Running) continue; // only a stopped -> running pair
                var gap = ordered[i].Time - ordered[i - 1].Time;
                if (gap > TimeSpan.Zero && gap <= StartDurationCeiling)
                    durations.Add(gap.TotalMilliseconds);
            }
            if (durations.Count == 0) continue;

            result.Add(new ServiceStartDuration
            {
                ServiceName = name,
                LastStartDurationMs = durations[^1],
                AvgStartDurationMs = durations.Average(),
                SampleCount = durations.Count,
            });
        }
        return result;
    }
}
