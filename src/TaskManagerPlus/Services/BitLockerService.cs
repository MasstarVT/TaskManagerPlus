using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #389-#393: BitLocker status card for the Storage tab. Win32_EncryptableVolume in
/// root\CIMV2\Security\MicrosoftVolumeEncryption - the same namespace/error-handling pattern
/// VolumeDiagnosticsService.ReadBitLockerStatus already established for its own single conversion-
/// status string (that method is left untouched; its one caller, SystemSpecsService's System Specs
/// volume table, keeps working unchanged). This is a separate, richer read of the same WMI class for
/// the new BitLocker card - key protector inventory, suspended-protection/auto-unlock detail, cipher/
/// hardware-encryption/live conversion percentage, a best-effort "why is it off" explanation, and
/// (on demand, via ReadRecoveryPromptHistory) recovery-prompt event history.
///
/// SECURITY: this file only ever reads key protector IDs (opaque GUIDs) and protector *types* -
/// it never calls GetKeyProtectorNumericalPassword or any other Win32_EncryptableVolume method that
/// would return the actual 48-digit recovery key, a passphrase, or any other secret key material.
/// </summary>
public static class BitLockerService
{
    // #389-391/#393: one-time-at-tab-load read for every fixed volume.
    //
    // The Task.Run wrapper is load-bearing: nothing below ever truly awaits, so without it the
    // whole read runs synchronously on the CALLER'S thread - and StorageViewModel's constructor
    // calls this during MainViewModel construction, on the UI thread, where connecting to the
    // MicrosoftVolumeEncryption WMI namespace measured ~5 seconds PER DRIVE in a non-elevated
    // process (access-denied discovery is slow). That one call was ~10s of the app's ~14.5s
    // time-to-first-window; elevated launches were "mysteriously" faster because the namespace
    // answers quickly when access succeeds.
    public static Task<List<BitLockerVolumeInfo>> ReadAllAsync() => Task.Run(async () =>
    {
        var result = new List<BitLockerVolumeInfo>();
        var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        foreach (var d in fixedDrives)
        {
            string driveLetter = d.Name.TrimEnd('\\'); // "C:" - matches Win32_EncryptableVolume.DriveLetter's format
            result.Add(await ReadOneAsync(driveLetter));
        }
        return result;
    });

    private static async Task<BitLockerVolumeInfo> ReadOneAsync(string driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}'");
            foreach (ManagementObject vol in searcher.Get())
                return await BuildVolumeInfoAsync(vol, driveLetter);

            // Query succeeded but found no instance for this drive letter - not a BitLocker-
            // capable volume, same "Not applicable" case VolumeDiagnosticsService.ReadBitLockerStatus
            // already treats this way.
            return new BitLockerVolumeInfo { DriveLetter = driveLetter, Available = false, UnavailableReason = "Not applicable to this volume." };
        }
        catch
        {
            // Namespace/method access denied (non-Enterprise/Pro edition, policy, ...) - "Unknown"
            // rather than a false "Off".
            return new BitLockerVolumeInfo { DriveLetter = driveLetter, Available = false, UnavailableReason = "Unknown - the BitLocker WMI namespace or method access was denied on this system." };
        }
    }

    private static async Task<BitLockerVolumeInfo> BuildVolumeInfoAsync(ManagementObject vol, string driveLetter)
    {
        // #389: key protector inventory - IDs and types only, see this class's remarks.
        var protectors = ReadKeyProtectors(vol);

        // #391: conversion status + live percentage - read before #390's suspended-protection
        // check below, since "suspended" only means something once we know BitLocker is actually
        // on (fully/partially encrypted), not simply never turned on.
        uint conversionStatus = 0;
        double? encryptionPercentage = null;
        try
        {
            var inC = vol.GetMethodParameters("GetConversionStatus");
            inC["PrecisionFactor"] = (uint)0;
            var outC = vol.InvokeMethod("GetConversionStatus", inC, null);
            if (outC?["ConversionStatus"] is not null) conversionStatus = Convert.ToUInt32(outC["ConversionStatus"]);
            if (outC?["EncryptionPercentage"] is not null) encryptionPercentage = Convert.ToUInt32(outC["EncryptionPercentage"]);
        }
        catch { /* leave 0 (treated as Off) / null percentage */ }

        string encryptionMethodText = string.Empty;
        try
        {
            var outE = vol.InvokeMethod("GetEncryptionMethod", vol.GetMethodParameters("GetEncryptionMethod"), null);
            if (outE?["EncryptionMethod"] is not null) encryptionMethodText = EncryptionMethodName(Convert.ToUInt32(outE["EncryptionMethod"]));
        }
        catch { /* leave empty */ }

        string hardwareEncryptionText = string.Empty;
        try
        {
            var outH = vol.InvokeMethod("GetHardwareEncryptionStatus", vol.GetMethodParameters("GetHardwareEncryptionStatus"), null);
            if (outH?["HardwareEncryptionStatus"] is not null) hardwareEncryptionText = HardwareEncryptionStatusName(Convert.ToUInt32(outH["HardwareEncryptionStatus"]));
        }
        catch { /* leave empty */ }

        // #390: protection status + suspend countdown + auto-unlock.
        uint protectionStatus = 2; // Unknown
        try
        {
            var outP = vol.InvokeMethod("GetProtectionStatus", vol.GetMethodParameters("GetProtectionStatus"), null);
            if (outP?["ProtectionStatus"] is not null) protectionStatus = Convert.ToUInt32(outP["ProtectionStatus"]);
        }
        catch { /* leave Unknown */ }

        // Per Win32_EncryptableVolume.GetProtectionStatus's own remarks: "if the disk is encrypted
        // and ProtectionStatus returns zero (PROTECTION OFF), keys are disabled" - i.e. suspended,
        // as opposed to a volume that was simply never turned on (conversionStatus == 0, protectors
        // empty) which isn't "suspended" at all.
        bool isSuspended = protectionStatus == 0 && conversionStatus != 0 && protectors.Count > 0;

        int? suspendCount = null;
        try
        {
            var outS = vol.InvokeMethod("GetSuspendCount", vol.GetMethodParameters("GetSuspendCount"), null);
            if (outS?["SuspendCount"] is not null) suspendCount = Convert.ToInt32(outS["SuspendCount"]);
        }
        catch { /* not the OS volume, or not currently suspended - leave null, not 0 */ }

        bool? autoUnlockEnabled = null;
        try
        {
            var outA = vol.InvokeMethod("IsAutoUnlockEnabled", vol.GetMethodParameters("IsAutoUnlockEnabled"), null);
            if (outA?["IsAutoUnlockEnabled"] is bool b) autoUnlockEnabled = b;
        }
        catch { /* not a data volume (e.g. the running OS volume) - leave null */ }

        bool? autoUnlockKeyStored = null;
        try
        {
            var outK = vol.InvokeMethod("IsAutoUnlockKeyStored", vol.GetMethodParameters("IsAutoUnlockKeyStored"), null);
            if (outK?["IsAutoUnlockKeyStored"] is bool b) autoUnlockKeyStored = b;
        }
        catch { /* leave null */ }

        // #393: only worth computing when BitLocker is actually off - a best-effort explanation,
        // frequently empty (the expected common case per this item's brief).
        string offReason = conversionStatus == 0 ? await ReadDeviceEncryptionBlockerReasonAsync(driveLetter) : string.Empty;

        return new BitLockerVolumeInfo
        {
            DriveLetter = driveLetter,
            Available = true,
            KeyProtectors = protectors,
            IsProtectionSuspended = isSuspended,
            SuspendCount = suspendCount,
            IsAutoUnlockEnabled = autoUnlockEnabled,
            IsAutoUnlockKeyStored = autoUnlockKeyStored,
            ConversionStatusText = ConversionStatusName(conversionStatus),
            ConversionStatusCode = (int)conversionStatus,
            EncryptionPercentage = encryptionPercentage,
            EncryptionMethodText = encryptionMethodText,
            HardwareEncryptionStatusText = hardwareEncryptionText,
            OffReasonText = offReason,
        };
    }

    /// <summary>#389: Win32_EncryptableVolume.GetKeyProtectors + GetKeyProtectorType. SECURITY:
    /// only the protector's opaque VolumeKeyProtectorID and its type code are ever read here -
    /// never GetKeyProtectorNumericalPassword or any other method that would return actual secret
    /// key material.</summary>
    private static List<BitLockerKeyProtectorInfo> ReadKeyProtectors(ManagementObject vol)
    {
        var protectors = new List<BitLockerKeyProtectorInfo>();
        try
        {
            var inParams = vol.GetMethodParameters("GetKeyProtectors");
            inParams["KeyProtectorType"] = (uint)0; // 0 = all types
            var outParams = vol.InvokeMethod("GetKeyProtectors", inParams, null);
            if (outParams?["VolumeKeyProtectorID"] is string[] ids)
            {
                foreach (var id in ids)
                {
                    string typeText = "Unknown";
                    try
                    {
                        var typeIn = vol.GetMethodParameters("GetKeyProtectorType");
                        typeIn["VolumeKeyProtectorID"] = id;
                        var typeOut = vol.InvokeMethod("GetKeyProtectorType", typeIn, null);
                        if (typeOut?["KeyProtectorType"] is not null)
                            typeText = KeyProtectorTypeName(Convert.ToUInt32(typeOut["KeyProtectorType"]));
                    }
                    catch { /* leave "Unknown" for this one protector - the ID itself still shows */ }

                    protectors.Add(new BitLockerKeyProtectorInfo { Id = id, TypeText = typeText });
                }
            }
        }
        catch { /* BitLocker not enabled on this volume (FVE_E_NOT_ACTIVATED), or method access denied - empty list */ }
        return protectors;
    }

    // Win32_EncryptableVolume.GetKeyProtectorType documented enum.
    private static string KeyProtectorTypeName(uint code) => code switch
    {
        0 => "Unknown/other",
        1 => "TPM",
        2 => "External key",
        3 => "Numerical password",
        4 => "TPM and PIN",
        5 => "TPM and startup key",
        6 => "TPM, PIN, and startup key",
        7 => "Public key",
        8 => "Passphrase",
        9 => "TPM certificate",
        10 => "CNG protector",
        _ => $"Type {code}",
    };

    // Win32_EncryptableVolume.GetConversionStatus's ConversionStatus documented enum.
    private static string ConversionStatusName(uint code) => code switch
    {
        0 => "Off",
        1 => "On",
        2 => "Encrypting",
        3 => "Decrypting",
        4 => "Encryption paused",
        5 => "Decryption paused",
        _ => "Unknown",
    };

    // Win32_EncryptableVolume.GetEncryptionMethod's EncryptionMethod documented enum.
    private static string EncryptionMethodName(uint code) => code switch
    {
        0 => "None",
        1 => "AES 128-bit with Diffuser",
        2 => "AES 256-bit with Diffuser",
        3 => "AES-CBC 128-bit",
        4 => "AES-CBC 256-bit",
        5 => "Hardware encryption",
        6 => "XTS-AES 128-bit",
        7 => "XTS-AES 256-bit",
        uint.MaxValue => "Unknown",
        _ => $"Method {code}",
    };

    // Win32_EncryptableVolume.GetHardwareEncryptionStatus documented enum.
    private static string HardwareEncryptionStatusName(uint code) => code switch
    {
        0 => "Not supported",
        1 => "No protection",
        2 => "Software (this drive's own controller isn't doing the encryption)",
        3 => "Hardware (this drive's own controller is doing the encryption)",
        _ => "Unknown",
    };

    // ================================================================================
    // #393: Device Encryption blocker report - only computed when a volume's conversion status is
    // Off. Two independent, non-fabricated signals: TPM readiness (directly verifiable via WMI) and
    // manage-bde's own text output (best-effort scanned for known blocker phrasing, per this item's
    // brief) - empty when neither finds anything, which is the expected common case.
    // ================================================================================

    private static readonly Regex BlockerLineRegex = new(
        @"\b(TPM|DMA|hardware security|not allowed|unsupported|not ready|SKU)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static async Task<string> ReadDeviceEncryptionBlockerReasonAsync(string driveLetter)
    {
        var reasons = new List<string>();

        string? tpmReason = ReadTpmBlockerReason();
        if (tpmReason is not null) reasons.Add(tpmReason);

        string? manageBdeReason = await ReadManageBdeBlockerReasonAsync(driveLetter);
        if (manageBdeReason is not null) reasons.Add(manageBdeReason);

        return string.Join(" ", reasons);
    }

    /// <summary>Same root\CIMV2\Security\MicrosoftTpm namespace SystemSpecsService.ReadTpmStatus
    /// already queries for the System Specs tab - a missing/not-ready TPM is both the single most
    /// common real-world reason automatic Device Encryption doesn't proceed, and directly
    /// verifiable rather than guessed.</summary>
    private static string? ReadTpmBlockerReason()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftTpm",
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, IsOwned_InitialValue FROM Win32_Tpm");
            foreach (ManagementObject tpm in searcher.Get())
            {
                bool enabled = tpm["IsEnabled_InitialValue"] is bool e && e;
                bool activated = tpm["IsActivated_InitialValue"] is bool a && a;
                bool owned = tpm["IsOwned_InitialValue"] is bool o && o;
                if (!enabled) return "No usable TPM: a TPM is present but not enabled in firmware.";
                if (!activated) return "No usable TPM: a TPM is present but not activated.";
                if (!owned) return "No usable TPM: a TPM is present but not yet provisioned/owned.";
                return null; // TPM looks ready - not the blocker
            }
            return "No TPM (Trusted Platform Module) detected - BitLocker/Device Encryption normally requires one.";
        }
        catch
        {
            return null; // Namespace/instance unavailable (e.g. denied on this edition) - degrade to no reason, never a guess.
        }
    }

    /// <summary>Best-effort scan of `manage-bde -status`'s own text for known blocker phrasing -
    /// absent on most systems/builds (manage-bde's status output doesn't always spell out *why*
    /// automatic Device Encryption didn't proceed), which is the expected common case per this
    /// item's brief: degrade to no reason, don't fabricate one.</summary>
    private static async Task<string?> ReadManageBdeBlockerReasonAsync(string driveLetter)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("manage-bde.exe", $"-status {driveLetter}", 5000);
            if (exitCode is null) return null;
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Key Protectors", StringComparison.OrdinalIgnoreCase)) continue;
                if (BlockerLineRegex.IsMatch(line)) return line;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ================================================================================
    // #392: recovery-prompt history - on-demand, event-log-scan gated behind an explicit button
    // (this app's "expensive scan -> explicit button" convention). Reads the BitLocker Management
    // and BitLocker-API operational logs, which are frequently disabled by default (need
    // `wevtutil sl <channel> /e:true` to enable) - a channel that doesn't exist/isn't enabled is the
    // expected common case here, not a bug, so every channel attempt below degrades to "found
    // nothing" rather than throwing past this method.
    // ================================================================================

    public const int RecoveryLookbackDays = 180;

    private sealed record RawRecoveryEvent(DateTime TimeCreated, string Channel, int EventId, string Message);
    private sealed record CorrelationEvent(DateTime TimeCreated, string Summary);

    public static List<BitLockerRecoveryPromptEvent> ReadRecoveryPromptHistory()
    {
        var raw = new List<RawRecoveryEvent>();

        // Event 24620/24621: "BitLocker Startup"/"BitLocker Volume Conversion" style entries in the
        // BitLocker Management log that include recovery-required notices.
        ReadChannelEvents(raw, new[] { "Microsoft-Windows-BitLocker/BitLocker Management" }, new[] { 24620, 24621 });

        // Event 845/846: recovery-key-usage entries in the BitLocker-API log. The exact channel
        // suffix for this log varies by build, so both plausible candidates are tried.
        ReadChannelEvents(raw, new[] { "Microsoft-Windows-BitLocker-API/Management", "Microsoft-Windows-BitLocker-API/Operational" }, new[] { 845, 846 });

        var result = new List<BitLockerRecoveryPromptEvent>();
        if (raw.Count == 0) return result;

        var correlationEvents = ReadTpmFirmwareCorrelationEvents(
            raw.Min(e => e.TimeCreated).AddDays(-3), raw.Max(e => e.TimeCreated).AddDays(3));

        foreach (var e in raw.OrderByDescending(e => e.TimeCreated))
        {
            // Heuristic: any TPM/firmware-related event within +/-2 days of a recovery prompt -
            // "likely cause, not proven" per this item's explicit framing, since a shared timestamp
            // window is a correlation, not a confirmed causal link.
            var nearby = correlationEvents
                .Where(c => Math.Abs((c.TimeCreated - e.TimeCreated).TotalDays) <= 2)
                .Select(c => c.Summary)
                .Distinct()
                .ToList();
            string cause = nearby.Count > 0
                ? "Likely cause (not proven - based only on nearby timestamps): " + string.Join("; ", nearby)
                : string.Empty;

            result.Add(new BitLockerRecoveryPromptEvent
            {
                TimeCreated = e.TimeCreated,
                Channel = e.Channel,
                EventId = e.EventId,
                Message = e.Message,
                LikelyCauseText = cause,
            });
        }
        return result;
    }

    private static void ReadChannelEvents(List<RawRecoveryEvent> into, string[] channelCandidates, int[] eventIds)
    {
        long maxAgeMs = RecoveryLookbackDays * 24L * 60 * 60 * 1000;
        string idFilter = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));

        foreach (var channel in channelCandidates)
        {
            bool foundAny = false;
            try
            {
                var query = new EventLogQuery(channel, PathType.LogName,
                    $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
                { ReverseDirection = true };

                using var reader = new EventLogReader(query);
                int count = 0;
                while (count < 100 && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        count++;
                        foundAny = true;
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                        into.Add(new RawRecoveryEvent(record.TimeCreated ?? DateTime.MinValue, channel, record.Id, Truncate(message, 400)));
                    }
                }
            }
            catch
            {
                // This candidate channel name doesn't exist, isn't enabled, or access was denied -
                // the expected common case for these operational logs. Try the next candidate name,
                // if any, rather than throwing.
            }
            if (foundAny) break;
        }
    }

    /// <summary>Broad, best-effort correlation signal - TPM provider events (any ID) plus Warning/
    /// Error-level Kernel-Boot events (firmware/Secure-Boot-adjacent) in the System log. Not tied to
    /// specific undocumented event IDs, since the correlation this feeds is already labelled "likely
    /// cause, not proven" wherever it's shown - a broad net here is consistent with that framing
    /// rather than overclaiming precision this data doesn't actually have.</summary>
    private static List<CorrelationEvent> ReadTpmFirmwareCorrelationEvents(DateTime from, DateTime to)
    {
        var result = new List<CorrelationEvent>();
        long maxAgeMs = Math.Max(60_000, (long)(DateTime.Now - from).TotalMilliseconds);

        ReadSystemProviderEvents(result, "Microsoft-Windows-TPM-WMI", maxAgeMs, "TPM-related event", levelFilter: false);
        ReadSystemProviderEvents(result, "Microsoft-Windows-Kernel-Boot", maxAgeMs, "Boot/firmware-related event", levelFilter: true);

        return result.Where(e => e.TimeCreated >= from && e.TimeCreated <= to).ToList();
    }

    private static void ReadSystemProviderEvents(List<CorrelationEvent> into, string providerName, long maxAgeMs, string label, bool levelFilter)
    {
        try
        {
            string levelClause = levelFilter ? " and (Level=2 or Level=3)" : string.Empty;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{providerName}']{levelClause} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < 50 && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    into.Add(new CorrelationEvent(record.TimeCreated ?? DateTime.MinValue, $"{label} (event {record.Id}, {providerName})"));
                }
            }
        }
        catch
        {
            // Provider unavailable on this system, or nothing logged in the window - contributes
            // nothing to the correlation.
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
