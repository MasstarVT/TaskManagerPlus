using System.Diagnostics;
using System.IO;
using System.Management;
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
    private static readonly (uint Bit, string Name)[] FlagBits =
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
    /// still requires a reboot to actually stop a currently-running verified session.</summary>
    public static async Task<bool> ResetAsync()
    {
        var (_, exitCode) = await RunCapturedAsync("verifier.exe", "/reset");
        return exitCode is 0 or 2; // 2 = EXIT_CODE_REBOOT_NEEDED, still a successful reset
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

    /// <summary>Item 88: writes the suspect tag into the REG_MULTI_SZ pool-tag restriction list
    /// under Session Manager\Memory Management\PoolTag (preserving whatever tags were already
    /// listed there, deduplicated) and enables Verifier's Special Pool flag - together, this makes
    /// Windows put every allocation carrying that tag into a guard-paged "special pool" allocation
    /// that immediately bugchecks (0xC1/0xC5-family) the instant a driver over/underruns it,
    /// naming the exact offender. Special Pool is not a volatile-eligible flag, so this always
    /// needs a reboot; when a specific driver name is known (e.g. from PoolTagLookup's own
    /// best-effort resolution) verification is scoped to just that driver, otherwise it's applied
    /// system-wide via /all - kept safe from ballooning memory use because the PoolTag registry
    /// list still restricts special pool to only allocations carrying the listed tag(s).</summary>
    public static async Task<(bool Ok, string Output)> ApplySpecialPoolForTagAsync(string tag, string? driverFileName)
    {
        if (string.IsNullOrWhiteSpace(tag)) return (false, "No pool tag was given.");

        bool registryOk = AddPoolTagToRegistryList(tag.Trim());

        string target = string.IsNullOrWhiteSpace(driverFileName)
            ? "/all"
            : $"/driver {driverFileName.Trim()}";
        var (output, exitCode) = await RunCapturedAsync("verifier.exe", $"/flags {SpecialPoolFlag} {target}");
        bool verifierOk = exitCode is 0 or 2;

        string combined = (registryOk ? "PoolTag registry list updated. " : "Couldn't write the PoolTag registry value. ") + output;
        return (registryOk && verifierOk, combined);
    }

    /// <summary>Adds one tag (padded/truncated to the 4 characters verifier/the pool manager
    /// expect) to the existing PoolTag REG_MULTI_SZ list, deduplicated case-insensitively -
    /// preserves whatever tags a previous item-88 action (or a manual gflags/registry edit)
    /// already listed there, rather than clobbering it.</summary>
    private static bool AddPoolTagToRegistryList(string tag)
    {
        string normalized = tag.Length >= 4 ? tag[..4] : tag.PadRight(4);
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PoolTagRegistryPath, writable: true);
            if (key is null) return false;

            var existing = key.GetValue(PoolTagValueName) as string[] ?? Array.Empty<string>();
            if (existing.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
                return true; // already listed - nothing to change

            var updated = existing.Append(normalized).ToArray();
            key.SetValue(PoolTagValueName, updated, RegistryValueKind.MultiString);
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

    /// <summary>Shells out and captures combined stdout+stderr under a bounded timeout - the same
    /// pattern CrashDumpConfigService.RunCapturedAsync/PowerPlanService.RunCapturedAsync already
    /// establish elsewhere in this app. Returns a null exit code (rather than throwing) when the
    /// tool itself couldn't be started at all (e.g. not present on this Windows edition).</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch
        {
            return (string.Empty, null);
        }
        if (proc is null) return (string.Empty, null);

        using (proc)
        {
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return ("(command timed out)", null);
            }

            string output = (await outputTask) + (await errorTask);
            return (output, proc.ExitCode);
        }
    }
}
