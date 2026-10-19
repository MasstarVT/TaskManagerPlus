using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, items 81-84 and 88: shells out to verifier.exe and parses its text output, matching
/// this project's established "prefer a known Windows tool over raw interop" convention
/// (CLAUDE.md) - the same shape CrashDumpConfigService already uses for powercfg/manage-bde.
/// Command syntax below is taken directly from Microsoft's own "Driver Verifier Command Syntax"
/// reference; verifier.exe's free-form status text isn't a versioned contract the way its command
/// syntax is, so /query and /querysettings are parsed leniently (regex over known phrases/driver-
/// name patterns) and always degrade to "couldn't parse" rather than a guess, per CLAUDE.md's
/// "degrade to Unknown, never fabricate" - RawQueryOutput/RawSettingsOutput on
/// <see cref="DriverVerifierStatus"/> always carry the untouched tool output alongside the
/// best-effort summary so nothing is lost to a parsing miss.
///
/// Every mutating method here (Reset/ApplyStandard/ApplyVolatile/ApplySpecialPoolForTag) is a
/// real, consequential system change - per this chunk's own instructions, the UI is responsible
/// for the explicit confirmation/warning before calling any of them, the same "this service only
/// ever performs the write once asked to" split ForcedCrashService/CrashDumpConfigService already
/// use.
/// </summary>
public static class DriverVerifierService
{
    private const string PoolTagRegistryPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PoolTagValueName = "PoolTag";

    // Item 84: only these five bits are documented as usable with `verifier /volatile /flags` -
    // Special Pool and most other flags need a reboot to take effect. Kept as its own table
    // (rather than a subset check against PersistentFlags below) so the wizard's "apply without
    // restarting" picker can only ever offer options that will actually work.
    public static readonly IReadOnlyList<(uint Value, string Name)> VolatileFlagOptions = new List<(uint, string)>
    {
        (0x00000004, "Randomized Low Resources Simulation"),
        (0x00000020, "Deadlock Detection"),
        (0x00000080, "DMA Checking"),
        (0x00000200, "Force Pending I/O Requests"),
        (0x00000400, "IRP Logging"),
    };

    // Item 81/86: the full documented `/flags` bitmap, straight from Microsoft's "Driver Verifier
    // Command Syntax" reference - used to turn /querysettings' raw Level value into plain English.
    // Bits this table doesn't recognize (a future Windows release, or a combination this app
    // hasn't seen) fall back to a bare "bit 0x..." label rather than being silently dropped.
    // Internal (not private) because DriverVerifierControlService below decodes the same
    // VerifyDriverLevel bitmap and reuses this table rather than carrying a second copy.
    internal static readonly (uint Bit, string Name)[] FlagBits =
    {
        (0x00000001, "Special Pool"),
        (0x00000002, "Force IRQL Checking"),
        (0x00000004, "Low Resources Simulation"),
        (0x00000008, "Pool Tracking"),
        (0x00000010, "I/O Verification"),
        (0x00000020, "Deadlock Detection"),
        (0x00000040, "Enhanced I/O Verification"),
        (0x00000080, "DMA Verification"),
        (0x00000100, "Security Checks"),
        (0x00000200, "Force Pending I/O Requests"),
        (0x00000400, "IRP Logging"),
        (0x00000800, "Miscellaneous Checks"),
        (0x00002000, "Invariant MDL Checking for Stack"),
        (0x00004000, "Invariant MDL Checking for Driver"),
        (0x00008000, "Power Framework Delay Fuzzing"),
        (0x00010000, "Port/Miniport Interface Checking"),
        (0x00020000, "DDI Compliance Checking"),
        (0x00040000, "Systematic Low Resources Simulation"),
        (0x00080000, "DDI Compliance Checking (additional)"),
        (0x00200000, "NDIS/WIFI Verification"),
        (0x00800000, "Kernel Synchronization Delay Fuzzing"),
        (0x01000000, "VM Switch Verification"),
        (0x02000000, "Code Integrity Checks"),
    };

    /// <summary>Special Pool's own bit (0x1) - referenced directly by ApplySpecialPoolForTagAsync
    /// below, separate from the general FlagBits table since item 88 always uses exactly this one
    /// flag, never a combination.</summary>
    private const uint SpecialPoolFlag = 0x00000001;

    /// <summary>Items 81/86: reads both `verifier /query` (live activity) and `verifier
    /// /querysettings` (persistent settings for the next boot) and folds them into one status.</summary>
    public static async Task<DriverVerifierStatus> ReadStatusAsync()
    {
        var (queryOutput, queryExit) = await RunCapturedAsync("verifier.exe", "/query");
        var (settingsOutput, settingsExit) = await RunCapturedAsync("verifier.exe", "/querysettings");

        if (queryExit is null && settingsExit is null)
        {
            return new DriverVerifierStatus
            {
                QuerySucceeded = false,
                ErrorText = "Couldn't run verifier.exe - it should ship with every edition of Windows under System32; check that it's on the PATH.",
                StatusSummaryText = "Unknown - verifier.exe couldn't be run.",
            };
        }

        var verifiedDrivers = ExtractDriverNames(queryOutput);
        bool looksNotRunning = queryOutput.Contains("No drivers are currently verified", StringComparison.OrdinalIgnoreCase)
            || queryOutput.Contains("not currently active", StringComparison.OrdinalIgnoreCase)
            || queryOutput.Contains("Driver Verifier is not running", StringComparison.OrdinalIgnoreCase);
        bool isRunning = verifiedDrivers.Count > 0 && !looksNotRunning;

        uint? persistentFlags = ExtractLevelHex(settingsOutput);
        var persistentDrivers = ExtractDriverNames(settingsOutput);
        var flagsDescription = persistentFlags is { } f ? DescribeFlags(f) : new List<string>();

        string summary = isRunning
            ? $"Running - {verifiedDrivers.Count} driver{(verifiedDrivers.Count == 1 ? "" : "s")} currently verified: {string.Join(", ", verifiedDrivers)}."
            : "Not currently running - no drivers are being verified right now.";

        if (persistentDrivers.Count > 0 && !isRunning)
            summary += $" A persistent configuration for {persistentDrivers.Count} driver(s) is set for the next reboot.";

        return new DriverVerifierStatus
        {
            QuerySucceeded = true,
            IsRunning = isRunning,
            VerifiedDriverNames = verifiedDrivers,
            PersistentFlagsRaw = persistentFlags,
            PersistentFlagsDescription = flagsDescription,
            PersistentDriverNames = persistentDrivers,
            RawQueryOutput = queryOutput,
            RawSettingsOutput = settingsOutput,
            StatusSummaryText = summary,
        };
    }

    /// <summary>Item 82's one-click reset - `verifier /reset` clears every Driver Verifier setting
    /// (persistent and, per Microsoft's docs, the settings that would apply after the next boot);
    /// still requires a reboot to actually stop a currently-running verified session. `/reset`
    /// leaves the PoolTag value alone, so a successful reset also restores it (see
    /// <see cref="RestorePoolTagFromBackup"/>); PoolTagNote says what happened to it, if anything.</summary>
    public static async Task<(bool Ok, string? PoolTagNote)> ResetAsync()
    {
        var (_, exitCode) = await RunCapturedAsync("verifier.exe", "/reset");
        bool ok = exitCode is 0 or 2; // 2 = EXIT_CODE_REBOOT_NEEDED, still a successful reset
        return (ok, ok ? RestorePoolTagFromBackup() : null);
    }

    /// <summary>Item 83: loaded kernel drivers whose embedded Authenticode signature (or lack of
    /// one) doesn't say "Microsoft" - the pool this wizard's driver picker offers, since verifying
    /// Microsoft's own in-box drivers is rarely useful and dramatically increases overhead. Built
    /// from Win32_SystemDriver (a known WMI class listing every loaded driver service and its
    /// on-disk path) cross-checked against SignatureCheckService's existing embedded-signature
    /// vendor read - both already-established, reused infrastructure rather than a new text-tool
    /// parse. Best-effort like every signature check in this app: an unsigned or unreadable driver
    /// is treated as "non-Microsoft" (the conservative choice for this list's purpose - it's a
    /// candidate list to review, not an automatic decision).</summary>
    public static Task<List<NonMicrosoftDriverCandidate>> ListNonMicrosoftDriversAsync() => Task.Run(() =>
    {
        var result = new List<NonMicrosoftDriverCandidate>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PathName, State FROM Win32_SystemDriver WHERE State='Running'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string serviceName = (mo["Name"] as string ?? string.Empty).Trim();
                string path = (mo["PathName"] as string ?? string.Empty).Trim().Trim('"');
                if (serviceName.Length == 0 || path.Length == 0) continue;

                string fileName = Path.GetFileName(path);
                if (fileName.Length == 0 || !fileName.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) continue;

                string? vendor = SignatureCheckService.GetVendor(Environment.ExpandEnvironmentVariables(path));
                bool isMicrosoft = vendor is not null && vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                if (isMicrosoft) continue;

                if (result.Any(d => d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new NonMicrosoftDriverCandidate { FileName = fileName, ServiceName = serviceName, Vendor = vendor });
            }
        }
        catch
        {
            // WMI unavailable - degrade to an empty candidate list; the wizard's driver step just
            // shows "couldn't enumerate loaded drivers" rather than a guess.
        }
        return result.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    });

    /// <summary>Item 83: `verifier /standard /driver NAME [NAME ...]` - Microsoft's own recommended
    /// "standard" flag combination (Special Pool, Force IRQL Checking, Pool Tracking, I/O
    /// Verification, Deadlock Detection, DMA Verification, WDF Verification, Security Checks,
    /// Miscellaneous Checks, DDI Compliance Checking), scoped to only the given drivers. Takes
    /// effect after a reboot.</summary>
    public static async Task<(bool Ok, string Output)> ApplyStandardAsync(IEnumerable<string> driverFileNames)
    {
        var names = driverFileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (names.Count == 0) return (false, "No drivers were selected.");
        if (FirstInvalidDriverName(names) is { } bad) return (false, InvalidDriverNameMessage(bad));

        var (output, exitCode) = await RunCapturedAsync("verifier.exe", $"/standard /driver {string.Join(' ', names)}");
        return (exitCode is 0 or 2, output);
    }

    /// <summary>Item 84: `verifier /volatile /adddriver NAME [NAME ...]` starts verifying the given
    /// drivers immediately, no reboot - and, when a volatile-eligible flag is supplied, `verifier
    /// /volatile /flags &lt;n&gt;` changes that flag immediately too. Per Microsoft's docs only a
    /// handful of flags (<see cref="VolatileFlagOptions"/>) can be changed this way; Special Pool
    /// itself is NOT one of them; a volatile session started this way runs with whatever
    /// persistent flags are already configured (or Windows' own defaults if none are).</summary>
    public static async Task<(bool Ok, string Output)> ApplyVolatileAsync(IEnumerable<string> driverFileNames, uint? volatileFlags)
    {
        var names = driverFileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (names.Count == 0) return (false, "No drivers were selected.");
        if (FirstInvalidDriverName(names) is { } bad) return (false, InvalidDriverNameMessage(bad));

        var sb = new System.Text.StringBuilder();

        var (addOutput, addExit) = await RunCapturedAsync("verifier.exe", $"/volatile /adddriver {string.Join(' ', names)}");
        sb.AppendLine(addOutput);
        bool ok = addExit is 0 or 2;

        if (ok && volatileFlags is { } flags and > 0)
        {
            var (flagsOutput, flagsExit) = await RunCapturedAsync("verifier.exe", $"/volatile /flags {flags}");
            sb.AppendLine(flagsOutput);
            ok &= flagsExit is 0 or 2;
        }

        return (ok, sb.ToString());
    }

    /// <summary>Item 88: sets Session Manager\Memory Management\PoolTag - one REG_DWORD holding
    /// the tag's four ASCII bytes little-endian (gflags' own layout: "Tag1" is 0x31676154) - which
    /// makes the kernel put every allocation carrying that tag into a guard-paged "special pool"
    /// allocation that bugchecks (0xC1/0xC5-family) the instant a driver over/underruns it, naming
    /// the exact offender. Windows honours a single tag, so this replaces any previous one (the
    /// caller shows it first; the result text names it). Always needs a reboot. When a specific
    /// driver is known (PoolTagLookup's best-effort resolution) Verifier's Special Pool flag is
    /// also scoped to just that driver; with no driver the registry value alone is used -
    /// `verifier /flags 1 /all` would special-pool every allocation of every driver, and PoolTag
    /// does not narrow that.</summary>
    public static async Task<(bool Ok, string Output)> ApplySpecialPoolForTagAsync(string tag, string? driverFileName)
    {
        if (string.IsNullOrWhiteSpace(tag)) return (false, "No pool tag was given.");
        if (PackPoolTag(tag.Trim()) is not { } packed)
            return (false, $"'{tag}' isn't a valid pool tag (up to 4 printable ASCII characters).");
        if (!string.IsNullOrWhiteSpace(driverFileName) && FirstInvalidDriverName(new[] { driverFileName.Trim() }) is { } bad)
            return (false, InvalidDriverNameMessage(bad));

        string? previous = ReadCurrentPoolTag();
        int? previousDword = ReadPoolTagDword();
        var (createdBackup, backupError) = SavePoolTagBackupIfMissing(previousDword);
        // No change without a way back: a reset restores PoolTag from this backup.
        if (backupError is not null)
            return (false, $"Couldn't save a backup of the current PoolTag value, so nothing was changed: {backupError}");
        bool registryOk = WritePoolTag(packed);
        if (registryOk)
        {
            RegistryChangeJournalService.Record(
                source: "Stability",
                description: $"Set Special Pool tag '{tag.Trim()}'",
                hive: "HKLM",
                subKeyPath: PoolTagRegistryPath,
                valueName: PoolTagValueName,
                kind: RegistryValueKind.DWord,
                oldValueText: previousDword?.ToString(CultureInfo.InvariantCulture),
                newValueText: packed.ToString(CultureInfo.InvariantCulture));
        }
        else if (createdBackup)
        {
            DeletePoolTagBackup();
        }
        string registryText = registryOk
            ? "PoolTag registry value set" + (previous is not null ? $" (replaced previous tag '{previous}'). " : ". ")
            : "Couldn't write the PoolTag registry value. ";

        if (string.IsNullOrWhiteSpace(driverFileName))
            return (registryOk, registryText + "No owning driver was identified, so Driver Verifier settings were left unchanged.");

        var (output, exitCode) = await RunCapturedAsync("verifier.exe", $"/flags {SpecialPoolFlag} /driver {driverFileName.Trim()}");
        bool verifierOk = exitCode is 0 or 2;
        return (registryOk && verifierOk, registryText + output);
    }

    /// <summary>The tag PoolTag currently names, or null when unset/unreadable. Also accepts the
    /// REG_MULTI_SZ list earlier builds of this app wrote (which Windows ignores) and a REG_SZ.</summary>
    public static string? ReadCurrentPoolTag()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoolTagRegistryPath);
            return key?.GetValue(PoolTagValueName) switch
            {
                int dword => UnpackPoolTag(dword),
                string[] list => list.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                string s when !string.IsNullOrWhiteSpace(s) => s,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Space-pads/truncates to the 4 characters a pool tag is, then packs it the way
    /// gflags does. Null for anything outside printable ASCII.</summary>
    private static int? PackPoolTag(string tag)
    {
        string normalized = tag.Length >= 4 ? tag[..4] : tag.PadRight(4);
        if (normalized.Any(c => c < 0x20 || c > 0x7E)) return null;
        return BitConverter.ToInt32(System.Text.Encoding.ASCII.GetBytes(normalized), 0);
    }

    private static string UnpackPoolTag(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        return bytes.All(b => b >= 0x20 && b <= 0x7E)
            ? System.Text.Encoding.ASCII.GetString(bytes)
            : $"0x{value:X8}";
    }

    private static int? ReadPoolTagDword()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoolTagRegistryPath);
            return key?.GetValue(PoolTagValueName) is int dword ? dword : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- PoolTag backup: the value as it was before this app first set it, so a Driver Verifier
    // reset can put it back. Only the first write is backed up - a second Apply must not replace
    // the original with the app's own earlier tag. A legacy non-DWORD value (which Windows
    // ignores) is backed up as "absent". Signed and kept in the admin-only Backups area (see
    // RevertBackupStore): a reset writes it back to HKLM. ----

    private sealed class PoolTagBackup
    {
        public int? PreviousValue { get; set; }
    }

    private const string PoolTagBackupFileName = "pooltag-backup.json";
    private const string PoolTagBackupMacDomain = "pooltag-backup";

    private static string?[] PoolTagBackupMacFields(PoolTagBackup b) => new[] { b.PreviousValue?.ToString(CultureInfo.InvariantCulture) };

    private static PoolTagBackup? LoadPoolTagBackup(out string? problem)
        => RevertBackupStore.Load<PoolTagBackup>(PoolTagBackupFileName, PoolTagBackupMacDomain, PoolTagBackupMacFields, out problem);

    /// <summary>Created = this call wrote the backup; Error = it was needed and couldn't be written.
    /// A verified backup from an earlier Apply is kept - it holds the original value.</summary>
    private static (bool Created, string? Error) SavePoolTagBackupIfMissing(int? previousDword)
    {
        if (LoadPoolTagBackup(out _) is not null) return (false, null);
        var backup = new PoolTagBackup { PreviousValue = previousDword };
        string? error = RevertBackupStore.Save(PoolTagBackupFileName, PoolTagBackupMacDomain, backup, PoolTagBackupMacFields(backup));
        return (error is null, error);
    }

    private static void DeletePoolTagBackup() => RevertBackupStore.Delete(PoolTagBackupFileName);

    /// <summary>What a Driver Verifier reset will do to PoolTag, for its confirmation dialog - the
    /// exact value restored, or why nothing will be. Null when this app never changed it.</summary>
    public static string? DescribePendingPoolTagRestore()
    {
        var backup = LoadPoolTagBackup(out var problem);
        if (backup is null)
        {
            string? legacy = RevertBackupStore.LegacyNote(PoolTagBackupFileName);
            return problem is not null ? $"PoolTag is left as it is: {problem}"
                : legacy is not null ? $"PoolTag is left as it is. {legacy}"
                : null;
        }
        return backup.PreviousValue is { } p
            ? $"The Special Pool tag (PoolTag) is set back to '{UnpackPoolTag(p)}' (DWORD 0x{p:X8})."
            : "The PoolTag value this app set is removed (there was none before).";
    }

    /// <summary>Puts PoolTag back to what it was before this app first set it - deleting it when
    /// there was none - from the backup <see cref="ApplySpecialPoolForTagAsync"/> saved. Called by
    /// both Driver Verifier reset paths. Null when this app never changed the value.</summary>
    internal static string? RestorePoolTagFromBackup()
    {
        var backup = LoadPoolTagBackup(out var problem);
        if (backup is null)
            return problem is null ? null : $"{problem} The Special Pool tag was left as it is.";

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PoolTagRegistryPath, writable: true);
            if (key is null) return "Couldn't open the Memory Management key - the Special Pool tag was left as it is.";

            int? current = key.GetValue(PoolTagValueName) is int dword ? dword : null;
            if (backup.PreviousValue is { } previous) key.SetValue(PoolTagValueName, previous, RegistryValueKind.DWord);
            else key.DeleteValue(PoolTagValueName, throwOnMissingValue: false);

            RegistryChangeJournalService.Record(
                source: "Stability",
                description: "Restored the Special Pool tag after a Driver Verifier reset",
                hive: "HKLM",
                subKeyPath: PoolTagRegistryPath,
                valueName: PoolTagValueName,
                kind: RegistryValueKind.DWord,
                oldValueText: current?.ToString(CultureInfo.InvariantCulture),
                newValueText: backup.PreviousValue?.ToString(CultureInfo.InvariantCulture));

            DeletePoolTagBackup();
            return backup.PreviousValue is { } p
                ? $"The Special Pool tag was restored to '{UnpackPoolTag(p)}'."
                : "The Special Pool tag (PoolTag) this app set was removed.";
        }
        catch (Exception ex)
        {
            return $"Couldn't restore the Special Pool tag: {ex.Message}";
        }
    }

    private static bool WritePoolTag(int packed)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PoolTagRegistryPath, writable: true);
            if (key is null) return false;
            key.SetValue(PoolTagValueName, packed, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Best-effort ".sys" filename scan over either /query or /querysettings output -
    /// verifier.exe lists verified driver names as bare "name.sys" tokens (one per line, in a
    /// summary block), so this is a lenient regex rather than a strict per-tool output format
    /// parse, matching CLAUDE.md's tolerance for shelled-out tool text.</summary>
    private static List<string> ExtractDriverNames(string output)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(output, @"(?im)^\s*([A-Za-z0-9_\-\.]+\.sys)\s*$"))
        {
            string name = m.Groups[1].Value.Trim();
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    /// <summary>verifier.exe takes driver names as separate argv entries, and the argument string
    /// is split on spaces - so a name is only passed when it is a plain "name.sys" file name.</summary>
    internal static string? FirstInvalidDriverName(IEnumerable<string> names)
        => names.FirstOrDefault(n => !Regex.IsMatch(n, @"^[A-Za-z0-9_.\-]+\.sys$", RegexOptions.IgnoreCase));

    internal static string InvalidDriverNameMessage(string name)
        => $"'{name}' isn't a plain driver file name (name.sys) - nothing was changed.";

    /// <summary>Extracts /querysettings' "Level = 0x........" (or "Level: 0x...") hex value.</summary>
    private static uint? ExtractLevelHex(string output)
    {
        var m = Regex.Match(output, @"Level\s*[:=]\s*0x([0-9A-Fa-f]+)");
        if (m.Success && uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return v;
        return null;
    }

    private static List<string> DescribeFlags(uint flags)
    {
        var result = new List<string>();
        uint remaining = flags;
        foreach (var (bit, name) in FlagBits)
        {
            if ((flags & bit) != 0)
            {
                result.Add(name);
                remaining &= ~bit;
            }
        }
        if (remaining != 0) result.Add($"(unrecognized bits: 0x{remaining:X})");
        return result;
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>. Returns a null exit code
    /// (rather than throwing) when the tool itself couldn't be started at all (e.g. not present on
    /// this Windows edition) - callers treat null as "couldn't run", distinct from ran-and-failed
    /// (#1019's load-bearing distinction).</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        try { return await ToolRunner.RunCapturedAsync(exe, args, timeoutMs); }
        catch { return (string.Empty, null); }
    }
}

/// <summary>
/// #497/#498/#499: Driver Verifier status query, the standard-settings setup action, and the two
/// recovery actions (verifier /reset, bcdedit safe-boot toggle). Every mutating action here
/// (EnableStandardAsync/ResetAsync/SetSafeBootAsync) is destructive-adjacent - see
/// DevicesDriversViewModel's remarks and DriverVerifierSetupWindow for the mandatory warning/typed-
/// confirmation UI CLAUDE.md's safety-critical callout requires in front of every call into this
/// class's mutating methods. This service itself does no confirmation of its own - it trusts the
/// caller already got explicit, informed consent. Named "Control" here (rather than
/// plain DriverVerifierService) since the Round-19/items-81-88 DriverVerifierService above
/// already owns that name for the Stability tab's own, differently-shaped guided wizard -
/// both call the same verifier.exe/bcdedit tools independently rather than share one
/// implementation, since they were built for two different tabs with different status
/// models (DriverVerifierStatus vs DriverVerifierConfigStatus).
/// </summary>
public static class DriverVerifierControlService
{
    private const string MemoryManagementKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    // ------------------------------------------------------------------------------------------
    // #497: status.
    // ------------------------------------------------------------------------------------------

    public static async Task<DriverVerifierConfigStatus> QueryStatusAsync()
    {
        var (isConfigured, verifiesAll, driverNames, levelRaw) = ReadConfiguredState();

        string queryOutput;
        string? queryError = null;
        try
        {
            (queryOutput, int? exitCode) = await RunCapturedAsync("verifier.exe", "/query", timeoutMs: 15000);
            // A null exit code is a timed-out (killed) run - its output says nothing about whether
            // Verifier is active, so it must not read as a clean "not active".
            if (exitCode is null)
            {
                queryError = "verifier /query didn't finish in time and was stopped - whether Driver Verifier is active this boot is unknown.";
                queryOutput = string.Empty;
            }
        }
        catch (Exception ex)
        {
            queryOutput = string.Empty;
            queryError = ex.Message;
        }

        var activeDrivers = ParseActiveDrivers(queryOutput);

        return new DriverVerifierConfigStatus
        {
            IsConfigured = isConfigured,
            VerifiesAllDrivers = verifiesAll,
            ConfiguredDriverNames = driverNames,
            VerifyLevelRaw = levelRaw,
            EnabledChecks = DescribeFlags(levelRaw),
            IsActiveThisBoot = activeDrivers.Count > 0,
            ActiveDriverNames = activeDrivers,
            QueryError = queryError,
        };
    }

    private static (bool IsConfigured, bool VerifiesAll, List<string> DriverNames, uint LevelRaw) ReadConfiguredState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKeyPath);
            if (key is null) return (false, false, new List<string>(), 0);

            string? verifyDrivers = key.GetValue("VerifyDrivers") as string;
            uint level = key.GetValue("VerifyDriverLevel") switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                _ => 0u,
            };

            bool verifiesAll = verifyDrivers?.Trim() == "*";
            var names = string.IsNullOrWhiteSpace(verifyDrivers) || verifiesAll
                ? new List<string>()
                : verifyDrivers.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            bool isConfigured = verifiesAll || names.Count > 0;
            return (isConfigured, verifiesAll, names, level);
        }
        catch
        {
            return (false, false, new List<string>(), 0);
        }
    }

    private static List<string> DescribeFlags(uint level)
    {
        var descriptions = new List<string>();
        if (level == 0) return descriptions;

        uint remaining = level;
        foreach (var (flag, name) in DriverVerifierService.FlagBits)
        {
            if ((level & flag) == flag)
            {
                descriptions.Add(name);
                remaining &= ~flag;
            }
        }
        if (remaining != 0) descriptions.Add($"Unrecognized flag(s): 0x{remaining:X}");
        return descriptions;
    }

    /// <summary>`verifier /query` prints one ".sys (Description)" line per currently-verified
    /// driver under a "Verified drivers" heading, followed by a statistics section - this stops
    /// collecting names once it sees a line that looks like a statistics header rather than trying
    /// to parse the (much less stable) statistics table itself.</summary>
    private static List<string> ParseActiveDrivers(string output)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return names;
        if (output.Contains("No drivers", StringComparison.OrdinalIgnoreCase)) return names;

        var sysNameRegex = new Regex(@"^\s*([A-Za-z0-9_.\-]+\.sys)\b", RegexOptions.IgnoreCase);
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Contains("STATISTICS", StringComparison.OrdinalIgnoreCase)) break;
            var m = sysNameRegex.Match(line);
            if (m.Success && !names.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    // ------------------------------------------------------------------------------------------
    // #498: enable standard verification against the given driver file names.
    // ------------------------------------------------------------------------------------------

    public static Task<(bool Success, string Message)> EnableStandardAsync(IReadOnlyList<string> driverFileNames)
    {
        if (driverFileNames.Count == 0) return Task.FromResult((false, "No drivers selected."));
        if (DriverVerifierService.FirstInvalidDriverName(driverFileNames) is { } bad)
            return Task.FromResult((false, DriverVerifierService.InvalidDriverNameMessage(bad)));
        string args = $"/standard /driver {string.Join(' ', driverFileNames)}";
        return RunArgsAsync("verifier.exe", args, timeoutMs: 30000, rebootNeededExitCodeOk: true);
    }

    // ------------------------------------------------------------------------------------------
    // #499: recovery - verifier /reset (needs a reboot to take effect) and the bcdedit safe-boot
    // toggle for when the machine is already bugchecking on every normal boot.
    // ------------------------------------------------------------------------------------------

    public static async Task<(bool Success, string Message)> ResetAsync()
    {
        var (success, message) = await RunArgsAsync("verifier.exe", "/reset", timeoutMs: 30000, rebootNeededExitCodeOk: true);
        // `verifier /reset` leaves the PoolTag value alone - put back what the Stability tab's
        // Special Pool action changed, the same as that tab's own reset does.
        if (success && DriverVerifierService.RestorePoolTagFromBackup() is { } note)
            message = $"{message}\n{note}";
        return (success, message);
    }

    /// <summary>Reads whether {current}'s BCD entry currently has a safeboot value set, and which
    /// mode - `bcdedit /enum {current}` only ever prints a "safeboot" line when one is configured.</summary>
    public static async Task<(bool IsConfigured, string? Mode)> QuerySafeBootAsync()
    {
        string output;
        try
        {
            (output, _) = await RunCapturedAsync("bcdedit.exe", "/enum {current}", timeoutMs: 15000);
        }
        catch
        {
            return (false, null);
        }

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("safeboot", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                return (true, parts.Length > 1 ? parts[1].Trim() : null);
            }
        }
        return (false, null);
    }

    public static Task<(bool Success, string Message)> SetSafeBootAsync(bool enable) => enable
        ? RunArgsAsync("bcdedit.exe", "/set {current} safeboot minimal", timeoutMs: 15000)
        : RunArgsAsync("bcdedit.exe", "/deletevalue {current} safeboot", timeoutMs: 15000);

    // ------------------------------------------------------------------------------------------

    private static async Task<(bool Success, string Message)> RunArgsAsync(string exe, string args, int timeoutMs, bool rebootNeededExitCodeOk = false)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync(exe, args, timeoutMs);
            string trimmed = output.Trim();
            // A null exit code means the run timed out and was killed - never report that (or a
            // nonzero exit) as success: SetSafeBootAsync/ResetAsync callers act on this answer
            // while recovering from a Verifier bugcheck loop. verifier.exe exits 2 when the change
            // applied but needs a reboot (EXIT_CODE_REBOOT_NEEDED - see the sibling
            // DriverVerifierService.ResetAsync), which its callers opt into.
            bool ok = exitCode == 0 || (rebootNeededExitCodeOk && exitCode == 2);
            if (trimmed.Length == 0)
                trimmed = ok ? "Done." : $"The command failed (exit code {exitCode?.ToString() ?? "unknown"}) with no output.";
            return (ok, trimmed);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>. A null exit code means
    /// the run timed out and the process tree was killed - RunArgsAsync's success check treats
    /// that as failure (#1019).</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
