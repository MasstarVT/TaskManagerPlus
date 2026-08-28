using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #774/#775: registry-only reads for the Windows Health tab's top "Pending reboot" card and its
/// "Update pause, defer and policy audit" card - both are pure registry reads (no event log, no
/// shell-out), so they share one service separate from WindowsUpdateHistoryService/
/// WindowsServicingService.
/// </summary>
public static class WindowsUpdatePolicyService
{
    #region #774 - Pending reboot detail panel

    private const string CbsBasePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    private const string WuAutoUpdatePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";
    private const string SessionManagerPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string NetlogonPath = @"SYSTEM\CurrentControlSet\Services\Netlogon";
    private const string ActiveComputerNamePath = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    private const string ComputerNamePath = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";

    /// <summary>
    /// #774: turns SystemSpecsService.ReadRebootPending's single bool into a detail list naming
    /// exactly which indicator fired, each with a plain-language explanation and (where the key
    /// itself could be opened) its last-write time. Same well-known indicator set the "Test-
    /// PendingReboot"-style community scripts check - CBS RebootPending/PackagesPending, the WU
    /// Auto Update client's own RebootRequired flag, PendingFileRenameOperations (with the actual
    /// queued file list), a pending computer-name change (ActiveComputerName vs ComputerName
    /// mismatch), a pending domain join/rename (Netlogon\JoinDomain), and UpdateExeVolatile (set by
    /// some installers that ran an .exe needing a reboot before they can finish). A denied/missing
    /// key just reads as "not active" for that one indicator, not an error for the whole panel.
    /// </summary>
    public static List<RebootPendingIndicator> ReadRebootPendingDetail()
    {
        var result = new List<RebootPendingIndicator>();

        result.Add(ReadKeyExistsIndicator(
            "CBS: RebootPending",
            $@"{CbsBasePath}\RebootPending",
            "A component-based servicing operation (most cumulative updates) needs a reboot to finish."));

        result.Add(ReadKeyExistsIndicator(
            "CBS: PackagesPending",
            $@"{CbsBasePath}\PackagesPending",
            "One or more servicing packages are queued and waiting for the next reboot to be processed."));

        result.Add(ReadKeyExistsIndicator(
            "Windows Update: RebootRequired",
            $@"{WuAutoUpdatePath}\RebootRequired",
            "The Windows Update Auto Update client flagged that an installed update needs a reboot."));

        result.Add(ReadPendingFileRenameIndicator());

        result.Add(ReadKeyExistsIndicator(
            "Pending domain join/rename (Netlogon\\JoinDomain)",
            NetlogonPath,
            "A domain join or computer rename is queued and waiting for the next reboot.",
            requiredValueName: "JoinDomain"));

        result.Add(ReadComputerNameMismatchIndicator());

        result.Add(ReadUpdateExeVolatileIndicator());

        return result;
    }

    private static RebootPendingIndicator ReadKeyExistsIndicator(string name, string keyPath, string activeDetail, string? requiredValueName = null)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            bool active = requiredValueName is null ? key is not null : key?.GetValue(requiredValueName) is not null;
            return new RebootPendingIndicator
            {
                Name = name,
                IsActive = active,
                Detail = active ? activeDetail : "Not currently set.",
                LastWriteTime = active ? TryGetLastWriteTime(key) : null,
            };
        }
        catch
        {
            // Denied/missing - "not active" rather than a false positive, same tradeoff
            // SystemSpecsService.ReadRebootPending already takes.
            return new RebootPendingIndicator { Name = name, IsActive = false, Detail = "Couldn't be read (access denied or not present)." };
        }
    }

    private static RebootPendingIndicator ReadPendingFileRenameIndicator()
    {
        const string name = "PendingFileRenameOperations";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionManagerPath);
            if (key?.GetValue(name) is string[] { Length: > 0 } pairs)
            {
                // REG_MULTI_SZ alternates source/destination (destination empty means "delete on
                // reboot") - show up to the first few pairs so the card stays readable even when
                // an installer queued dozens of files.
                var lines = new List<string>();
                for (int i = 0; i + 1 < pairs.Length && lines.Count < 8; i += 2)
                {
                    string source = pairs[i];
                    string dest = pairs[i + 1];
                    lines.Add(dest.Length == 0 ? $"Delete: {source}" : $"Rename: {source} -> {dest}");
                }
                int pairCount = pairs.Length / 2;
                string suffix = pairCount > lines.Count ? $" (+{pairCount - lines.Count} more)" : string.Empty;

                return new RebootPendingIndicator
                {
                    Name = "Session Manager: PendingFileRenameOperations",
                    IsActive = true,
                    Detail = string.Join("\n", lines) + suffix,
                    LastWriteTime = TryGetLastWriteTime(key),
                };
            }
        }
        catch
        {
            // fall through to "not active" below
        }
        return new RebootPendingIndicator { Name = "Session Manager: PendingFileRenameOperations", IsActive = false, Detail = "Not currently set." };
    }

    private static RebootPendingIndicator ReadComputerNameMismatchIndicator()
    {
        const string name = "Pending computer rename (ActiveComputerName vs ComputerName)";
        try
        {
            using var activeKey = Registry.LocalMachine.OpenSubKey(ActiveComputerNamePath);
            using var pendingKey = Registry.LocalMachine.OpenSubKey(ComputerNamePath);
            string? active = activeKey?.GetValue("ComputerName") as string;
            string? pending = pendingKey?.GetValue("ComputerName") as string;

            bool mismatch = active is not null && pending is not null &&
                !active.Equals(pending, StringComparison.OrdinalIgnoreCase);

            return new RebootPendingIndicator
            {
                Name = name,
                IsActive = mismatch,
                Detail = mismatch
                    ? $"Active name: {active} - pending name (takes effect on reboot): {pending}"
                    : "Not currently set.",
                LastWriteTime = mismatch ? TryGetLastWriteTime(pendingKey) : null,
            };
        }
        catch
        {
            return new RebootPendingIndicator { Name = name, IsActive = false, Detail = "Couldn't be read (access denied or not present)." };
        }
    }

    private static RebootPendingIndicator ReadUpdateExeVolatileIndicator()
    {
        const string name = "Session Manager: UpdateExeVolatile";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionManagerPath);
            bool active = key?.GetValue("UpdateExeVolatile") is int i && i != 0;
            return new RebootPendingIndicator
            {
                Name = name,
                IsActive = active,
                Detail = active
                    ? "An installer ran an executable that needs a reboot to finish before it can be cleaned up."
                    : "Not currently set.",
                LastWriteTime = active ? TryGetLastWriteTime(key) : null,
            };
        }
        catch
        {
            return new RebootPendingIndicator { Name = name, IsActive = false, Detail = "Couldn't be read (access denied or not present)." };
        }
    }

    /// <summary>RegistryKey has no managed API for a key's own last-write time - RegQueryInfoKey is
    /// the only way to get it, and there's no Windows tool that surfaces it either (CLAUDE.md's
    /// "raw P/Invoke reserved for cases with no tool or WMI class available at all"). Wrapped to
    /// return null on any failure rather than throw - a missing timestamp just means that field is
    /// left blank in the UI.</summary>
    private static DateTime? TryGetLastWriteTime(RegistryKey? key)
    {
        if (key is null) return null;
        try
        {
            int result = RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long fileTime);
            return result == 0 && fileTime > 0 ? DateTime.FromFileTime(fileTime) : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen,
        IntPtr lpcValues, IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    #endregion

    #region #775 - Update pause, defer and policy audit

    private const string UxSettingsPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string PolicyAuPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    /// <summary>#775: reads the pause/active-hours state Windows itself writes when a user pauses
    /// updates from Settings (UX\Settings) and the admin/Group-Policy-managed policy tree
    /// (Policies\...\WindowsUpdate, plus its \AU subkey - NoAutoUpdate and UseWUServer are
    /// documented to live under \AU specifically, even though the rest of the deferral/WSUS values
    /// live directly under WindowsUpdate). Composes SummaryText from whichever fields are actually
    /// set, since most machines have none of this configured at all.</summary>
    public static UpdatePolicySnapshot ReadPolicySnapshot()
    {
        using var uxKey = TryOpenKey(UxSettingsPath);
        using var policyKey = TryOpenKey(PolicyPath);
        using var auKey = TryOpenKey(PolicyAuPath);

        DateTime? pauseExpiry = ReadDateTime(uxKey, "PauseUpdatesExpiryTime");
        DateTime? featureStart = ReadDateTime(uxKey, "PauseFeatureUpdatesStartTime");
        DateTime? featureEnd = ReadDateTime(uxKey, "PauseFeatureUpdatesEndTime");
        DateTime? qualityStart = ReadDateTime(uxKey, "PauseQualityUpdatesStartTime");
        DateTime? qualityEnd = ReadDateTime(uxKey, "PauseQualityUpdatesEndTime");
        string? activeHoursStart = ReadHourString(uxKey, "ActiveHoursStart");
        string? activeHoursEnd = ReadHourString(uxKey, "ActiveHoursEnd");

        bool noAutoUpdate = ReadDwordBool(auKey, "NoAutoUpdate") ?? ReadDwordBool(policyKey, "NoAutoUpdate") ?? false;
        int? deferFeature = ReadDwordInt(policyKey, "DeferFeatureUpdatesPeriodInDays");
        int? deferQuality = ReadDwordInt(policyKey, "DeferQualityUpdatesPeriodInDays");
        string? targetRelease = policyKey?.GetValue("TargetReleaseVersionInfo") as string;
        string? wuServer = policyKey?.GetValue("WUServer") as string;
        bool useWuServer = ReadDwordBool(auKey, "UseWUServer") ?? ReadDwordBool(policyKey, "UseWUServer") ?? false;

        var partial = new UpdatePolicySnapshot
        {
            PauseUpdatesExpiryTime = pauseExpiry,
            PauseFeatureUpdatesStart = featureStart,
            PauseFeatureUpdatesEnd = featureEnd,
            PauseQualityUpdatesStart = qualityStart,
            PauseQualityUpdatesEnd = qualityEnd,
            ActiveHoursStart = activeHoursStart,
            ActiveHoursEnd = activeHoursEnd,
            NoAutoUpdate = noAutoUpdate,
            DeferFeatureUpdatesPeriodInDays = deferFeature,
            DeferQualityUpdatesPeriodInDays = deferQuality,
            TargetReleaseVersionInfo = string.IsNullOrWhiteSpace(targetRelease) ? null : targetRelease,
            WuServer = string.IsNullOrWhiteSpace(wuServer) ? null : wuServer,
            UseWuServer = useWuServer,
        };

        // SummaryText depends on every field above, and UpdatePolicySnapshot's properties are
        // init-only (like every other model in this app), so it's composed from the fields
        // directly here rather than re-reading them back off a constructed instance.
        return new UpdatePolicySnapshot
        {
            PauseUpdatesExpiryTime = partial.PauseUpdatesExpiryTime,
            PauseFeatureUpdatesStart = partial.PauseFeatureUpdatesStart,
            PauseFeatureUpdatesEnd = partial.PauseFeatureUpdatesEnd,
            PauseQualityUpdatesStart = partial.PauseQualityUpdatesStart,
            PauseQualityUpdatesEnd = partial.PauseQualityUpdatesEnd,
            ActiveHoursStart = partial.ActiveHoursStart,
            ActiveHoursEnd = partial.ActiveHoursEnd,
            NoAutoUpdate = partial.NoAutoUpdate,
            DeferFeatureUpdatesPeriodInDays = partial.DeferFeatureUpdatesPeriodInDays,
            DeferQualityUpdatesPeriodInDays = partial.DeferQualityUpdatesPeriodInDays,
            TargetReleaseVersionInfo = partial.TargetReleaseVersionInfo,
            WuServer = partial.WuServer,
            UseWuServer = partial.UseWuServer,
            SummaryText = BuildSummaryText(partial),
        };
    }

    /// <summary>#775: `PauseUpdatesExpiryTime`/etc are cleared under confirmation - the same "why
    /// hasn't this PC updated in 8 months" card's resume action. There is no documented CLI tool
    /// for this (unlike every other mutating action in this app), so this writes the registry
    /// values directly - the same values Settings' own "Resume updates" button clears - rather than
    /// reimplement a COM-based Windows Update Agent call for one narrow action.</summary>
    public static (bool Success, string? Error) ResumeUpdates()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UxSettingsPath, writable: true);
            if (key is null) return (false, "Couldn't open the WindowsUpdate\\UX\\Settings registry key (needs Administrator, and only exists once updates have been paused at least once).");

            foreach (var name in new[]
            {
                "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
                "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
            })
            {
                try { key.DeleteValue(name, throwOnMissingValue: false); }
                catch { /* best-effort per value - a partial clear is still progress */ }
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildSummaryText(UpdatePolicySnapshot s)
    {
        var reasons = new List<string>();

        if (s.PauseUpdatesExpiryTime is { } expiry && expiry > DateTime.Now)
            reasons.Add($"Updates are paused until {expiry:g}.");
        if (s.PauseFeatureUpdatesEnd is { } fEnd && fEnd > DateTime.Now)
            reasons.Add($"Feature updates are paused until {fEnd:g}.");
        if (s.PauseQualityUpdatesEnd is { } qEnd && qEnd > DateTime.Now)
            reasons.Add($"Quality updates are paused until {qEnd:g}.");
        if (s.NoAutoUpdate)
            reasons.Add("Automatic updates are disabled entirely by policy (NoAutoUpdate).");
        if (s.DeferFeatureUpdatesPeriodInDays is { } dFeat && dFeat > 0)
            reasons.Add($"Feature updates are deferred by policy for {dFeat} day(s) after release.");
        if (s.DeferQualityUpdatesPeriodInDays is { } dQual && dQual > 0)
            reasons.Add($"Quality updates are deferred by policy for {dQual} day(s) after release.");
        if (!string.IsNullOrEmpty(s.TargetReleaseVersionInfo))
            reasons.Add($"Policy pins this machine to Windows version \"{s.TargetReleaseVersionInfo}\" - it will not offer a feature update past that version.");
        if (s.UseWuServer && !string.IsNullOrEmpty(s.WuServer))
            reasons.Add($"Updates are managed by a WSUS server ({s.WuServer}) instead of Windows Update directly - if that server has nothing new, this PC won't either.");

        return reasons.Count == 0
            ? "No update pause, deferral, or WSUS policy found - nothing here is blocking updates."
            : string.Join(" ", reasons);
    }

    private static RegistryKey? TryOpenKey(string path)
    {
        try { return Registry.LocalMachine.OpenSubKey(path); }
        catch { return null; }
    }

    private static DateTime? ReadDateTime(RegistryKey? key, string valueName)
    {
        try
        {
            if (key?.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s) &&
                DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }
        }
        catch { /* ignore - treat as unset */ }
        return null;
    }

    private static string? ReadHourString(RegistryKey? key, string valueName)
    {
        try
        {
            if (key?.GetValue(valueName) is int hour) return $"{hour:00}:00";
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool? ReadDwordBool(RegistryKey? key, string valueName)
    {
        try
        {
            if (key?.GetValue(valueName) is int i) return i != 0;
        }
        catch { /* ignore */ }
        return null;
    }

    private static int? ReadDwordInt(RegistryKey? key, string valueName)
    {
        try
        {
            if (key?.GetValue(valueName) is int i) return i;
        }
        catch { /* ignore */ }
        return null;
    }

    #endregion

    #region #776 - WSUS / WUfB reachability check

    // Short timeout, and no protocol validation - this only ever answers "did something respond
    // at all", the exact same "just checking for a response vs. a connection failure/timeout"
    // scope PublicIpLookupService's own HttpClient already takes for an unrelated lookup. A single
    // static instance, reused across calls, is the documented way to use HttpClient (a new instance
    // per call risks socket exhaustion under repeated use).
    private static readonly HttpClient ReachabilityHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>#776: on-demand plain HTTP reachability check against a WUServer policy URL - a
    /// machine that once belonged to a domain (or that a "block Windows Update" script pointed at a
    /// dead/decommissioned address) silently fails every update with an 0x8024xxxx error, and this
    /// is the one-click way to confirm that's actually what's happening. Tries HEAD first (cheaper,
    /// and WSUS's IIS front end answers it); falls back to GET since some reverse proxies/firewalls
    /// don't implement HEAD. Any response at all - even a 404/500 - counts as "reachable": this is
    /// checking network/host reachability, not validating the WSUS protocol itself.</summary>
    public static async Task<WuServerReachabilityResult> CheckWuServerReachabilityAsync(string wuServerUrl)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(wuServerUrl) || !Uri.TryCreate(wuServerUrl, UriKind.Absolute, out var uri))
        {
            return new WuServerReachabilityResult { IsReachable = false, StatusText = $"\"{wuServerUrl}\" isn't a valid URL.", CheckedAt = now };
        }

        try
        {
            using var headResponse = await ReachabilityHttp.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
            return new WuServerReachabilityResult
            {
                IsReachable = true,
                StatusText = $"Reachable - HTTP {(int)headResponse.StatusCode} {headResponse.ReasonPhrase} from {uri.Host}.",
                CheckedAt = now,
            };
        }
        catch
        {
            // HEAD failed - could be a genuinely unreachable host, or a server that just doesn't
            // implement HEAD. Try GET before giving up.
        }

        try
        {
            using var getResponse = await ReachabilityHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            return new WuServerReachabilityResult
            {
                IsReachable = true,
                StatusText = $"Reachable - HTTP {(int)getResponse.StatusCode} {getResponse.ReasonPhrase} from {uri.Host}.",
                CheckedAt = now,
            };
        }
        catch (Exception ex)
        {
            return new WuServerReachabilityResult
            {
                IsReachable = false,
                StatusText = $"Unreachable - {ex.Message}. This silently blocks every update with an 0x8024xxxx error while UseWUServer is on.",
                CheckedAt = now,
            };
        }
    }

    #endregion
}
