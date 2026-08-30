using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #90/#91: power scheme listing/switching and Modern Standby (S0) vs. legacy S3 sleep
/// support detection, both by shelling out to powercfg.exe - the same "known Windows tool, not
/// raw registry/API interop" tradeoff ScheduledTaskService/ServiceControlService's recovery-action
/// reader already take (there's no simple public WMI class for either of these; the underlying
/// power-policy API surface is COM-based and a meaningfully larger undertaking for what's
/// ultimately just "read and lightly reformat a command's own text output").
/// </summary>
public static class PowerPlanService
{
    /// <summary>Parses `powercfg /list` - one line per scheme, e.g.
    /// "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *" (the trailing `*`
    /// marks the active scheme). Returns an empty list on any failure (powercfg missing/blocked)
    /// rather than throwing - the Energy &amp; Thermals tab just hides the power-plan card when
    /// this comes back empty.</summary>
    public static async Task<List<PowerPlanInfo>> ListPowerPlansAsync()
    {
        var plans = new List<PowerPlanInfo>();
        try
        {
            string output = (await RunCapturedAsync("powercfg.exe", "/list")).Output;
            foreach (Match m in Regex.Matches(output, @"Power Scheme GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]*)\)\s*(\*)?"))
            {
                plans.Add(new PowerPlanInfo
                {
                    Guid = m.Groups[1].Value,
                    Name = m.Groups[2].Value.Trim(),
                    IsActive = m.Groups[3].Success,
                });
            }
        }
        catch
        {
            // Best-effort - an empty list just means the power-plan card stays hidden.
        }
        return plans;
    }

    /// <summary>`powercfg /setactive &lt;guid&gt;` - switches the active Windows power scheme.</summary>
    public static async Task<(bool Success, string? Error)> SetActivePlanAsync(string guid)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", $"/setactive {guid}");
            return exitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Round 12, #91: parses `powercfg /a` ("available sleep states report") to say
    /// whether this system supports Modern Standby (S0 Low Power Idle - the "instant on/off"
    /// networked-standby mode most recent laptops use) vs. legacy S3 ("suspend to RAM", the older
    /// mechanism most desktops still use), or neither/unknown when powercfg's report doesn't
    /// mention either in a recognizable way (older Windows builds phrase this report slightly
    /// differently release to release, so this looks for the two well-known phrases rather than
    /// trying to parse the report's full structure).</summary>
    public static async Task<string> ReadSleepStateSupportAsync()
    {
        try
        {
            string output = (await RunCapturedAsync("powercfg.exe", "/a")).Output;
            bool hasModernStandby = output.Contains("S0 Low Power Idle", StringComparison.OrdinalIgnoreCase);
            bool hasS3 = Regex.IsMatch(output, @"Standby\s*\(S3\)", RegexOptions.IgnoreCase);

            if (hasModernStandby) return "Modern Standby (S0 Low Power Idle)";
            if (hasS3) return "Legacy S3 (Suspend to RAM)";
            return "Unknown - powercfg /a didn't report a recognizable sleep state";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>#631: parses `powercfg /qh` (query, including hidden settings, for the active
    /// scheme) for the processor-power sub-group - minimum/maximum processor state and
    /// core-parking minimum cores, AC and DC. Not a documented/versioned format (same caveat as
    /// every other powercfg text-parse in this app) - each setting is matched by its bracketed
    /// friendly name rather than a fixed line offset, and any setting not found in the output is
    /// left null ("Unknown") rather than guessed.</summary>
    public static async Task<ProcessorPowerSettings> ReadProcessorPowerSettingsAsync()
    {
        string output;
        try { output = (await RunCapturedAsync("powercfg.exe", "/qh", 15000)).Output; }
        catch { return new ProcessorPowerSettings(); }

        return new ProcessorPowerSettings
        {
            MinProcessorStateAcPercent = ExtractSettingPercent(output, "Minimum processor state", ac: true),
            MinProcessorStateDcPercent = ExtractSettingPercent(output, "Minimum processor state", ac: false),
            MaxProcessorStateAcPercent = ExtractSettingPercent(output, "Maximum processor state", ac: true),
            MaxProcessorStateDcPercent = ExtractSettingPercent(output, "Maximum processor state", ac: false),
            CoreParkingMinCoresAcPercent = ExtractSettingPercent(output, "Processor performance core parking min cores", ac: true),
            CoreParkingMinCoresDcPercent = ExtractSettingPercent(output, "Processor performance core parking min cores", ac: false),
        };
    }

    /// <summary>Finds the block for one named setting (e.g. "(Minimum processor state)") and reads
    /// its "Current AC/DC Power Setting Index" hex value - `powercfg /qh` reports processor-state
    /// and core-parking settings as a raw hex value that IS already the percent (0x00000032 = 50).
    /// Searches only within that one setting's own block (up to the next "Power Setting GUID:"
    /// line) so an AC/DC value from an unrelated nearby setting can't be matched by mistake.</summary>
    private static int? ExtractSettingPercent(string output, string settingFriendlyName, bool ac)
    {
        int nameIdx = output.IndexOf($"({settingFriendlyName})", StringComparison.OrdinalIgnoreCase);
        if (nameIdx < 0) return null;

        int blockEnd = output.IndexOf("Power Setting GUID:", nameIdx + 1, StringComparison.OrdinalIgnoreCase);
        string block = blockEnd > nameIdx ? output[nameIdx..blockEnd] : output[nameIdx..];

        string label = ac ? "Current AC Power Setting Index" : "Current DC Power Setting Index";
        var match = Regex.Match(block, $@"{Regex.Escape(label)}:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        return int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value : null;
    }

    // Well-known, publicly documented powercfg subgroup/setting GUIDs (the "Processor power
    // management" subgroup and two of its individual settings, plus the USB subgroup and its
    // selective-suspend setting) - unlike this file's other undocumented text-parse formats,
    // these specific GUIDs are a stable part of Windows' power-setting schema and are widely
    // relied on in Microsoft's own and third-party deployment scripts.
    public const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    public const string SystemCoolingPolicyGuid = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
    public const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string UsbSelectiveSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    /// <summary>#661: parses `powercfg /q &lt;scheme&gt;` (every subgroup/setting visible in the
    /// Control Panel Power Options UI - deliberately not `/qh`'s hidden settings, which #662
    /// already covers separately) for both the active scheme and the built-in SCHEME_BALANCED
    /// defaults, then returns only the settings whose AC and/or DC index actually differs between
    /// the two. "Differs from Balanced" is a reasonable, well-known reference point for
    /// troubleshooting even when the active scheme isn't literally derived from Balanced - the
    /// same idea a diff-against-a-known-baseline tool uses for any config file.</summary>
    public static async Task<(List<PowerPlanSettingDiff> Diffs, string StatusText)> ReadPlanSettingDiffAsync(string activeSchemeGuid)
    {
        string activeOutput, balancedOutput;
        try
        {
            var activeTask = RunCapturedAsync("powercfg.exe", $"/q {activeSchemeGuid}", 15000);
            var balancedTask = RunCapturedAsync("powercfg.exe", "/q SCHEME_BALANCED", 15000);
            await Task.WhenAll(activeTask, balancedTask);
            activeOutput = activeTask.Result.Output;
            balancedOutput = balancedTask.Result.Output;
        }
        catch (Exception ex)
        {
            return (new List<PowerPlanSettingDiff>(), $"Couldn't read power plan settings: {ex.Message}");
        }

        var activeSettings = ParsePlanQueryOutput(activeOutput);
        if (activeSettings.Count == 0)
            return (new List<PowerPlanSettingDiff>(), "Couldn't read the active scheme's settings (powercfg /q returned nothing recognizable).");

        var balancedByKey = new Dictionary<(string Subgroup, string Setting), (string? AcHex, string? DcHex)>();
        foreach (var s in ParsePlanQueryOutput(balancedOutput))
            balancedByKey[(s.SubgroupName, s.SettingName)] = (s.AcHex, s.DcHex);

        var diffs = new List<PowerPlanSettingDiff>();
        foreach (var s in activeSettings)
        {
            if (!balancedByKey.TryGetValue((s.SubgroupName, s.SettingName), out var def)) continue;

            bool acDiffers = !string.Equals(s.AcHex, def.AcHex, StringComparison.OrdinalIgnoreCase);
            bool dcDiffers = !string.Equals(s.DcHex, def.DcHex, StringComparison.OrdinalIgnoreCase);
            if (!acDiffers && !dcDiffers) continue;

            diffs.Add(new PowerPlanSettingDiff
            {
                SubgroupName = s.SubgroupName,
                SettingName = s.SettingName,
                ActiveAcText = s.AcHex ?? "Unknown",
                ActiveDcText = s.DcHex ?? "Unknown",
                DefaultAcText = def.AcHex ?? "Unknown",
                DefaultDcText = def.DcHex ?? "Unknown",
                AcDiffers = acDiffers,
                DcDiffers = dcDiffers,
            });
        }

        diffs = diffs.OrderBy(d => d.SubgroupName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.SettingName, StringComparer.OrdinalIgnoreCase).ToList();
        string status = diffs.Count == 0
            ? "No settings differ from the Balanced defaults."
            : $"{diffs.Count} setting(s) differ from Balanced defaults.";
        return (diffs, status);
    }

    /// <summary>Parses one `powercfg /q` report into a flat (subgroup, setting, AC hex, DC hex)
    /// list - each setting's enclosing subgroup is whichever "Subgroup GUID: ... (Name)" line most
    /// recently preceded it, the same nearest-preceding-header technique BootPerformanceService's
    /// own adaptive event-field scan uses for a different undocumented source. AC/DC values are
    /// kept as raw "0xNNNNNNNN" text (not parsed to int) since not every setting is a simple
    /// percent - some are opaque bitmasks or GUID selections.</summary>
    private static List<(string SubgroupName, string SettingName, string? AcHex, string? DcHex)> ParsePlanQueryOutput(string output)
    {
        var result = new List<(string, string, string?, string?)>();
        var subgroupMatches = Regex.Matches(output, @"Subgroup GUID:\s*[0-9a-fA-F-]{36}\s*\(([^)]*)\)", RegexOptions.IgnoreCase)
            .Cast<Match>().OrderBy(m => m.Index).ToList();
        var settingMatches = Regex.Matches(output, @"Power Setting GUID:\s*[0-9a-fA-F-]{36}\s*\(([^)]*)\)", RegexOptions.IgnoreCase)
            .Cast<Match>().OrderBy(m => m.Index).ToList();

        for (int i = 0; i < settingMatches.Count; i++)
        {
            var m = settingMatches[i];
            int blockEnd = i + 1 < settingMatches.Count ? settingMatches[i + 1].Index : output.Length;
            string block = output[m.Index..blockEnd];

            string subgroupName = subgroupMatches.LastOrDefault(sg => sg.Index <= m.Index)?.Groups[1].Value.Trim() ?? string.Empty;
            string settingName = m.Groups[1].Value.Trim();

            var acMatch = Regex.Match(block, @"Current AC Power Setting Index:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
            var dcMatch = Regex.Match(block, @"Current DC Power Setting Index:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);

            result.Add((subgroupName, settingName,
                acMatch.Success ? "0x" + acMatch.Groups[1].Value : null,
                dcMatch.Success ? "0x" + dcMatch.Groups[1].Value : null));
        }
        return result;
    }

    // #683: the "PCI Express" subgroup and its "Link State Power Management" (ASPM) setting - like
    // SubProcessorGuid/UsbSubgroupGuid above, these are well-known, stable GUIDs (not an
    // undocumented text format), confirmed live via `powercfg /q` on a real dev machine ("PCI
    // Express" / "Link State Power Management", GUID Alias SUB_PCIEXPRESS/ASPM). Possible setting
    // indices are 0 = Off, 1 = Moderate power savings, 2 = Maximum power savings.
    public const string PciExpressSubgroupGuid = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string AspmSettingGuid = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>#683: reads the active scheme's ASPM index (AC and DC) - aggressive ASPM (index 1
    /// or 2) is a well-known cause of NVMe dropouts and eGPU/Thunderbolt disconnects, since the
    /// link partner has to renegotiate out of a low-power link state before it can be used again.
    /// Null on either side means the setting couldn't be read (denied, or a system whose chipset
    /// doesn't expose PCIe ASPM to Windows at all) - "Unknown", not "Off".</summary>
    public static async Task<(int? AcIndex, int? DcIndex)> ReadAspmSettingAsync()
    {
        string output;
        try { output = (await RunCapturedAsync("powercfg.exe", $"/q SCHEME_CURRENT {PciExpressSubgroupGuid} {AspmSettingGuid}", 15000)).Output; }
        catch { return (null, null); }

        var acMatch = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
        var dcMatch = Regex.Match(output, @"Current DC Power Setting Index:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
        int? ac = acMatch.Success ? int.Parse(acMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
        int? dc = dcMatch.Success ? int.Parse(dcMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
        return (ac, dc);
    }

    /// <summary>#683: the GPU tab's one-click "set to Off" action - sets both AC and DC ASPM index
    /// to 0 (Off) on the active scheme and re-activates it, the same two-step SetAcValueIndexAsync
    /// already uses for a single-side setting change.</summary>
    public static async Task<(bool Success, string? Error)> SetAspmOffAsync()
    {
        try
        {
            var (acOutput, acExit) = await RunCapturedAsync("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {PciExpressSubgroupGuid} {AspmSettingGuid} 0", 15000);
            if (acExit != 0) return (false, acOutput.Trim());

            var (dcOutput, dcExit) = await RunCapturedAsync("powercfg.exe", $"/setdcvalueindex SCHEME_CURRENT {PciExpressSubgroupGuid} {AspmSettingGuid} 0", 15000);
            if (dcExit != 0) return (false, dcOutput.Trim());

            var (activateOutput, activateExit) = await RunCapturedAsync("powercfg.exe", "/setactive SCHEME_CURRENT", 15000);
            return activateExit == 0 ? (true, null) : (false, activateOutput.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#662: the small set of commonly-hidden-from-the-Control-Panel-UI settings this
    /// round surfaces, each with a fixed, hand-written plain-English explanation - powercfg's own
    /// setting descriptions are themselves indirect string resource references
    /// (<c>@%SystemRoot%\system32\...,-NNNN</c>) this app doesn't resolve, so these are written
    /// once here rather than sourced from Windows.</summary>
    private static readonly (string FriendlyName, string DisplayName, string Explanation)[] HiddenSettingsCatalog =
    {
        ("Processor performance boost mode", "Processor performance boost mode",
            "Controls whether, and how aggressively, the CPU is allowed to boost above its base clock. \"Disabled\" means it never boosts at all; \"Aggressive\" favors performance over efficiency."),
        ("Processor performance core parking min cores", "Core parking — minimum cores",
            "The minimum percentage of logical processors Windows keeps unparked (available) at all times. Too low can hurt burst responsiveness; too high wastes idle power."),
        ("Processor performance core parking max cores", "Core parking — maximum cores",
            "The maximum percentage of logical processors Windows will ever unpark. Set below 100%, this silently caps how many cores can ever be active at once — a common cause of \"more cores than the app shows using.\""),
        ("Minimum processor state", "Minimum processor state (PROCTHROTTLEMIN)",
            "The floor on CPU clock speed as a percent of maximum, even when idle. A low value on battery saves power; a very low value on AC can make the system feel sluggish coming out of idle."),
        ("Maximum processor state", "Maximum processor state (PROCTHROTTLEMAX)",
            "The ceiling on CPU clock speed as a percent of maximum. Set below 100% on AC, this silently caps performance even while plugged in — a classic, invisible \"why is my desktop slow\" cause."),
        ("System cooling policy", "System cooling policy",
            "Active cooling ramps fans up to hold clock speed; Passive throttles the CPU down first and only ramps fans as a last resort. Passive while on AC power (a plugged-in desktop) is a classic cause of \"slow but cool.\""),
    };

    public static async Task<List<HiddenPowerSettingRow>> ReadHiddenPowerSettingsAsync()
    {
        string output;
        try { output = (await RunCapturedAsync("powercfg.exe", "/qh", 15000)).Output; }
        catch { return new List<HiddenPowerSettingRow>(); }

        var rows = new List<HiddenPowerSettingRow>();
        foreach (var (friendlyName, displayName, explanation) in HiddenSettingsCatalog)
        {
            int? acRaw = ExtractSettingPercent(output, friendlyName, ac: true);
            int? dcRaw = ExtractSettingPercent(output, friendlyName, ac: false);
            if (acRaw is null && dcRaw is null) continue; // not exposed on this Windows build/scheme at all

            rows.Add(new HiddenPowerSettingRow
            {
                SettingName = displayName,
                Explanation = explanation,
                AcValueText = DescribeHiddenSettingValue(friendlyName, acRaw),
                DcValueText = DescribeHiddenSettingValue(friendlyName, dcRaw),
                ValuesDiffer = acRaw != dcRaw,
            });
        }
        return rows;
    }

    private static string DescribeHiddenSettingValue(string friendlyName, int? raw)
    {
        if (raw is not { } v) return "Unknown";
        return friendlyName switch
        {
            "Processor performance boost mode" => v switch
            {
                0 => "Disabled",
                1 => "Enabled",
                2 => "Aggressive",
                3 => "Efficient Enabled",
                4 => "Efficient Aggressive",
                5 => "Aggressive At Guaranteed",
                6 => "Efficient Aggressive At Guaranteed",
                _ => $"{v} (unrecognized)",
            },
            "System cooling policy" => v switch { 0 => "Active", 1 => "Passive", _ => $"{v} (unrecognized)" },
            _ => $"{v}%",
        };
    }

    /// <summary>#663/#668: `powercfg /setacvalueindex` for one AC-side setting, followed by
    /// `/setactive` on the same scheme - `/setacvalueindex` alone only stages the change,
    /// powercfg's own documented guidance is to re-activate the scheme to force it to take effect
    /// immediately (the same two-step SetPowerPlanAsync's own `/setactive` call already performs
    /// for a full plan switch).</summary>
    public static async Task<(bool Success, string? Error)> SetAcValueIndexAsync(string schemeGuid, string subgroupGuid, string settingGuid, int value)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", $"/setacvalueindex {schemeGuid} {subgroupGuid} {settingGuid} {value}", 15000);
            if (exitCode != 0) return (false, output.Trim());

            var (activateOutput, activateExit) = await RunCapturedAsync("powercfg.exe", $"/setactive {schemeGuid}", 15000);
            return activateExit == 0 ? (true, null) : (false, activateOutput.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // #691: power-mode overlay (Best power efficiency / Balanced / Best performance - the slider
    // next to the battery icon on Windows 10 1709+/11). This sits ON TOP of whichever power scheme
    // ListPowerPlansAsync/SetActivePlanAsync manage - a machine can show "Balanced" as its active
    // scheme while the overlay independently caps it to the efficiency profile, a genuinely
    // confusing state the plan card alone can't surface. There's no powercfg subcommand or WMI
    // class for the overlay (verified: `powercfg /list`, `/q`, and `/getactivescheme` never mention
    // it) - PowerGetActualOverlayScheme/PowerSetActiveOverlayScheme are the only surface, and they
    // are documented Win32 APIs (powrprof.dll), so this is the one place in this file that's a
    // direct P/Invoke rather than a powercfg text-parse (project convention: raw interop only when
    // no tool/WMI class exists at all).
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    // Well-known, publicly documented overlay-scheme GUIDs - the same three every "toggle Windows'
    // power-mode slider from a script" reference relies on. GUID_NULL (all-zero) is "Balanced" -
    // Windows encodes "no overlay applied" as the null GUID rather than a fourth named value.
    public const string OverlayBestPowerEfficiencyGuid = "961cc777-2547-4f9d-8174-7d86181b8a7a";
    public const string OverlayBalancedGuid = "00000000-0000-0000-0000-000000000000";
    public const string OverlayBestPerformanceGuid = "ded574b5-45a0-4f42-8737-46345c09c238";

    public static readonly IReadOnlyList<PowerOverlaySchemeOption> OverlaySchemes = new[]
    {
        new PowerOverlaySchemeOption { Guid = OverlayBestPowerEfficiencyGuid, Name = "Best power efficiency" },
        new PowerOverlaySchemeOption { Guid = OverlayBalancedGuid, Name = "Balanced" },
        new PowerOverlaySchemeOption { Guid = OverlayBestPerformanceGuid, Name = "Best performance" },
    };

    /// <summary>#691: the active power-mode overlay. "Unknown" (never a guess) when the API isn't
    /// present at all (pre-1709 Windows, or a policy/edition that hides it) or the returned GUID
    /// doesn't match one of the three documented values - some OEM utilities register a fourth
    /// "Recommended"/custom overlay this app doesn't try to name, shown as "Unknown overlay
    /// ({guid})" instead of silently mislabeling it as one of the three known ones.</summary>
    public static string ReadActiveOverlaySchemeText()
    {
        try
        {
            uint result = PowerGetActualOverlayScheme(out var guid);
            if (result != 0) return "Unknown";

            foreach (var scheme in OverlaySchemes)
            {
                if (Guid.TryParse(scheme.Guid, out var known) && known == guid) return scheme.Name;
            }
            return $"Unknown overlay ({guid:B})";
        }
        catch (EntryPointNotFoundException)
        {
            return "Unknown — not supported on this Windows build";
        }
        catch
        {
            // powrprof.dll missing/blocked, or any other P/Invoke failure - degrade to Unknown
            // rather than throw out of a card that's otherwise just informational.
            return "Unknown";
        }
    }

    /// <summary>#691: switches the active power-mode overlay via PowerSetActiveOverlayScheme -
    /// takes effect immediately, no separate "activate" step the way a plain power-scheme switch
    /// needs.</summary>
    public static Task<(bool Success, string? Error)> SetActiveOverlaySchemeAsync(string overlaySchemeGuid)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Guid.TryParse(overlaySchemeGuid, out var guid))
                    return (false, "Not a recognized overlay scheme.");

                uint result = PowerSetActiveOverlayScheme(guid);
                return result == 0 ? (true, (string?)null) : (false, $"powrprof.dll returned error {result}.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
