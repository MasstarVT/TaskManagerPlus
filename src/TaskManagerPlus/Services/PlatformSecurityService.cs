using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #871-880: "Platform security" section for the Security tab - HVCI/memory integrity,
/// VBS detail beyond the plain running flag, LSA protection (RunAsPPL), Kernel DMA Protection,
/// the vulnerable-driver blocklist, boot integrity switches (testsigning/etc via bcdedit), app
/// control policy presence (WDAC/AppLocker/Smart App Control), an extended TPM detail read, Secure
/// Boot detail (setup mode / DBX recency), and a UAC configuration audit. This is a big enough
/// topic (10 items) for its own file/section rather than folding into SystemSpecsService, which
/// keeps its existing (smaller) TPM/Secure Boot/VBS card as-is.
///
/// Same conventions as the rest of this app's security surface (see DefenderService/AutorunsService):
/// every registry/WMI/event-log/shell-out read is wrapped independently and degrades to Unknown/
/// empty rather than fabricating a value; nothing here polls - everything is reached from one
/// on-demand "Refresh platform security" button on the Security tab. A couple of these reads
/// (#874 Kernel DMA Protection, and DBX recency under #879) have no reliably documented
/// programmatic source at all - those degrade WHOLESALE to an honest "Unknown - no reliable
/// programmatic source found" rather than a guess, which CLAUDE.md's "degrade to Unknown, never
/// fabricate" rule explicitly permits.
/// </summary>
public static class PlatformSecurityService
{
    private const string CodeIntegrityOperationalLog = "Microsoft-Windows-CodeIntegrity/Operational";
    private const int LookbackDays = 180;
    private const int MaxEventsPerQuery = 30;

    private static readonly Regex SysDriverNameRegex = new(@"[\w.\-]+\.sys\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BcdFieldLineRegex = new(@"^(\S+)\s+(.+)$", RegexOptions.Compiled);

    // Shared bool?-to-display-text helper (nested result classes below can call this by simple
    // name) - same "precompute a *Text property so XAML can bind straight to a string" convention
    // DefenderService.ComputerStatus already uses for its own tri-state flags.
    private static string TriStateText(bool? v) => v switch { true => "Yes", false => "No", null => "Unknown" };

    // ==================================================================================
    // Top-level bundle + entry point.
    // ==================================================================================

    public sealed class PlatformSecurityInfo
    {
        public HvciInfo Hvci { get; init; } = new();
        public VbsDetailInfo VbsDetail { get; init; } = new();
        public LsaProtectionInfo LsaProtection { get; init; } = new();
        public KernelDmaProtectionInfo KernelDma { get; init; } = new();
        public VulnerableDriverBlocklistInfo DriverBlocklist { get; init; } = new();
        public BootIntegrityInfo BootIntegrity { get; init; } = new();
        public AppControlInfo AppControl { get; init; } = new();
        public TpmDetailInfo TpmDetail { get; init; } = new();
        public SecureBootDetailInfo SecureBootDetail { get; init; } = new();
        public UacConfigurationInfo Uac { get; init; } = new();
    }

    /// <summary>Runs every read in this file and returns both the bundled info and whatever
    /// SecurityFinding objects the High/Medium-severity items (#875/#876/#880) raised.
    /// <paramref name="persistenceEntries"/> lets the caller reuse an already-scanned Persistence
    /// grid (the same "pass a snapshot, or fall back to a fresh AutorunsService.Scan()" pattern
    /// DefenderService.DiagnoseDuplicatedRealTimeScanners already uses) rather than paying for a
    /// second full registry sweep just to get the Kernel Driver entries #875/#876 cross-reference
    /// against.</summary>
    public static (PlatformSecurityInfo Info, List<SecurityFinding> Findings) ReadAll(IEnumerable<AutorunEntry>? persistenceEntries)
    {
        var findings = new List<SecurityFinding>();
        var kernelDrivers = (persistenceEntries?.ToList() ?? AutorunsService.Scan())
            .Where(e => e.Category.Equals("Kernel Driver", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dg = ReadDeviceGuardSnapshot();
        var hvci = BuildHvci(dg);
        var vbs = BuildVbsDetail(dg);
        var lsa = BuildLsaProtection();
        var dma = BuildKernelDmaProtection(dg);
        var (blocklistInfo, blocklistFinding) = BuildVulnerableDriverBlocklist(kernelDrivers);
        var (bootInfo, bootFinding) = BuildBootIntegrity(kernelDrivers);
        var appControl = BuildAppControl(dg);
        var tpm = BuildTpmDetail();
        var secureBoot = BuildSecureBootDetail();
        var (uacInfo, uacFinding) = BuildUacConfiguration();

        if (blocklistFinding is not null) findings.Add(blocklistFinding);
        if (bootFinding is not null) findings.Add(bootFinding);
        if (uacFinding is not null) findings.Add(uacFinding);

        var info = new PlatformSecurityInfo
        {
            Hvci = hvci,
            VbsDetail = vbs,
            LsaProtection = lsa,
            KernelDma = dma,
            DriverBlocklist = blocklistInfo,
            BootIntegrity = bootInfo,
            AppControl = appControl,
            TpmDetail = tpm,
            SecureBootDetail = secureBoot,
            Uac = uacInfo,
        };

        return (info, findings);
    }

    // ==================================================================================
    // Shared Win32_DeviceGuard reader - the full documented property set (VirtualizationBased-
    // SecurityStatus, Required/AvailableSecurityProperties, SecurityServicesConfigured/Running,
    // CodeIntegrityPolicyEnforcementStatus) - queried once and reused by #871/#872/#874/#877
    // rather than four separate WMI round trips. SystemSpecsService.ReadVbsStatus already queries
    // this same class for its own (smaller) card - querying it again here with more properties is
    // the explicitly-sanctioned approach per #872's own text, not a duplicate-and-diverge mistake.
    // ==================================================================================

    private sealed class DeviceGuardSnapshot
    {
        public bool Available;
        public int? VirtualizationBasedSecurityStatus;
        public uint[]? RequiredSecurityProperties;
        public uint[]? AvailableSecurityProperties;
        public uint[]? ConfiguredSecurityServices;
        public uint[]? RunningSecurityServices;
        public int? CodeIntegrityPolicyEnforcementStatus;
    }

    private static DeviceGuardSnapshot ReadDeviceGuardSnapshot()
    {
        var snap = new DeviceGuardSnapshot();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, RequiredSecurityProperties, AvailableSecurityProperties, SecurityServicesConfigured, SecurityServicesRunning, CodeIntegrityPolicyEnforcementStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                snap.Available = true;
                try { snap.VirtualizationBasedSecurityStatus = Convert.ToInt32(mo["VirtualizationBasedSecurityStatus"]); } catch { /* field missing on this build */ }
                try { snap.RequiredSecurityProperties = mo["RequiredSecurityProperties"] as uint[]; } catch { /* leave null */ }
                try { snap.AvailableSecurityProperties = mo["AvailableSecurityProperties"] as uint[]; } catch { /* leave null */ }
                try { snap.ConfiguredSecurityServices = mo["SecurityServicesConfigured"] as uint[]; } catch { /* leave null */ }
                try { snap.RunningSecurityServices = mo["SecurityServicesRunning"] as uint[]; } catch { /* leave null */ }
                try { snap.CodeIntegrityPolicyEnforcementStatus = Convert.ToInt32(mo["CodeIntegrityPolicyEnforcementStatus"]); } catch { /* leave null */ }
                break; // one instance expected
            }
        }
        catch
        {
            // Class doesn't exist on this OS build (pre-1607), or WMI is unavailable/denied -
            // snap.Available stays false, every consumer below degrades to "Unknown".
        }
        return snap;
    }

    // Win32_DeviceGuard.SecurityServicesConfigured/Running value -> display name - same documented
    // mapping SystemSpecsService.SecurityServiceName already uses for its own (smaller) VBS card.
    private static string SecurityServiceName(uint code) => code switch
    {
        1 => "Credential Guard",
        2 => "Memory Integrity (HVCI)",
        3 => "System Guard Secure Launch",
        4 => "SMM Firmware Measurement",
        _ => $"Unknown ({code})",
    };

    // Win32_DeviceGuard.Required/AvailableSecurityProperties value -> display name - a DIFFERENT
    // documented enum from SecurityServiceName above (hardware/firmware capabilities, not "which
    // service is running").
    private static string SecurityPropertyName(uint code) => code switch
    {
        0 => "No available hardware security",
        1 => "Base virtualization support",
        2 => "Secure Boot",
        3 => "DMA protection",
        4 => "Secure memory overwrite",
        5 => "UEFI code readonly",
        6 => "SMM security mitigations",
        7 => "Mode-based execution control",
        8 => "APIC virtualization",
        _ => $"Unknown ({code})",
    };

    // ==================================================================================
    // #871: HVCI / memory integrity status and why it's off.
    // ==================================================================================

    public sealed class HvciInfo
    {
        public bool? RunningPerDeviceGuard { get; init; }
        public bool? ConfiguredPerDeviceGuard { get; init; }
        public IReadOnlyDictionary<string, int> RegistryValues { get; init; } = new Dictionary<string, int>();
        public bool RegistryKeyPresent { get; init; }
        public string ReasonText { get; init; } = "Unknown.";
        public IReadOnlyList<string> BlockingDriverNames { get; init; } = Array.Empty<string>();

        public string RegistryValuesText => RegistryValues.Count == 0
            ? (RegistryKeyPresent ? "(key present, no DWORD values under it)" : "(key not present)")
            : string.Join(", ", RegistryValues.Select(kv => $"{kv.Key}={kv.Value}"));

        public string RunningText => TriStateText(RunningPerDeviceGuard);
        public string ConfiguredText => TriStateText(ConfiguredPerDeviceGuard);
    }

    private static HvciInfo BuildHvci(DeviceGuardSnapshot dg)
    {
        bool? running = dg.RunningSecurityServices is null ? null : dg.RunningSecurityServices.Contains(2u);
        bool? configured = dg.ConfiguredSecurityServices is null ? null : dg.ConfiguredSecurityServices.Contains(2u);

        var regValues = new Dictionary<string, int>();
        bool keyPresent = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (key is not null)
            {
                keyPresent = true;
                // Value names vary by Windows build (Enabled, WasEnabledBy, ...) - read whatever
                // DWORDs actually exist rather than assuming one specific name, per the item's own
                // guidance.
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is int i) regValues[valueName] = i;
                }
            }
        }
        catch
        {
            // Key inaccessible - regValues stays empty, keyPresent may be false.
        }

        var blockingDrivers = new List<string>();
        string reason;
        if (running == true)
        {
            reason = "Memory integrity (HVCI) is running.";
        }
        else
        {
            var events = ReadCodeIntegrityEvents(new[] { 3082, 3083 }, MaxEventsPerQuery, out _);
            foreach (var ev in events)
            {
                var m = SysDriverNameRegex.Match(ev.Summary);
                if (m.Success && !blockingDrivers.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                    blockingDrivers.Add(m.Value);
            }

            reason = blockingDrivers.Count > 0
                ? $"Memory integrity is off because of {string.Join(", ", blockingDrivers)} (named in Microsoft-Windows-CodeIntegrity/Operational events 3082/3083)."
                : configured == true
                    ? "Memory integrity is configured but not currently running - this often just needs a reboot to take effect."
                    : "Memory integrity is off (reason not determined from available logs).";
        }

        return new HvciInfo
        {
            RunningPerDeviceGuard = running,
            ConfiguredPerDeviceGuard = configured,
            RegistryValues = regValues,
            RegistryKeyPresent = keyPresent,
            ReasonText = reason,
            BlockingDriverNames = blockingDrivers,
        };
    }

    // ==================================================================================
    // Shared Microsoft-Windows-CodeIntegrity/Operational event reader - #871 (3082/3083) and #873
    // (3065/3033) both need it. Same EventLogQuery/EventLogReader shape as DefenderService's own
    // Operational-log reads (capped count, capped lookback, degrades to "nothing found" rather than
    // throwing on a channel that isn't enabled/is access-denied).
    // ==================================================================================

    public sealed record CodeIntegrityEvent(DateTime Time, int EventId, string Summary);

    private static List<CodeIntegrityEvent> ReadCodeIntegrityEvents(int[] eventIds, int maxEvents, out bool logAvailable)
    {
        var result = new List<CodeIntegrityEvent>();
        logAvailable = true;
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(CodeIntegrityOperationalLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap
                    result.Add(new CodeIntegrityEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, Truncate(message, 400)));
                }
            }
        }
        catch
        {
            // Operational log unavailable/access denied/channel not enabled - "nothing found".
            logAvailable = false;
        }
        return result;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    // ==================================================================================
    // #872: VBS detail beyond the existing running flag.
    // ==================================================================================

    public sealed class VbsDetailInfo
    {
        public IReadOnlyList<string> RequiredProperties { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AvailableProperties { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ConfiguredServices { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RunningServices { get; init; } = Array.Empty<string>();
        public bool? CredentialGuardRunning { get; init; }
        public bool? HypervisorPresent { get; init; }
        public bool? VirtualizationFirmwareEnabled { get; init; }
        public bool UefiSecureBootKeyPresent { get; init; }
        public bool? SecureBootEnabled { get; init; }
        public string ExplanationText { get; init; } = "Unknown.";

        public string RequiredPropertiesText => RequiredProperties.Count == 0 ? "(none reported)" : string.Join(", ", RequiredProperties);
        public string AvailablePropertiesText => AvailableProperties.Count == 0 ? "(none reported)" : string.Join(", ", AvailableProperties);
        public string ConfiguredServicesText => ConfiguredServices.Count == 0 ? "(none configured)" : string.Join(", ", ConfiguredServices);
        public string RunningServicesText => RunningServices.Count == 0 ? "(none running)" : string.Join(", ", RunningServices);
        public string CredentialGuardRunningText => TriStateText(CredentialGuardRunning);
        public string HypervisorPresentText => TriStateText(HypervisorPresent);
        public string VirtualizationFirmwareEnabledText => TriStateText(VirtualizationFirmwareEnabled);
        public string SecureBootEnabledText => TriStateText(SecureBootEnabled);
        public string UefiSecureBootKeyPresentText => UefiSecureBootKeyPresent ? "Yes" : "No";
    }

    private static VbsDetailInfo BuildVbsDetail(DeviceGuardSnapshot dg)
    {
        var required = (dg.RequiredSecurityProperties ?? Array.Empty<uint>()).Select(SecurityPropertyName).ToList();
        var available = (dg.AvailableSecurityProperties ?? Array.Empty<uint>()).Select(SecurityPropertyName).ToList();
        var configured = (dg.ConfiguredSecurityServices ?? Array.Empty<uint>()).Select(SecurityServiceName).ToList();
        var running = (dg.RunningSecurityServices ?? Array.Empty<uint>()).Select(SecurityServiceName).ToList();
        bool? credGuard = dg.RunningSecurityServices is null ? null : dg.RunningSecurityServices.Contains(1u);

        bool? hypervisorPresent = ReadHypervisorPresent();
        bool? vfwEnabled = ReadVirtualizationFirmwareEnabled();
        bool uefiKeyPresent = SecureBootStateKeyExists();
        bool? secureBootEnabled = SystemSpecsService.ReadSecureBootEnabled();

        int? vbsStatus = dg.VirtualizationBasedSecurityStatus;
        string explanation;
        if (vbsStatus == 2)
        {
            explanation = "Virtualization-based security is running.";
        }
        else if (vbsStatus == 1)
        {
            explanation = "Virtualization-based security is configured but not currently running - this typically just needs a reboot to take effect.";
        }
        else
        {
            var missing = new List<string>();
            if (hypervisorPresent == false) missing.Add("no hypervisor is currently active (virtualization may be disabled in firmware, or Hyper-V/VBS hasn't started)");
            if (vfwEnabled == false) missing.Add("the CPU reports firmware virtualization support is not enabled");
            if (!uefiKeyPresent) missing.Add("no UEFI Secure Boot registry state was found (likely a legacy BIOS boot - VBS requires UEFI)");
            if (secureBootEnabled == false) missing.Add("Secure Boot is off");

            explanation = missing.Count > 0
                ? $"Virtualization-based security isn't running - likely reason(s): {string.Join("; ", missing)}."
                : "Virtualization-based security isn't running (the prerequisites checked here look met - it may simply not be enabled by policy, or need a reboot).";
        }

        return new VbsDetailInfo
        {
            RequiredProperties = required,
            AvailableProperties = available,
            ConfiguredServices = configured,
            RunningServices = running,
            CredentialGuardRunning = credGuard,
            HypervisorPresent = hypervisorPresent,
            VirtualizationFirmwareEnabled = vfwEnabled,
            UefiSecureBootKeyPresent = uefiKeyPresent,
            SecureBootEnabled = secureBootEnabled,
            ExplanationText = explanation,
        };
    }

    private static bool? ReadHypervisorPresent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
                if (mo["HypervisorPresent"] is bool b) return b;
        }
        catch { /* fall through to Unknown */ }
        return null;
    }

    // Win32_Processor.VirtualizationFirmwareEnabled isn't exposed on every Windows build/CPU - read
    // defensively and degrade to Unknown rather than assume it's absent means "off", per #872's own
    // guidance to be defensive about this specific property.
    private static bool? ReadVirtualizationFirmwareEnabled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
                if (mo["VirtualizationFirmwareEnabled"] is bool b) return b;
        }
        catch { /* property not present on this build - fall through to Unknown */ }
        return null;
    }

    private static bool SecureBootStateKeyExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key is not null;
        }
        catch { return false; }
    }

    // ==================================================================================
    // #873: LSA protection (RunAsPPL) - display-only, no toggle action per the item's own text.
    // ==================================================================================

    public sealed class LsaProtectionInfo
    {
        public int? RunAsPPL { get; init; }
        public int? RunAsPPLBoot { get; init; }
        public string RunAsPPLText { get; init; } = "Unknown";
        public string RunAsPPLBootText { get; init; } = "Unknown";
        public IReadOnlyList<CodeIntegrityEvent> RelatedEvents { get; init; } = Array.Empty<CodeIntegrityEvent>();
        public bool EventLogAvailable { get; init; }
        public bool EventLogUnavailable => !EventLogAvailable;
    }

    private static string RunAsPplText(int? v) => v switch
    {
        null => "Not set (off by default)",
        0 => "Off",
        1 => "On",
        2 => "On, with UEFI lock",
        _ => $"Unknown ({v})",
    };

    private static LsaProtectionInfo BuildLsaProtection()
    {
        int? runAsPpl = null, runAsPplBoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Lsa");
            if (key?.GetValue("RunAsPPL") is int a) runAsPpl = a;
            if (key?.GetValue("RunAsPPLBoot") is int b) runAsPplBoot = b;
        }
        catch { /* denied - both stay null (Unknown/"not set" text) */ }

        var events = ReadCodeIntegrityEvents(new[] { 3065, 3033 }, MaxEventsPerQuery, out bool logAvailable);

        return new LsaProtectionInfo
        {
            RunAsPPL = runAsPpl,
            RunAsPPLBoot = runAsPplBoot,
            RunAsPPLText = RunAsPplText(runAsPpl),
            RunAsPPLBootText = RunAsPplText(runAsPplBoot),
            RelatedEvents = events,
            EventLogAvailable = logAvailable,
        };
    }

    // ==================================================================================
    // #874: Kernel DMA Protection / IOMMU. No single, well-documented WMI property exists for the
    // live enabled/disabled state of Kernel DMA Protection specifically - per the item's own
    // explicit permission, this degrades to an honest "Unknown" rather than guessing, after
    // attempting the one defensible secondary signal (Win32_DeviceGuard's AvailableSecurityProperties
    // listing "DMA protection" as an available hardware capability - NOT the same as confirming the
    // feature is actually turned on).
    // ==================================================================================

    public sealed class KernelDmaProtectionInfo
    {
        public string StatusText { get; init; } = "Unknown - no reliable programmatic source found.";
        public bool DmaProtectionListedAsAvailable { get; init; }
        public string IommuText { get; init; } = "Unknown";
    }

    private static KernelDmaProtectionInfo BuildKernelDmaProtection(DeviceGuardSnapshot dg)
    {
        bool dmaListed = dg.AvailableSecurityProperties is not null && dg.AvailableSecurityProperties.Contains(3u);
        bool? vfw = ReadVirtualizationFirmwareEnabled();
        string iommu = vfw switch
        {
            true => "The CPU reports firmware virtualization support enabled - a prerequisite for IOMMU-based protections, not direct confirmation of IOMMU/VT-d itself.",
            false => "The CPU reports firmware virtualization support not enabled.",
            null => "Unknown",
        };

        return new KernelDmaProtectionInfo
        {
            StatusText = dmaListed
                ? "Unknown - no reliable programmatic source for the live enabled/disabled state was found. Win32_DeviceGuard does list \"DMA protection\" as an available hardware security property on this machine, which is a capability signal only, not confirmation Kernel DMA Protection is actually turned on."
                : "Unknown - no reliable programmatic source found.",
            DmaProtectionListedAsAvailable = dmaListed,
            IommuText = iommu,
        };
    }

    // ==================================================================================
    // #875: Vulnerable-driver blocklist state, cross-referenced against a small curated list of
    // well-known "bring-your-own-vulnerable-driver" (BYOVD) filenames.
    // ==================================================================================

    private static readonly string[] KnownVulnerableDriverFileNames =
    {
        "RTCore64.sys", "RTCore32.sys",
        "WinRing0x64.sys", "WinRing0.sys",
        "Gdrv.sys", "DBUtil_2_3.sys",
        "AsIO.sys", "AsIO2.sys", "AsUpIO.sys", "AsUpIO64.sys",
        "MsIo64.sys", "MsIo32.sys", "PhymemX64.sys",
    };

    public sealed record VulnerableDriverMatch(string ServiceName, string FileName);

    public sealed class VulnerableDriverBlocklistInfo
    {
        public int? EnabledValue { get; init; }
        public string EnabledText { get; init; } = "Unknown";
        public IReadOnlyDictionary<string, int> OtherConfigValues { get; init; } = new Dictionary<string, int>();
        public IReadOnlyList<VulnerableDriverMatch> PossibleMatches { get; init; } = Array.Empty<VulnerableDriverMatch>();

        public string OtherConfigValuesText => OtherConfigValues.Count == 0
            ? "(no other DWORD values found under this key)"
            : string.Join(", ", OtherConfigValues.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static (VulnerableDriverBlocklistInfo Info, SecurityFinding? Finding) BuildVulnerableDriverBlocklist(List<AutorunEntry> kernelDrivers)
    {
        int? enabled = null;
        var others = new Dictionary<string, int>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Config");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not int i) continue;
                    if (valueName.Equals("VulnerableDriverBlocklistEnable", StringComparison.OrdinalIgnoreCase))
                        enabled = i;
                    else
                        others[valueName] = i; // whatever else is actually there (e.g. a policy-version value) - reported as-is
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent) - enabled stays null (Unknown), others stays empty.
        }

        var matches = new List<VulnerableDriverMatch>();
        foreach (var d in kernelDrivers)
        {
            if (string.IsNullOrWhiteSpace(d.ResolvedPath)) continue;
            var fileName = System.IO.Path.GetFileName(d.ResolvedPath);
            if (fileName.Length == 0) continue;
            if (KnownVulnerableDriverFileNames.Any(k => k.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                matches.Add(new VulnerableDriverMatch(d.Name, fileName));
        }

        var info = new VulnerableDriverBlocklistInfo
        {
            EnabledValue = enabled,
            EnabledText = enabled switch { null => "Unknown (value not found)", 0 => "Off", 1 => "On", _ => $"Unknown ({enabled})" },
            OtherConfigValues = others,
            PossibleMatches = matches,
        };

        SecurityFinding? finding = matches.Count == 0 ? null : new SecurityFinding
        {
            Severity = FindingSeverity.Medium,
            Title = $"Possible known-vulnerable driver loaded: {string.Join(", ", matches.Select(m => m.FileName))}",
            Reason = $"A loaded kernel driver's filename matches a small, curated list of publicly reported vulnerable/abused (\"bring-your-own-vulnerable-driver\") driver names: {string.Join(", ", matches.Select(m => $"{m.FileName} ({m.ServiceName})"))}. This is a filename match only, not a version or hash check - worth checking, not a confirmed exploit.",
            Path = @"HKLM\SYSTEM\CurrentControlSet\Services (Kernel Driver entries)",
            WhatDisablingDoes = "If you don't recognize the driver or the software that installed it, check it against Microsoft's published vulnerable driver blocklist and consider removing the software that installed it.",
        };

        return (info, finding);
    }

    // ==================================================================================
    // #876: Boot integrity switches, via bcdedit /enum {current}.
    // ==================================================================================

    private static readonly string[] BootIntegrityFields = { "testsigning", "nointegritychecks", "debug", "safeboot", "flightsigning" };

    public sealed class BootIntegrityInfo
    {
        public IReadOnlyDictionary<string, string> RawFields { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<string> OnFields { get; init; } = Array.Empty<string>();
        public bool CouldRun { get; init; }

        public string RawFieldsText => RawFields.Count == 0
            ? "(none of testsigning/nointegritychecks/debug/safeboot/flightsigning present in bcdedit output)"
            : string.Join(", ", RawFields.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static (BootIntegrityInfo Info, SecurityFinding? Finding) BuildBootIntegrity(List<AutorunEntry> kernelDrivers)
    {
        string output;
        try { output = RunCapturedSync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(10)); }
        catch { output = string.Empty; }

        if (string.IsNullOrWhiteSpace(output))
            return (new BootIntegrityInfo { CouldRun = false }, null);

        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            var m = BcdFieldLineRegex.Match(line);
            if (!m.Success) continue;
            var field = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (BootIntegrityFields.Any(f => f.Equals(field, StringComparison.OrdinalIgnoreCase)))
                raw[field] = value;
        }

        var onFields = raw.Where(kv => IsOnValue(kv.Value)).Select(kv => kv.Key).ToList();
        var info = new BootIntegrityInfo { RawFields = raw, OnFields = onFields, CouldRun = true };

        if (onFields.Count == 0) return (info, null);

        bool hasUnsignedKernelDriver = kernelDrivers.Any(d => d.SignatureStatus.Equals("Unsigned", StringComparison.OrdinalIgnoreCase));

        var finding = new SecurityFinding
        {
            Severity = FindingSeverity.High,
            Title = $"Boot configuration integrity switch(es) on: {string.Join(", ", onFields)}",
            Reason = $"bcdedit reports {string.Join(", ", onFields)} enabled on the current boot entry. These are typically turned on temporarily to install/test an unsigned driver and then left on by mistake - a real, findable exposure since it weakens what Windows will let load."
                + (hasUnsignedKernelDriver ? " This also explains the unsigned kernel driver(s) already flagged in the Persistence section's Kernel Driver entries." : string.Empty),
            Path = "bcdedit /enum {current}",
            WhatDisablingDoes = "From an elevated prompt, run \"bcdedit /set testsigning off\" (and the equivalent for any other switch listed here), then reboot - only if you don't have an active reason (e.g. actively developing/testing an unsigned driver) to keep it on.",
        };

        return (info, finding);
    }

    private static bool IsOnValue(string v) =>
        v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v.Equals("On", StringComparison.OrdinalIgnoreCase) || v == "1";

    // ==================================================================================
    // #877: App control policy presence - WDAC (.cip files + CodeIntegrityPolicyEnforcementStatus),
    // AppLocker (AppIDSvc + Get-AppLockerPolicy -Effective), Smart App Control.
    // ==================================================================================

    public sealed class AppControlInfo
    {
        public IReadOnlyList<string> WdacPolicyFiles { get; init; } = Array.Empty<string>();
        public string CodeIntegrityEnforcementText { get; init; } = "Unknown";
        public string AppIdSvcStatusText { get; init; } = "Unknown (not found or inaccessible)";
        public string AppLockerRulesText { get; init; } = "AppLocker rules: not checked.";
        public string SmartAppControlText { get; init; } = "Unknown";
        public int? SmartAppControlRawValue { get; init; }

        public string WdacPolicyFilesText => WdacPolicyFiles.Count == 0 ? "(no .cip files found)" : string.Join(", ", WdacPolicyFiles);
    }

    private static AppControlInfo BuildAppControl(DeviceGuardSnapshot dg)
    {
        var wdacFiles = new List<string>();
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CodeIntegrity", "CiPolicies", "Active");
            if (System.IO.Directory.Exists(dir))
                wdacFiles.AddRange(System.IO.Directory.GetFiles(dir, "*.cip").Select(System.IO.Path.GetFileName)!);
        }
        catch { /* directory inaccessible - empty list */ }

        string ciText = dg.CodeIntegrityPolicyEnforcementStatus switch
        {
            0 => "Off",
            1 => "Audit",
            2 => "Enforced",
            null => "Unknown",
            _ => $"Unknown ({dg.CodeIntegrityPolicyEnforcementStatus})",
        };

        string appIdStatus = "Unknown (not found or inaccessible)";
        try
        {
            using var sc = new ServiceController("AppIDSvc");
            appIdStatus = $"{sc.Status} (start type: {sc.StartType})";
        }
        catch { /* leave default */ }

        string appLockerText;
        try
        {
            var (exitCode, output) = RunCapturedWithExitCode("powershell.exe",
                "-NoProfile -Command \"Get-AppLockerPolicy -Effective -Xml\"", TimeSpan.FromSeconds(20));
            appLockerText = exitCode == 0 && output.Contains("<AppLockerPolicy", StringComparison.OrdinalIgnoreCase)
                ? $"Effective AppLocker policy read - approximately {CountAppLockerRules(output)} rule(s) found."
                : "AppLocker rules: could not be read (module unavailable or no policy set).";
        }
        catch
        {
            appLockerText = "AppLocker rules: could not be read (module unavailable or no policy set).";
        }

        int? sacRaw = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            if (key?.GetValue("VerifiedAndReputablePolicyState") is int v) sacRaw = v;
        }
        catch { /* leave null */ }

        string sacText = sacRaw switch
        {
            null => "Unknown (value not found)",
            0 => "Off",
            1 => "On (Enforced)",
            2 => "Evaluation mode",
            _ => $"Unknown (raw value {sacRaw})",
        };

        return new AppControlInfo
        {
            WdacPolicyFiles = wdacFiles,
            CodeIntegrityEnforcementText = ciText,
            AppIdSvcStatusText = appIdStatus,
            AppLockerRulesText = appLockerText,
            SmartAppControlText = sacText,
            SmartAppControlRawValue = sacRaw,
        };
    }

    private static int CountAppLockerRules(string xml)
    {
        try { return Regex.Matches(xml, "<FilePublisherRule|<FilePathRule|<FileHashRule", RegexOptions.IgnoreCase).Count; }
        catch { return 0; }
    }

    // ==================================================================================
    // #878: extended TPM detail card, complementing SystemSpecsService.ReadTpmStatus's existing
    // (smaller) System Specs card rather than replacing it.
    // ==================================================================================

    public sealed class TpmDetailInfo
    {
        public bool? Present { get; init; }
        public bool? IsOwned { get; init; }
        public bool? IsActivated { get; init; }
        public bool? IsEnabled { get; init; }
        public string SpecVersion { get; init; } = string.Empty;
        public string ManufacturerIdText { get; init; } = "Unknown";
        public string ManufacturerVersion { get; init; } = string.Empty;
        public string ManufacturerVersionInfo { get; init; } = string.Empty;
        public string LockoutCountText { get; init; } = "Not available via this read";

        /// <summary>Static "worth a manual check" caveat, shown whenever manufacturer+version info
        /// IS available - NOT a real version-range-against-advisory-database lookup (there's no
        /// live advisory feed this app checks against), per #878's own explicit framing.</summary>
        public bool ShowAdvisoryCaveat { get; init; }

        public string PresentText => TriStateText(Present);
        public string IsOwnedText => TriStateText(IsOwned);
        public string IsActivatedText => TriStateText(IsActivated);
        public string IsEnabledText => TriStateText(IsEnabled);
    }

    private static TpmDetailInfo BuildTpmDetail()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2\security\microsofttpm", "SELECT * FROM Win32_Tpm");
            foreach (ManagementObject mo in searcher.Get())
            {
                bool? activated = TryPropBool(mo, "IsActivated_InitialValue");
                bool? enabled = TryPropBool(mo, "IsEnabled_InitialValue");
                bool? owned = TryPropBool(mo, "IsOwned_InitialValue");
                string spec = TryPropString(mo, "SpecVersion");

                string manufacturerId = TryPropString(mo, "ManufacturerIdTxt");
                if (manufacturerId.Length == 0)
                {
                    try { if (mo["ManufacturerId"] is uint mid && mid != 0) manufacturerId = mid.ToString(); } catch { /* leave empty */ }
                }

                string mfgVersion = TryPropString(mo, "ManufacturerVersion");
                string mfgVersionInfo = TryPropString(mo, "ManufacturerVersionInfo");
                bool hasVersionInfo = manufacturerId.Length > 0 && (mfgVersion.Length > 0 || mfgVersionInfo.Length > 0);

                return new TpmDetailInfo
                {
                    Present = true,
                    IsOwned = owned,
                    IsActivated = activated,
                    IsEnabled = enabled,
                    SpecVersion = spec,
                    ManufacturerIdText = manufacturerId.Length > 0 ? manufacturerId : "Unknown",
                    ManufacturerVersion = mfgVersion,
                    ManufacturerVersionInfo = mfgVersionInfo,
                    LockoutCountText = "Not available via this read (would require invoking a Win32_Tpm WMI method this app doesn't attempt - see class remarks).",
                    ShowAdvisoryCaveat = hasVersionInfo,
                };
            }
            return new TpmDetailInfo { Present = false };
        }
        catch
        {
            // Most common cause: not elevated enough (shouldn't happen - this app runs elevated),
            // or a local policy denies WMI access to this namespace - "Unknown", not "absent".
            return new TpmDetailInfo { Present = null };
        }
    }

    private static bool? TryPropBool(ManagementBaseObject mo, string name)
    {
        try { return mo[name] as bool?; } catch { return null; }
    }

    private static string TryPropString(ManagementBaseObject mo, string name)
    {
        try { return (mo[name] as string ?? string.Empty).Trim(); } catch { return string.Empty; }
    }

    // ==================================================================================
    // #879: Secure Boot detail - setup mode + DBX recency, degrading WHOLESALE to a single
    // "legacy BIOS" message when the SecureBoot\State key itself doesn't exist.
    // ==================================================================================

    public sealed class SecureBootDetailInfo
    {
        public bool KeyPresent { get; init; }
        public bool? Enabled { get; init; }
        public bool? SetupMode { get; init; }
        public string DbxRecencyText { get; init; } = "Unknown - no reliable programmatic source found.";

        public bool KeyMissing => !KeyPresent;
        public string EnabledText => TriStateText(Enabled);
        public string SetupModeText => TriStateText(SetupMode);
    }

    private static SecureBootDetailInfo BuildSecureBootDetail()
    {
        bool keyPresent;
        int? setupMode = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            keyPresent = key is not null;
            if (key?.GetValue("SetupMode") is int sm) setupMode = sm;
        }
        catch { keyPresent = false; }

        if (!keyPresent)
            return new SecureBootDetailInfo { KeyPresent = false };

        bool? enabled = SystemSpecsService.ReadSecureBootEnabled();

        // DBX (revocation list) last-updated timestamp: no reliably documented registry source was
        // found for this specific fact (SecureBoot\Servicing and neighboring keys hold UEFI variable
        // bookkeeping, not a plain human-readable "last updated" date) - degrades honestly rather
        // than guess at an undocumented key, the same permission #874 takes.
        return new SecureBootDetailInfo
        {
            KeyPresent = true,
            Enabled = enabled,
            SetupMode = setupMode == 1,
            DbxRecencyText = "Unknown - no reliable programmatic source found.",
        };
    }

    // ==================================================================================
    // #880: UAC configuration audit.
    // ==================================================================================

    public sealed class UacConfigurationInfo
    {
        public int? EnableLUA { get; init; }
        public int? ConsentPromptBehaviorAdmin { get; init; }
        public int? ConsentPromptBehaviorUser { get; init; }
        public int? PromptOnSecureDesktop { get; init; }
        public int? FilterAdministratorToken { get; init; }
        public int? EnableInstallerDetection { get; init; }
        public string SliderPositionText { get; init; } = "Unknown";
    }

    private static (UacConfigurationInfo Info, SecurityFinding? Finding) BuildUacConfiguration()
    {
        int? lua = null, cpba = null, cpbu = null, psd = null, fat = null, eid = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            lua = key?.GetValue("EnableLUA") as int?;
            cpba = key?.GetValue("ConsentPromptBehaviorAdmin") as int?;
            cpbu = key?.GetValue("ConsentPromptBehaviorUser") as int?;
            psd = key?.GetValue("PromptOnSecureDesktop") as int?;
            fat = key?.GetValue("FilterAdministratorToken") as int?;
            eid = key?.GetValue("EnableInstallerDetection") as int?;
        }
        catch { /* leave all null - "Unknown"/"not set" everywhere below */ }

        // Windows' documented built-in defaults, used only to resolve the slider position when a
        // value is genuinely absent (not configured) rather than explicitly set to something else.
        int luaEff = lua ?? 1;
        int cpbaEff = cpba ?? 5;
        int psdEff = psd ?? 1;

        string slider =
            luaEff == 0 ? "Never notify" :
            luaEff == 1 && cpbaEff == 2 && psdEff == 1 ? "Always notify" :
            luaEff == 1 && cpbaEff == 5 && psdEff == 1 ? "Notify me only when apps try to make changes (default)" :
            luaEff == 1 && cpbaEff == 5 && psdEff == 0 ? "Notify me only when apps try to make changes (without dimming the desktop)" :
            luaEff == 1 && cpbaEff == 0 ? "Never notify (via ConsentPromptBehaviorAdmin=0, LUA still on)" :
            $"Custom configuration (EnableLUA={Fmt(lua)}, ConsentPromptBehaviorAdmin={Fmt(cpba)}, PromptOnSecureDesktop={Fmt(psd)})";

        var info = new UacConfigurationInfo
        {
            EnableLUA = lua,
            ConsentPromptBehaviorAdmin = cpba,
            ConsentPromptBehaviorUser = cpbu,
            PromptOnSecureDesktop = psd,
            FilterAdministratorToken = fat,
            EnableInstallerDetection = eid,
            SliderPositionText = slider,
        };

        SecurityFinding? finding = lua == 0 ? new SecurityFinding
        {
            Severity = FindingSeverity.High,
            Title = "User Account Control (UAC) is fully disabled",
            Reason = "EnableLUA is 0 under HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System. This doesn't just remove the elevation prompt - it silently breaks Windows Store apps and several Microsoft Defender features that assume UAC is active.",
            Path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\EnableLUA",
            WhatDisablingDoes = "Set EnableLUA back to 1 (or use the UAC slider in Control Panel > User Accounts) and reboot - there's almost no legitimate reason to run with UAC fully off on Windows 10/11.",
        } : null;

        return (info, finding);
    }

    private static string Fmt(int? v) => v?.ToString() ?? "not set";

    // ==================================================================================
    // #1084: thin adapters over the shared ToolRunner run/capture/kill-on-timeout
    // implementation, keeping this file's historical shapes (-1 exit code and empty
    // output for a timed-out run).
    // ==================================================================================

    private static string RunCapturedSync(string exe, string args, TimeSpan timeout) => RunCapturedWithExitCode(exe, args, timeout).Output;

    private static (int ExitCode, string Output) RunCapturedWithExitCode(string exe, string args, TimeSpan timeout)
    {
        var (output, exitCode) = ToolRunner.RunCaptured(exe, args, timeout);
        return (exitCode ?? -1, output);
    }
}
