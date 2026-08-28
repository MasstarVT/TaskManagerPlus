using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 13, #802/#806-810: a single enumeration engine for persistence locations beyond the
/// classic Run key / Startup folder that StartupManagerService already covers - RunOnce, RunOnceEx,
/// RunServices, RunServicesOnce, the Group Policy Run keys (#806), the Winlogon shell chain
/// (#807), Winlogon Notify packages (#808), AppInit_DLLs (#809), and AppCertDlls (#810). Later
/// chunks (811-835) add more persistence categories to the same Scan() call rather than starting a
/// second engine, so the Security tab's Persistence section stays one sortable/filterable
/// DataGrid.
///
/// Every category method is wrapped in its own try/catch and degrades to contributing nothing on
/// failure (a denied key, an absent policy, ...) - the same "never fabricate" rule as every other
/// registry sweep in this app (see ShellExtensionService, StartupManagerService). Signature status
/// reuses SignatureCheckService's shared per-path cache rather than duplicating that check.
/// Publisher is left "Unknown" for this chunk - see AutorunEntry's remarks.
/// </summary>
public static class AutorunsService
{
    private const string WinlogonKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string WinlogonNotifyKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notify";
    private const string AppCertDllsKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";
    private const string IfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string SilentProcessExitKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";
    private const string SessionManagerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string KnownDllsKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs";
    private const string LsaKeyPath = @"SYSTEM\CurrentControlSet\Control\Lsa";

    private static readonly string[] AccessibilityBinaryNames = { "sethc.exe", "utilman.exe", "osk.exe", "narrator.exe", "magnify.exe" };

    /// <summary>Entry point used by the ViewModel when only the entry list is needed (e.g. the
    /// baseline compare flow, which re-scans without touching Findings).</summary>
    public static List<AutorunEntry> Scan() => Scan(out _);

    /// <summary>Primary entry point: runs every persistence-category scan and returns both the
    /// flat entry list (for the DataGrid) and the findings each heuristic raised along the way (for
    /// the detail pane) - see #804.</summary>
    public static List<AutorunEntry> Scan(out List<SecurityFinding> findings)
    {
        var entries = new List<AutorunEntry>();
        findings = new List<SecurityFinding>();

        AddRunOnceFamilyItems(entries);
        AddPolicyRunItems(entries);
        AddWinlogonShellChainItems(entries, findings);
        AddWinlogonNotifyItems(entries, findings);
        AddAppInitDllsItems(entries, findings);
        AddAppCertDllsItems(entries, findings);
        AddIfeoDebuggerItems(entries, findings);
        AddSilentProcessExitItems(entries, findings);
        AddAccessibilityHijackItems(entries, findings);
        AddSessionManagerExecutionItems(entries, findings);
        AddKnownDllsItems(entries, findings);
        AddLsaPackageItems(entries, findings);
        AddPrintSpoolerItems(entries, findings);
        AddNetshHelperItems(entries, findings);
        AddWinsockLspItems(entries, findings);
        AddMinifilterItems(entries, findings);
        AddKernelDriverItems(entries, findings);
        AddServicePathHygieneItems(entries, findings);
        AddWmiEventSubscriptionItems(entries, findings);
        AddActiveSetupItems(entries, findings);
        AddLegacyLogonItems(entries, findings);
        AddGroupPolicyScriptItems(entries, findings);
        AddBrowserHelperObjectItems(entries, findings);
        AddComHijackItems(entries, findings);
        AddShellVerbHijackItems(entries, findings);
        AddCodecAutoPlayScreensaverItems(entries, findings);
        AddScheduledTaskSecurityFindings(findings);
        AddBitsJobItems(entries, findings);
        AddOfficeAddinItems(entries, findings);
        AddProvisionedAppxPackageItems(entries);
        AddShortcutTamperingItems(entries, findings);

        return entries
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // #806: RunOnce/RunOnceEx/RunServices/RunServicesOnce - locations Task Manager's own Startup
    // tab never shows, on top of the plain Run key StartupManagerService already covers.
    private static void AddRunOnceFamilyItems(List<AutorunEntry> items)
    {
        AddFlatRegistryKeyItems(items, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce (HKCU)", "HKCU");
        AddFlatRegistryKeyItems(items, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce (HKLM)", "HKLM");
        AddFlatRegistryKeyItems(items, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce (HKLM, 32-bit)", "HKLM");

        AddRunOnceExItems(items, Registry.CurrentUser, "HKCU");
        AddRunOnceExItems(items, Registry.LocalMachine, "HKLM");

        AddFlatRegistryKeyItems(items, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunServices", "RunServices (HKCU)", "HKCU");
        AddFlatRegistryKeyItems(items, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunServices", "RunServices (HKLM)", "HKLM");
        AddFlatRegistryKeyItems(items, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce", "RunServicesOnce (HKCU)", "HKCU");
        AddFlatRegistryKeyItems(items, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce", "RunServicesOnce (HKLM)", "HKLM");
    }

    // #806: the Group Policy variants under ...\Policies\Explorer\Run - a separate mechanism from
    // the plain Run key, checked by Explorer independently of StartupApproved.
    private static void AddPolicyRunItems(List<AutorunEntry> items)
    {
        AddFlatRegistryKeyItems(items, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "Policy Run (HKCU)", "HKCU");
        AddFlatRegistryKeyItems(items, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "Policy Run (HKLM)", "HKLM");
    }

    /// <summary>Shared "key of value name -> command string" reader used by every Run-shaped
    /// location above - none of these respect the StartupApproved enable/disable flag the plain
    /// Run key does, so every entry found here is simply "present" (Enabled = true).</summary>
    private static void AddFlatRegistryKeyItems(List<AutorunEntry> items, RegistryKey hive, string keyPath, string category, string hiveLabel)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var valueName in key.GetValueNames())
            {
                var raw = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var displayName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName;
                items.Add(BuildEntry(category, displayName, raw, $@"{hiveLabel}\{keyPath}\{displayName}"));
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case for most of these on a clean system) -
            // contribute nothing from this location.
        }
    }

    /// <summary>RunOnceEx's documented shape nests one numbered subkey per install step, each
    /// holding its own "1", "2", ... command values (or a default value) rather than flat value
    /// names directly under the key - walked one level deep, which covers every real-world layout
    /// this mechanism actually uses.</summary>
    private static void AddRunOnceExItems(List<AutorunEntry> items, RegistryKey hive, string hiveLabel)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx";
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            // Flat value names directly under RunOnceEx (uncommon, but a valid shape too).
            foreach (var valueName in key.GetValueNames())
            {
                var raw = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var displayName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName;
                items.Add(BuildEntry("RunOnceEx", displayName, raw, $@"{hiveLabel}\{keyPath}\{displayName}"));
            }

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null) continue;

                    foreach (var valueName in sub.GetValueNames())
                    {
                        var raw = sub.GetValue(valueName) as string;
                        if (string.IsNullOrWhiteSpace(raw)) continue;

                        var displayName = string.IsNullOrEmpty(valueName) ? subName : $"{subName}\\{valueName}";
                        items.Add(BuildEntry("RunOnceEx", displayName, raw, $@"{hiveLabel}\{keyPath}\{subName}"));
                    }
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent) - contribute nothing.
        }
    }

    // #807: Winlogon shell chain - flags anything that isn't the stock Userinit/Shell value, or
    // any value at all in the other (normally-empty on modern Windows) legacy hook values.
    private static void AddWinlogonShellChainItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonKeyPath);
            if (key is null) return;

            string location = $@"HKLM\{WinlogonKeyPath}";
            AddWinlogonValue(items, findings, key, location, "Userinit");
            AddWinlogonValue(items, findings, key, location, "Shell");
            AddWinlogonValue(items, findings, key, location, "Taskman");
            AddWinlogonValue(items, findings, key, location, "System");
            AddWinlogonValue(items, findings, key, location, "VmApplet");
            AddWinlogonValue(items, findings, key, location, "AppSetup");
            AddWinlogonValue(items, findings, key, location, "GinaDLL");
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddWinlogonValue(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey key, string location, string valueName)
    {
        var raw = key.GetValue(valueName) as string;
        // Absent is the normal, expected case for every one of these except Userinit/Shell - only
        // surface a row (and possibly a finding) when there's actually something to look at.
        if (string.IsNullOrWhiteSpace(raw)) return;

        var entryLocation = $@"{location}\{valueName}";
        var entry = BuildEntry("Logon chain", valueName, raw, entryLocation);
        items.Add(entry);

        bool flagged;
        string reason;
        if (valueName == "Userinit")
        {
            var parts = raw.Split(',');
            bool endsWithUserinit = raw.TrimEnd().EndsWith("userinit.exe,", StringComparison.OrdinalIgnoreCase);
            bool hasExtraCommand = parts.Length > 2 || (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]));
            flagged = !endsWithUserinit || hasExtraCommand;
            reason = "Userinit should point to userinit.exe with nothing appended after the comma; a second command here runs alongside every interactive logon.";
        }
        else if (valueName == "Shell")
        {
            flagged = !raw.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
            reason = "Shell is normally the bare value \"explorer.exe\"; a different value replaces the entire desktop shell for every user who signs in.";
        }
        else
        {
            // Taskman/System/VmApplet/AppSetup/GinaDLL: any non-empty value at all is unusual on
            // modern Windows - these are Windows NT-era hooks with almost no legitimate use left.
            flagged = true;
            reason = $"{valueName} is normally empty on Windows 10/11; a value here is an old (or unusual) logon hook worth a manual check.";
        }

        if (!flagged) return;

        findings.Add(new SecurityFinding
        {
            Severity = FindingSeverity.Medium,
            Title = $"Winlogon {valueName} looks non-standard",
            Reason = reason,
            Path = entryLocation,
            WhatDisablingDoes = "Editing this value changes what runs for every interactive logon - restoring it to the stock value (or removing it, for the legacy keys) is safest if you don't recognize what's there. Quick flag, not a verdict - some legitimate accessibility/kiosk software does use these values.",
            RelatedEntry = entry,
        });
    }

    // #808: Winlogon Notify packages - a load-at-logon DLL hook with almost no legitimate
    // third-party use left, so any entry at all is worth surfacing.
    private static void AddWinlogonNotifyItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WinlogonNotifyKeyPath);
            if (key is null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    var dllName = sub?.GetValue("DllName") as string;
                    if (string.IsNullOrWhiteSpace(dllName)) continue;

                    var location = $@"HKLM\{WinlogonNotifyKeyPath}\{subName}";
                    var entry = BuildEntry("Winlogon Notify", subName, dllName, location);
                    items.Add(entry);

                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.High,
                        Title = $"Winlogon Notify package: {subName}",
                        Reason = "Winlogon Notify DLLs load into the logon process on every sign-in/lock/unlock; almost no legitimate third-party software still uses this mechanism on Windows 10/11, so any entry at all is worth a look.",
                        Path = location,
                        WhatDisablingDoes = "Deleting this subkey stops the DLL from loading at the next logon; only do this for a package you don't recognize, since a handful of older enterprise/accessibility tools still register one legitimately. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common modern case) - contribute nothing.
        }
    }

    // #809: AppInit_DLLs - loads every listed DLL into nearly every GUI process that loads
    // user32.dll when LoadAppInit_DLLs is set, one of the oldest system-wide injection hooks.
    private static void AddAppInitDllsItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddAppInitDllsForKey(items, findings, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit_DLLs");
        AddAppInitDllsForKey(items, findings, @"SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit_DLLs (32-bit)");
    }

    private static void AddAppInitDllsForKey(List<AutorunEntry> items, List<SecurityFinding> findings, string keyPath, string categoryLabel)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            var dlls = key.GetValue("AppInit_DLLs") as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dlls)) return; // empty is the normal, expected case

            bool loadAppInit = ReadDwordAsBool(key, "LoadAppInit_DLLs");
            bool requireSigned = ReadDwordAsBool(key, "RequireSignedAppInit_DLLs");
            string location = $@"HKLM\{keyPath}\AppInit_DLLs";

            // Per MSDN, entries are separated by spaces or (more commonly on modern Windows) semicolons.
            var dllPaths = dlls.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dllPath in dllPaths)
            {
                var entry = new AutorunEntry
                {
                    Category = categoryLabel,
                    Name = System.IO.Path.GetFileName(dllPath),
                    RawCommand = dlls,
                    ResolvedPath = dllPath,
                    Publisher = "Unknown",
                    SignatureStatus = SignatureCheckService.GetStatus(dllPath),
                    Location = location,
                    Enabled = loadAppInit,
                };
                items.Add(entry);
            }

            findings.Add(new SecurityFinding
            {
                Severity = loadAppInit ? FindingSeverity.High : FindingSeverity.Medium,
                Title = $"{categoryLabel} is not empty",
                Reason = loadAppInit
                    ? $"LoadAppInit_DLLs is enabled, so every listed DLL loads into nearly every GUI process that loads user32.dll ({(requireSigned ? "signed DLLs required" : "unsigned DLLs allowed")})."
                    : "LoadAppInit_DLLs is currently disabled, so these DLLs are listed but not actually loading - still worth checking why they're configured at all.",
                Path = location,
                WhatDisablingDoes = "Clearing the AppInit_DLLs value (or leaving LoadAppInit_DLLs at 0) stops these DLLs from injecting into user-mode processes; only do this for DLLs you don't recognize. Quick flag, not a verdict - some legitimate accessibility/IME/security software still uses this hook.",
            });
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    // #810: AppCertDlls - loaded into every process that calls CreateProcess; effectively always
    // empty on a clean system, so any value here is presented as "worth investigating".
    private static void AddAppCertDllsItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppCertDllsKeyPath);
            if (key is null) return;

            string location = $@"HKLM\{AppCertDllsKeyPath}";
            foreach (var valueName in key.GetValueNames())
            {
                var dllPath = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(dllPath)) continue;

                var displayName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName;
                var entry = new AutorunEntry
                {
                    Category = "AppCertDlls",
                    Name = displayName,
                    RawCommand = dllPath,
                    ResolvedPath = dllPath,
                    Publisher = "Unknown",
                    SignatureStatus = SignatureCheckService.GetStatus(dllPath),
                    Location = location,
                    Enabled = true,
                };
                items.Add(entry);

                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.High,
                    Title = $"AppCertDlls entry: {displayName}",
                    Reason = "AppCertDlls is effectively always empty on a clean system - any DLL listed here loads into every process on the machine that calls CreateProcess, making it worth investigating.",
                    Path = location,
                    WhatDisablingDoes = "Deleting this value stops the DLL from loading into new processes going forward; only do this for a DLL you don't recognize. Quick flag, not a verdict.",
                    RelatedEntry = entry,
                });
            }
        }
        catch
        {
            // Key inaccessible (or absent - the expected common case) - contribute nothing.
        }
    }

    // #811: Image File Execution Options - a Debugger value silently replaces what actually
    // launches when a given exe name starts. Both a common malware persistence trick and a common
    // leftover from a badly-uninstalled profiler/compat tool.
    private static void AddIfeoDebuggerItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(IfeoKeyPath);
            if (key is null) return;

            foreach (var exeName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(exeName);
                    var debugger = sub?.GetValue("Debugger") as string;
                    if (string.IsNullOrWhiteSpace(debugger)) continue;

                    var location = $@"HKLM\{IfeoKeyPath}\{exeName}\Debugger";
                    var entry = BuildEntry("IFEO Debugger", exeName, debugger, location);
                    items.Add(entry);

                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.High,
                        Title = $"IFEO Debugger hijacks {exeName}",
                        Reason = $"A Debugger value under Image File Execution Options silently replaces what actually launches when {exeName} starts - instead of {exeName}, \"{debugger}\" runs. This is both a common malware persistence trick and a common leftover from a badly-uninstalled profiler/compat tool.",
                        Path = location,
                        WhatDisablingDoes = "Deleting the Debugger value (or the whole subkey, if nothing else legitimate is under it) restores normal launch behavior for this exe name. Quick flag, not a verdict - some legitimate debugging/compat tools do use this mechanism deliberately.",
                        RelatedEntry = entry,
                    });
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case) - contribute nothing.
        }
    }

    private static string? TryGetIfeoDebugger(string exeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoKeyPath}\{exeName}");
            return key?.GetValue("Debugger") as string;
        }
        catch
        {
            return null;
        }
    }

    // #812a: SilentProcessExit - MonitorProcess silently launches a second program whenever the
    // named process exits (ReportingMode governs where the resulting WER-style event goes). A
    // niche mechanism with almost no legitimate third-party use, so any entry with MonitorProcess
    // set is worth a look.
    private static void AddSilentProcessExitItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SilentProcessExitKeyPath);
            if (key is null) return;

            foreach (var exeName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(exeName);
                    var monitorProcess = sub?.GetValue("MonitorProcess") as string;
                    if (string.IsNullOrWhiteSpace(monitorProcess)) continue;

                    var location = $@"HKLM\{SilentProcessExitKeyPath}\{exeName}\MonitorProcess";
                    var entry = BuildEntry("SilentProcessExit", exeName, monitorProcess, location);
                    items.Add(entry);

                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = $"SilentProcessExit monitor on {exeName}",
                        Reason = $"MonitorProcess silently launches \"{monitorProcess}\" every time {exeName} exits. This mechanism has almost no legitimate third-party use on Windows 10/11, so any entry here is worth a look.",
                        Path = location,
                        WhatDisablingDoes = "Deleting this subkey stops the monitor process from launching on the next exit of the named process; only do this for an entry you don't recognize. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case) - contribute nothing.
        }
    }

    // #812b: the classic pre-logon "sticky keys" backdoor pattern - either an IFEO Debugger hijack
    // on one of the five accessibility binaries launched from the lock screen before any sign-in,
    // or the on-disk binary itself having been swapped. NOTE: there is no reliable Windows API for
    // a real hash-against-known-good-binary check without shipping a hash database, so "swapped"
    // is approximated here as "missing from System32 entirely, or present but not carrying a valid
    // embedded Authenticode signature" - the same "good enough for a quick flag, not a verdict"
    // tradeoff SignatureCheckService's own remarks already describe (it can't see catalog
    // signatures either, so a false positive on a legitimately catalog-signed file is possible).
    private static void AddAccessibilityHijackItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        foreach (var exeName in AccessibilityBinaryNames)
        {
            var debugger = TryGetIfeoDebugger(exeName);
            if (!string.IsNullOrWhiteSpace(debugger))
            {
                var location = $@"HKLM\{IfeoKeyPath}\{exeName}\Debugger";
                var entry = BuildEntry("Accessibility hijack", exeName, debugger, location);
                items.Add(entry);

                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.High,
                    Title = $"Accessibility binary hijack: {exeName}",
                    Reason = $"{exeName} is one of the five accessibility binaries launchable from the Windows lock screen before any sign-in, and it has an Image File Execution Options Debugger set (\"{debugger}\") - the classic pre-logon backdoor pattern (sethc.exe \"sticky keys\" replacement, etc.).",
                    Path = location,
                    WhatDisablingDoes = "Deleting the Debugger value restores normal lock-screen behavior for this binary. Quick flag, not a verdict - a small number of legitimate accessibility/kiosk tools do hook this mechanism deliberately.",
                    RelatedEntry = entry,
                });
            }

            try
            {
                var onDiskPath = System.IO.Path.Combine(Environment.SystemDirectory, exeName);
                if (!System.IO.File.Exists(onDiskPath))
                {
                    // Missing entirely is unusual for a stock Windows binary - surface it, but as
                    // a lower-severity "worth checking" rather than a hijack claim.
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Low,
                        Title = $"Accessibility binary missing: {exeName}",
                        Reason = $"\"{onDiskPath}\" wasn't found on disk. Approximate check only (no hash-against-known-good-binary database is available here) - worth a manual check, not a confirmed tamper.",
                        Path = onDiskPath,
                        WhatDisablingDoes = "No action to take from this app alone - verify against a known-clean System32 (or an in-place repair) if you don't recognize the change.",
                    });
                    continue;
                }

                var status = SignatureCheckService.GetStatus(onDiskPath);
                if (!status.Equals("Signed", StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new AutorunEntry
                    {
                        Category = "Accessibility binary",
                        Name = exeName,
                        RawCommand = onDiskPath,
                        ResolvedPath = onDiskPath,
                        Publisher = "Unknown",
                        SignatureStatus = status,
                        Location = onDiskPath,
                        Enabled = true,
                    };
                    items.Add(entry);

                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.High,
                        Title = $"Accessibility binary not signed: {exeName}",
                        Reason = $"The on-disk copy of {exeName} in System32 doesn't carry a valid embedded Authenticode signature. Approximate check only - this compares against \"is it Microsoft-signed\", not a real hash against a known-good binary, so a false positive is possible (e.g. catalog-only signing). Still, an unsigned copy of a pre-logon accessibility binary is worth a manual check.",
                        Path = onDiskPath,
                        WhatDisablingDoes = "Compare this file against a known-clean System32 (or an in-place Windows repair) if you don't recognize the change. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
            catch
            {
                // File check failed (e.g. denied) - degrade to nothing for this half of the check.
            }
        }
    }

    // #813: Session Manager execution values - BootExecute/SetupExecute/Execute/S0InitialCommand
    // all run before (or very early after) the OS itself is fully up, with no visibility at all via
    // Task Manager's own Startup tab. Also surfaces PendingFileRenameOperations' actual contents as
    // a file-replacement-at-next-boot list - SystemSpecsService.ReadRebootPending already reads
    // this same value, but only tests it as a boolean reboot-pending flag; this is a NEW read for
    // the Security tab, not a change to that existing check.
    private static void AddSessionManagerExecutionItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionManagerKeyPath);
            if (key is null) return;

            string location = $@"HKLM\{SessionManagerKeyPath}";

            AddSessionManagerMultiSzValue(items, findings, key, location, "BootExecute",
                values => values.Length == 1 && values[0].Trim().Equals("autocheck autochk *", StringComparison.OrdinalIgnoreCase));
            AddSessionManagerMultiSzValue(items, findings, key, location, "SetupExecute", _ => false);
            AddSessionManagerMultiSzValue(items, findings, key, location, "Execute", _ => false);
            AddSessionManagerMultiSzValue(items, findings, key, location, "S0InitialCommand", _ => false);

            AddPendingFileRenameItems(items, key, location);
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddSessionManagerMultiSzValue(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey key, string location, string valueName, Func<string[], bool> isStock)
    {
        if (key.GetValue(valueName) is not string[] values || values.Length == 0) return;

        var raw = string.Join(" ; ", values);
        var entryLocation = $@"{location}\{valueName}";
        var entry = new AutorunEntry
        {
            Category = "Session Manager",
            Name = valueName,
            RawCommand = raw,
            ResolvedPath = string.Empty,
            Publisher = "Unknown",
            SignatureStatus = "Unknown",
            Location = entryLocation,
            Enabled = true,
        };
        items.Add(entry);

        if (isStock(values)) return;

        findings.Add(new SecurityFinding
        {
            Severity = FindingSeverity.Medium,
            Title = $"Session Manager {valueName} is non-standard",
            Reason = valueName == "BootExecute"
                ? $"BootExecute normally contains only \"autocheck autochk *\"; this system has \"{raw}\" instead, which runs before Windows itself is fully initialized."
                : $"{valueName} is normally absent entirely; this system has \"{raw}\" set, which runs very early in the boot/session sequence with no Task Manager Startup-tab visibility.",
            Path = entryLocation,
            WhatDisablingDoes = "Editing this value changes what runs during early boot/session init - only touch it if you don't recognize the command and understand the risk of an unbootable system. Quick flag, not a verdict.",
            RelatedEntry = entry,
        });
    }

    private static void AddPendingFileRenameItems(List<AutorunEntry> items, RegistryKey key, string location)
    {
        if (key.GetValue("PendingFileRenameOperations") is not string[] pairs || pairs.Length == 0) return;

        var entryLocation = $@"{location}\PendingFileRenameOperations";
        // Values come in source/destination pairs (an empty destination means "delete the source
        // at next boot") - the documented \??\ native-path prefix is stripped for readability.
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            var source = StripNativePrefix(pairs[i]);
            var dest = StripNativePrefix(pairs[i + 1]);
            var action = string.IsNullOrWhiteSpace(dest) ? "delete at next boot" : $"rename to {dest} at next boot";

            items.Add(new AutorunEntry
            {
                Category = "Pending File Rename",
                Name = System.IO.Path.GetFileName(source),
                RawCommand = $"{source} -> ({action})",
                ResolvedPath = source,
                Publisher = "Unknown",
                SignatureStatus = "Unknown",
                Location = entryLocation,
                Enabled = true,
            });
        }
    }

    private static string StripNativePrefix(string path) =>
        path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;

    // #814: KnownDLLs - Windows pre-maps these into every process's address space directly from
    // System32, bypassing the normal per-process DLL search order entirely. A value name/data pair
    // that doesn't match (e.g. "kernel32" pointing at something other than kernel32.dll), or a
    // DllDirectory pointed somewhere other than System32, is rare on a clean system and high-signal
    // when it happens.
    private static void AddKnownDllsItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KnownDllsKeyPath);
            if (key is null) return;

            string location = $@"HKLM\{KnownDllsKeyPath}";
            var expectedDllDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

            foreach (var valueName in key.GetValueNames())
            {
                if (valueName.Equals("DllDirectory", StringComparison.OrdinalIgnoreCase))
                {
                    var dllDirectory = key.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(dllDirectory)) continue;

                    var expanded = Environment.ExpandEnvironmentVariables(dllDirectory);
                    var loc = $@"{location}\DllDirectory";
                    var entry = new AutorunEntry
                    {
                        Category = "KnownDLLs",
                        Name = "DllDirectory",
                        RawCommand = dllDirectory,
                        ResolvedPath = expanded,
                        Publisher = "Unknown",
                        SignatureStatus = "Unknown",
                        Location = loc,
                        Enabled = true,
                    };
                    items.Add(entry);

                    if (!expanded.Equals(expectedDllDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.High,
                            Title = "KnownDLLs DllDirectory is non-standard",
                            Reason = $"DllDirectory is normally \"{expectedDllDirectory}\"; this system has \"{dllDirectory}\" instead, which changes where every KnownDLLs entry actually loads from.",
                            Path = loc,
                            WhatDisablingDoes = "Restoring DllDirectory to the stock System32 path (or deleting the value) is safest if you don't recognize the change. Quick flag, not a verdict.",
                            RelatedEntry = entry,
                        });
                    }
                    continue;
                }

                var dllFileName = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(dllFileName)) continue;

                var nameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(dllFileName);
                var entryLocation = $@"{location}\{valueName}";
                var mainEntry = new AutorunEntry
                {
                    Category = "KnownDLLs",
                    Name = valueName,
                    RawCommand = dllFileName,
                    ResolvedPath = System.IO.Path.Combine(Environment.SystemDirectory, dllFileName),
                    Publisher = "Unknown",
                    SignatureStatus = "Unknown",
                    Location = entryLocation,
                    Enabled = true,
                };
                items.Add(mainEntry);

                if (!nameWithoutExtension.Equals(valueName, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.High,
                        Title = $"KnownDLLs mismatch: {valueName}",
                        Reason = $"The KnownDLLs value name \"{valueName}\" is expected to map to \"{valueName}.dll\", but it actually maps to \"{dllFileName}\" - every process on the system loads this file whenever it needs a DLL by that name.",
                        Path = entryLocation,
                        WhatDisablingDoes = "Correcting this value back to the expected filename is safest if you don't recognize the change. Quick flag, not a verdict - rare, but high-signal.",
                        RelatedEntry = mainEntry,
                    });
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    // #815: LSA package audit - Authentication/Security/Notification Packages are how a
    // third-party password filter or authentication provider registers itself with lsass.exe.
    // Cross-references against lsass's actually-loaded modules on a best-effort basis only -
    // module enumeration on lsass can throw Access Denied even elevated in some configurations, in
    // which case the cross-reference is skipped entirely and the registered packages are still
    // listed on their own. A registered-but-not-loaded package is informational, not necessarily
    // bad - many of these are lazy-loaded and only appear once actually used.
    private static void AddLsaPackageItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LsaKeyPath);
            if (key is null) return;

            string location = $@"HKLM\{LsaKeyPath}";
            var loadedModules = TryGetLsassLoadedModuleNames();

            AddLsaPackageValue(items, findings, key, location, "Authentication Packages", loadedModules);
            AddLsaPackageValue(items, findings, key, location, "Security Packages", loadedModules);
            AddLsaPackageValue(items, findings, key, location, "Notification Packages", loadedModules);

            try
            {
                using var extKey = key.OpenSubKey("LsaExtensionConfig");
                if (extKey is not null)
                {
                    foreach (var subName in extKey.GetSubKeyNames())
                    {
                        items.Add(new AutorunEntry
                        {
                            Category = "LSA Extension",
                            Name = subName,
                            RawCommand = subName,
                            ResolvedPath = string.Empty,
                            Publisher = "Unknown",
                            SignatureStatus = "Unknown",
                            Location = $@"{location}\LsaExtensionConfig\{subName}",
                            Enabled = true,
                        });
                    }
                }
            }
            catch
            {
                // Subkey inaccessible (or absent - the common case) - skip.
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddLsaPackageValue(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey key, string location, string valueName, HashSet<string>? loadedModules)
    {
        if (key.GetValue(valueName) is not string[] packages || packages.Length == 0) return;

        var entryLocation = $@"{location}\{valueName}";
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package)) continue;

            var entry = new AutorunEntry
            {
                Category = "LSA Package",
                Name = $"{valueName}: {package}",
                RawCommand = package,
                ResolvedPath = string.Empty,
                Publisher = "Unknown",
                SignatureStatus = "Unknown",
                Location = entryLocation,
                Enabled = true,
            };
            items.Add(entry);

            if (loadedModules is null) continue; // cross-reference unavailable - list only

            var expectedDllName = package.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? package : package + ".dll";
            if (!loadedModules.Contains(expectedDllName))
            {
                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.Info,
                    Title = $"{valueName.TrimEnd('s')} registered but not loaded: {package}",
                    Reason = $"\"{package}\" is registered under {valueName}, but \"{expectedDllName}\" wasn't found among lsass.exe's currently loaded modules. Informational only - many packages are lazy-loaded and only appear in lsass's module list once actually used.",
                    Path = entryLocation,
                    WhatDisablingDoes = "No action implied - this is a cross-reference observation, not a flag of anything wrong.",
                    RelatedEntry = entry,
                });
            }
        }
    }

    private static HashSet<string>? TryGetLsassLoadedModuleNames()
    {
        System.Diagnostics.Process[] processes;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName("lsass");
        }
        catch
        {
            return null;
        }

        if (processes.Length == 0) return null;

        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in processes)
            {
                try
                {
                    foreach (System.Diagnostics.ProcessModule module in proc.Modules)
                    {
                        names.Add(module.ModuleName);
                    }
                }
                catch
                {
                    // Access Denied on this process's module list - a known lsass limitation even
                    // when elevated, in some configurations. Skip just this process.
                }
            }
            return names.Count > 0 ? names : null;
        }
        catch
        {
            // Module enumeration failed entirely - skip the cross-reference, still list packages.
            return null;
        }
        finally
        {
            foreach (var proc in processes) proc.Dispose();
        }
    }

    // #816: print monitors/processors/providers - spooler-loaded DLLs; spoolsv.exe runs as SYSTEM,
    // making this a classic-but-still-live persistence/privesc target (PrintNightmare and its
    // relatives all rode this same category of registration). Driver values here are usually a
    // bare DLL filename rather than a full path, so each is resolved against the relevant System32
    // subfolder before checking its signature; a resolved file that doesn't exist still gets a row
    // with SignatureStatus "Unknown" rather than being silently dropped.
    private static void AddPrintSpoolerItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddPrintMonitorItems(items, findings);
        AddPrintProcessorItems(items, findings);
        AddPrintProviderItems(items);
    }

    private static void AddPrintMonitorItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\Print\Monitors";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    var driver = sub?.GetValue("Driver") as string;
                    if (string.IsNullOrWhiteSpace(driver)) continue;

                    var resolved = System.IO.Path.IsPathRooted(driver) ? driver : System.IO.Path.Combine(Environment.SystemDirectory, driver);
                    var location = $@"HKLM\{keyPath}\{subName}\Driver";
                    AddSpoolerEntry(items, findings, "Print Monitor", subName, driver, resolved, location);
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddPrintProcessorItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\Print\Environments";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var envName in key.GetSubKeyNames())
            {
                try
                {
                    using var envKey = key.OpenSubKey($@"{envName}\Print Processors");
                    if (envKey is null) continue;

                    foreach (var procName in envKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var procKey = envKey.OpenSubKey(procName);
                            var driver = procKey?.GetValue("Driver") as string;
                            if (string.IsNullOrWhiteSpace(driver)) continue;

                            // Print processor DLLs live under a per-architecture prtprocs
                            // subfolder, not directly in System32 - the folder name follows the
                            // environment subkey's own architecture naming.
                            var archFolder = envName.Contains("x64", StringComparison.OrdinalIgnoreCase) ? "w64x86"
                                : envName.Contains("Itanium", StringComparison.OrdinalIgnoreCase) ? "ia64"
                                : "w32x86";
                            var resolved = System.IO.Path.IsPathRooted(driver)
                                ? driver
                                : System.IO.Path.Combine(Environment.SystemDirectory, "spool", "prtprocs", archFolder, driver);
                            var location = $@"HKLM\{keyPath}\{envName}\Print Processors\{procName}\Driver";
                            AddSpoolerEntry(items, findings, "Print Processor", procName, driver, resolved, location);
                        }
                        catch
                        {
                            // One bad subkey shouldn't stop the rest.
                        }
                    }
                }
                catch
                {
                    // One bad environment subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddPrintProviderItems(List<AutorunEntry> items)
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\Print\Providers";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                items.Add(new AutorunEntry
                {
                    Category = "Print Provider",
                    Name = subName,
                    RawCommand = subName,
                    ResolvedPath = string.Empty,
                    Publisher = "Unknown",
                    SignatureStatus = "Unknown",
                    Location = $@"HKLM\{keyPath}\{subName}",
                    Enabled = true,
                });
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void AddSpoolerEntry(List<AutorunEntry> items, List<SecurityFinding> findings, string category, string name, string rawDriver, string resolvedPath, string location)
    {
        var status = System.IO.File.Exists(resolvedPath) ? SignatureCheckService.GetStatus(resolvedPath) : "Unknown";
        var entry = new AutorunEntry
        {
            Category = category,
            Name = name,
            RawCommand = rawDriver,
            ResolvedPath = resolvedPath,
            Publisher = "Unknown",
            SignatureStatus = status,
            Location = location,
            Enabled = true,
        };
        items.Add(entry);

        if (status.Equals("Signed", StringComparison.OrdinalIgnoreCase)) return;

        findings.Add(new SecurityFinding
        {
            Severity = status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase) ? FindingSeverity.Medium : FindingSeverity.Low,
            Title = $"{category} DLL is {status.ToLowerInvariant()}: {name}",
            Reason = $"{category} \"{name}\" loads \"{rawDriver}\" into the print spooler service, which runs as SYSTEM. This file is {status.ToLowerInvariant()} rather than a recognized signed component.",
            Path = location,
            WhatDisablingDoes = "Removing or replacing this registration stops the spooler from loading this DLL; only do this for one you don't recognize, since many legitimate printer drivers register their own monitor/processor DLLs here too. Quick flag, not a verdict.",
            RelatedEntry = entry,
        });
    }

    // #817: Netsh helper DLLs - every value here loads into netsh.exe's process whenever it runs.
    // Third-party helpers (VPN clients, firewall management tools, ...) are a legitimate, common
    // use of this mechanism, so every entry is listed with no finding unless it's unsigned.
    private static void AddNetshHelperItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Netsh";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            string location = $@"HKLM\{keyPath}";
            foreach (var valueName in key.GetValueNames())
            {
                var dllPath = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(dllPath)) continue;

                var resolved = System.IO.Path.IsPathRooted(dllPath) ? dllPath : System.IO.Path.Combine(Environment.SystemDirectory, dllPath);
                var status = System.IO.File.Exists(resolved) ? SignatureCheckService.GetStatus(resolved) : "Unknown";
                var entryLocation = $@"{location}\{valueName}";
                var entry = new AutorunEntry
                {
                    Category = "Netsh Helper",
                    Name = valueName,
                    RawCommand = dllPath,
                    ResolvedPath = resolved,
                    Publisher = "Unknown",
                    SignatureStatus = status,
                    Location = entryLocation,
                    Enabled = true,
                };
                items.Add(entry);

                if (status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Low,
                        Title = $"Unsigned netsh helper: {valueName}",
                        Reason = $"\"{valueName}\" loads \"{dllPath}\" into every netsh.exe invocation. Third-party helpers (VPN clients and similar) legitimately register here, but this one doesn't carry a valid Authenticode signature - worth a glance.",
                        Path = entryLocation,
                        WhatDisablingDoes = "Removing this value stops the helper from loading into netsh.exe; only do this for one you don't recognize or no longer use. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    // #818: Winsock LSP catalog - `netsh winsock show catalog` lists every Layered Service
    // Provider DLL registered in the Winsock catalog, a legacy-but-still-live way to sit in every
    // socket call system-wide. The binary catalog format isn't documented, so this shells out to
    // netsh.exe and parses its text output - the same known-tool-over-raw-interop tradeoff
    // ScheduledTaskService/NetworkDiagnosticsService already take elsewhere in this app. The
    // output's exact structure varies by Windows version, so this is a defensive best-effort scan:
    // every line shaped like "Path : <file>.dll" is taken as one catalog entry; if none match that
    // shape, every distinct .dll path mentioned anywhere in the output is used instead.
    private static void AddWinsockLspItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            string output = RunCapturedSync("netsh.exe", "winsock show catalog", TimeSpan.FromSeconds(10));
            if (string.IsNullOrWhiteSpace(output)) return;

            var dllPaths = new List<string>();
            foreach (var rawLine in output.Split('\n'))
            {
                var trimmed = rawLine.Trim().TrimEnd('\r');
                var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^Path\s*:\s*(.+\.dll)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) dllPaths.Add(match.Groups[1].Value.Trim());
            }

            if (dllPaths.Count == 0)
            {
                // Structure didn't match what we expected - fall back to scraping every distinct
                // .dll path mentioned anywhere in the output.
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    output, @"[A-Za-z]:\\[^\r\n:]*?\.dll", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    dllPaths.Add(m.Value.Trim());
                }
            }

            const string location = "netsh winsock show catalog";
            foreach (var dllPath in dllPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var status = System.IO.File.Exists(dllPath) ? SignatureCheckService.GetStatus(dllPath) : "Unknown";
                var entry = new AutorunEntry
                {
                    Category = "Winsock LSP",
                    Name = System.IO.Path.GetFileName(dllPath),
                    RawCommand = dllPath,
                    ResolvedPath = dllPath,
                    Publisher = "Unknown",
                    SignatureStatus = status,
                    Location = location,
                    Enabled = true,
                };
                items.Add(entry);

                if (!status.Equals("Signed", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Low,
                        Title = $"Non-Microsoft-signed Winsock LSP: {entry.Name}",
                        Reason = $"\"{dllPath}\" is registered in the Winsock LSP catalog, meaning it loads into every process that opens a socket. It doesn't carry a valid Authenticode signature - a legacy-but-still-live way to sit in every socket call system-wide.",
                        Path = location,
                        WhatDisablingDoes = "Removing an LSP from the catalog needs care (a full \"netsh winsock reset\" removes ALL of them at once, which can break network connectivity until reconfigured) - only do this for an entry you don't recognize, ideally via the vendor's own uninstaller. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
        }
        catch
        {
            // netsh unavailable/failed/timed out - contribute nothing, same as every other
            // optional shelled-out data source in this app.
        }
    }

    // #819: Minifilter driver list - `fltmc filters`/`fltmc instances` parsed defensively (the
    // documented column layout is stable across Windows 10/11 builds, but this doesn't assume it -
    // if the exact layout can't be read, the filter name (always the first column) is still kept
    // and the rest degrades to "Unknown" rather than throwing). Each filter name is then resolved
    // to its own Services registry entry for a signature check - minifilters are ordinary drivers
    // registered the same way as any other. Altitude ordering (surfaced in RawCommand) is what
    // makes a stacked-AV or leftover-uninstalled-AV-filter problem visible to a human reading the
    // list, not something this code judges on its own.
    private static void AddMinifilterItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            string filtersOutput = RunCapturedSync("fltmc.exe", "filters", TimeSpan.FromSeconds(10));
            var filterInfo = ParseFltmcFilters(filtersOutput);
            if (filterInfo.Count == 0) return; // fltmc unavailable/no filters/unparseable output

            string instancesOutput = RunCapturedSync("fltmc.exe", "instances", TimeSpan.FromSeconds(10));
            var volumesByFilter = ParseFltmcInstances(instancesOutput);

            foreach (var (name, (altitude, frame)) in filterInfo)
            {
                var (rawImagePath, resolvedPath) = ResolveServiceImagePath(name);
                bool exists = !string.IsNullOrWhiteSpace(resolvedPath) && System.IO.File.Exists(resolvedPath);
                string status = exists ? SignatureCheckService.GetStatus(resolvedPath) : "Unknown";
                string volumes = volumesByFilter.TryGetValue(name, out var v) && v.Count > 0 ? string.Join(", ", v) : "(none attached)";
                string location = $"fltmc filters: {name}";

                var raw = $"altitude {altitude}, frame {frame}, volumes: {volumes}";
                if (!string.IsNullOrWhiteSpace(rawImagePath)) raw += $", image {rawImagePath}";

                var entry = new AutorunEntry
                {
                    Category = "Minifilter",
                    Name = name,
                    RawCommand = raw,
                    ResolvedPath = resolvedPath,
                    Publisher = "Unknown",
                    SignatureStatus = status,
                    Location = location,
                    Enabled = true,
                };
                items.Add(entry);

                if (exists && status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = $"Unsigned minifilter driver: {name}",
                        Reason = $"\"{name}\" is attached at altitude {altitude} in the file I/O path (volumes: {volumes}), and its driver file doesn't carry a valid Authenticode signature. Altitude ordering makes a stacked-AV problem or a leftover uninstalled AV filter sitting in the I/O path visible - worth a look.",
                        Path = location,
                        WhatDisablingDoes = "Uninstalling the owning security/backup product removes its minifilter from the stack cleanly; only do this for one you don't recognize, since legitimate security/backup software installs minifilters here routinely. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
        }
        catch
        {
            // fltmc unavailable/failed/timed out - contribute nothing, same tradeoff as #818's netsh call.
        }
    }

    /// <summary>Parses `fltmc filters` output into name -&gt; (altitude, frame). Defensive by
    /// design: skips the header and the dashed separator line, splits remaining lines on runs of
    /// 2+ spaces (the columns are whitespace-padded, not delimited), and if a line doesn't have
    /// enough columns to read altitude/frame it still keeps the filter name with "Unknown" for the
    /// rest rather than dropping the row - the exact column layout isn't documented to be stable
    /// across every Windows build.</summary>
    private static Dictionary<string, (string Altitude, string Frame)> ParseFltmcFilters(string output)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("Filter Name", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (line.Trim().Trim('-').Length == 0) continue; // dashed separator line

            var columns = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
            if (columns.Length == 0 || string.IsNullOrWhiteSpace(columns[0])) continue;

            var name = columns[0].Trim();
            string altitude = columns.Length > 2 ? columns[2].Trim() : "Unknown";
            string frame = columns.Length > 3 ? columns[3].Trim() : "Unknown";
            result[name] = (altitude, frame);
        }
        return result;
    }

    /// <summary>Parses `fltmc instances` output into filter name -&gt; attached volumes, same
    /// defensive column-splitting approach as ParseFltmcFilters.</summary>
    private static Dictionary<string, List<string>> ParseFltmcInstances(string output)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("Filter Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Trim().Trim('-').Length == 0) continue;

            var columns = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
            if (columns.Length < 2) continue;

            var name = columns[0].Trim();
            var volume = columns[1].Trim();
            if (!result.TryGetValue(name, out var list))
            {
                list = new List<string>();
                result[name] = list;
            }
            if (!list.Contains(volume, StringComparer.OrdinalIgnoreCase)) list.Add(volume);
        }
        return result;
    }

    /// <summary>Looks up one service/driver's ImagePath under
    /// SYSTEM\CurrentControlSet\Services\&lt;name&gt; and resolves it to a real file path via
    /// ResolveDriverImagePath - shared by the minifilter list (#819) which only has a filter name
    /// to start from.</summary>
    private static (string RawImagePath, string ResolvedPath) ResolveServiceImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return (string.Empty, string.Empty);
            return (imagePath, ResolveDriverImagePath(imagePath));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>Resolves a driver-style ImagePath value (`\SystemRoot\System32\drivers\x.sys`, a
    /// native `\??\` prefix, a bare filename, or an already-rooted path) to a real filesystem path.
    /// A bare filename with no path at all is assumed to live in System32\drivers, since that's
    /// where the overwhelming majority of real driver ImagePath values without a full path point.</summary>
    private static string ResolveDriverImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return string.Empty;

        var path = imagePath.Trim().Trim('"');
        const string systemRootPrefix = @"\SystemRoot\";
        const string nativePrefix = @"\??\";

        if (path.StartsWith(systemRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[systemRootPrefix.Length..]);
        }
        else if (path.StartsWith(nativePrefix, StringComparison.Ordinal))
        {
            path = path[nativePrefix.Length..];
        }
        else if (!System.IO.Path.IsPathRooted(path))
        {
            // Either a relative "system32\drivers\x.sys"-shaped path or a bare filename - both
            // resolve against System32\drivers, the standard home for kernel/FS drivers.
            var trimmedRelative = path.TrimStart('\\');
            path = trimmedRelative.StartsWith("system32", StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), trimmedRelative)
                : System.IO.Path.Combine(Environment.SystemDirectory, "drivers", trimmedRelative);
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    // #820: Boot-start and kernel driver audit - walks every Services subkey (hundreds on a typical
    // machine; that's fine, this is an on-demand scan behind the same Scan button as everything
    // else) looking for Type 1 (kernel driver) or 2 (file-system driver) entries. An unsigned or
    // non-standard-path kernel driver is one of the single most useful malware/rootkit signals a
    // user-mode app can honestly produce, since kernel code runs with no further permission checks
    // once loaded.
    private static void AddKernelDriverItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (servicesKey is null) return;

            var standardDriverDir = System.IO.Path.Combine(Environment.SystemDirectory, "drivers");

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = servicesKey.OpenSubKey(serviceName);
                    if (sub is null) continue;
                    if (sub.GetValue("Type") is not int type || (type != 1 && type != 2)) continue;

                    var imagePath = sub.GetValue("ImagePath") as string;
                    if (string.IsNullOrWhiteSpace(imagePath)) continue; // no binary recorded - nothing to check

                    int startValue = sub.GetValue("Start") is int s ? s : -1;
                    string startFriendly = startValue switch
                    {
                        0 => "Boot",
                        1 => "System",
                        2 => "Automatic",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => "Unknown",
                    };

                    var resolved = ResolveDriverImagePath(imagePath);
                    bool exists = !string.IsNullOrWhiteSpace(resolved) && System.IO.File.Exists(resolved);
                    string status = exists ? SignatureCheckService.GetStatus(resolved) : "Unknown";
                    string? resolvedDir = exists ? System.IO.Path.GetDirectoryName(resolved) : null;
                    bool outsideStandardPath = exists && resolvedDir is not null && !resolvedDir.Equals(standardDriverDir, StringComparison.OrdinalIgnoreCase);

                    var location = $@"HKLM\{keyPath}\{serviceName}\ImagePath";
                    var entry = new AutorunEntry
                    {
                        Category = "Kernel Driver",
                        Name = serviceName,
                        RawCommand = $"{startFriendly} start ({(type == 1 ? "kernel driver" : "file system driver")}), {imagePath}",
                        ResolvedPath = resolved,
                        Publisher = "Unknown",
                        SignatureStatus = status,
                        Location = location,
                        Enabled = startValue != 4,
                    };
                    items.Add(entry);

                    var issues = new List<string>();
                    if (!exists) issues.Add("its resolved file wasn't found on disk");
                    if (exists && status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase)) issues.Add("its file isn't signed");
                    if (outsideStandardPath) issues.Add($"it loads from outside System32\\drivers (\"{resolved}\")");
                    if (issues.Count == 0) continue;

                    bool highSeverity = !exists || status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase);
                    findings.Add(new SecurityFinding
                    {
                        Severity = highSeverity ? FindingSeverity.High : FindingSeverity.Medium,
                        Title = $"Kernel driver worth checking: {serviceName}",
                        Reason = $"{serviceName} ({startFriendly} start) - {string.Join("; ", issues)}. An unsigned or non-standard-path kernel driver is one of the single most useful malware/rootkit signals a user-mode app can honestly produce.",
                        Path = location,
                        WhatDisablingDoes = "Setting Start to 4 (Disabled) via the Services tab or sc.exe stops the driver from loading at next boot without deleting its registration; only do this for one you don't recognize - some legitimate vendor drivers are unsigned test-signed drivers or intentionally live outside System32\\drivers. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    // #821: Service path hygiene audit - three independent checks over the same Services key as
    // #820, restricted to Win32 service types (Type 1/2 kernel/FS drivers are #820's job instead):
    // (a) an unquoted ImagePath whose path portion contains a space - the classic
    // privilege-escalation footgun where Windows tries each space-delimited prefix as its own
    // executable; (b) an svchost.exe -k <group> entry whose ServiceDll (under the service's
    // Parameters subkey) resolves somewhere user-writable, using a simple path-prefix heuristic
    // (Temp/AppData/LocalAppData/Public/user-profile root) rather than a full ACL check - a real
    // icacls-based writability check is #845's job, not this one; (c) a binary that's missing
    // entirely. Every finding names the exact service name so a user can cross-reference it on the
    // Services tab themselves - no navigation wiring, just a clear name in the text.
    //
    // Omitted deliberately: "recently created relative to Windows install" (registry key creation
    // time isn't reliably available through .NET's Registry APIs without extra native interop,
    // which is out of scope for this item).
    private static void AddServicePathHygieneItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (servicesKey is null) return;

            var userWritableRoots = BuildUserWritableRoots();

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = servicesKey.OpenSubKey(serviceName);
                    if (sub is null) continue;
                    if (sub.GetValue("Type") is int type && (type == 1 || type == 2)) continue; // drivers - #820's job

                    var imagePath = sub.GetValue("ImagePath") as string;
                    if (string.IsNullOrWhiteSpace(imagePath)) continue;

                    var location = $@"HKLM\{keyPath}\{serviceName}\ImagePath";

                    CheckUnquotedServicePath(items, findings, serviceName, imagePath, location);

                    if (imagePath.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase) &&
                        imagePath.Contains("-k", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckSvchostServiceDll(items, findings, sub, serviceName, keyPath, userWritableRoots);
                    }

                    var exePath = StartupManagerService.ExtractPath(imagePath);
                    var expandedExePath = string.IsNullOrWhiteSpace(exePath) ? string.Empty : Environment.ExpandEnvironmentVariables(exePath);
                    if (!string.IsNullOrWhiteSpace(expandedExePath) && !System.IO.File.Exists(expandedExePath))
                    {
                        var entry = new AutorunEntry
                        {
                            Category = "Service Hygiene",
                            Name = serviceName,
                            RawCommand = imagePath,
                            ResolvedPath = expandedExePath,
                            Publisher = "Unknown",
                            SignatureStatus = "Unknown",
                            Location = location,
                            Enabled = true,
                        };
                        items.Add(entry);

                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.Medium,
                            Title = $"Service binary missing: {serviceName}",
                            Reason = $"The service \"{serviceName}\" (look it up by this exact name on the Services tab) points at \"{expandedExePath}\", which wasn't found on disk.",
                            Path = location,
                            WhatDisablingDoes = "A missing binary usually means a leftover/orphaned service registration from an uninstalled product - removing the service (sc.exe delete) is reasonable once you confirm it's not something still needed. Quick flag, not a verdict.",
                            RelatedEntry = entry,
                        });
                    }
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    private static void CheckUnquotedServicePath(List<AutorunEntry> items, List<SecurityFinding> findings, string serviceName, string imagePath, string location)
    {
        var trimmed = imagePath.TrimStart();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal)) return; // already quoted

        int exeIdx = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0) return; // not a recognizable exe path - nothing to check

        var pathPortion = trimmed[..(exeIdx + 4)];
        if (!pathPortion.Contains(' ')) return; // no space before .exe - not vulnerable

        var entry = new AutorunEntry
        {
            Category = "Service Hygiene",
            Name = serviceName,
            RawCommand = imagePath,
            ResolvedPath = pathPortion,
            Publisher = "Unknown",
            SignatureStatus = "Unknown",
            Location = location,
            Enabled = true,
        };
        items.Add(entry);

        findings.Add(new SecurityFinding
        {
            Severity = FindingSeverity.Medium,
            Title = $"Unquoted service path with a space: {serviceName}",
            Reason = $"The service \"{serviceName}\" (look it up by this exact name on the Services tab) has an unquoted ImagePath with a space before the .exe (\"{pathPortion}\") - classic privilege-escalation footgun, since Windows tries each space-delimited prefix as its own executable before the real one (e.g. a planted C:\\Program.exe would win).",
            Path = location,
            WhatDisablingDoes = "Wrapping the ImagePath value in quotes (via regedit or `sc.exe config <name> binPath= \"...\"`) closes the gap; only do this if you're comfortable editing service configuration. Quick flag, not a verdict.",
            RelatedEntry = entry,
        });
    }

    private static void CheckSvchostServiceDll(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey serviceKey, string serviceName, string keyPath, string[] userWritableRoots)
    {
        try
        {
            using var parametersKey = serviceKey.OpenSubKey("Parameters");
            var serviceDll = parametersKey?.GetValue("ServiceDll") as string;
            if (string.IsNullOrWhiteSpace(serviceDll)) return;

            var expanded = Environment.ExpandEnvironmentVariables(serviceDll);
            bool userWritable = userWritableRoots.Any(root => !string.IsNullOrWhiteSpace(root) && expanded.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (!userWritable) return;

            var location = $@"HKLM\{keyPath}\{serviceName}\Parameters\ServiceDll";
            var entry = new AutorunEntry
            {
                Category = "Service Hygiene",
                Name = serviceName,
                RawCommand = serviceDll,
                ResolvedPath = expanded,
                Publisher = "Unknown",
                SignatureStatus = System.IO.File.Exists(expanded) ? SignatureCheckService.GetStatus(expanded) : "Unknown",
                Location = location,
                Enabled = true,
            };
            items.Add(entry);

            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = $"svchost service DLL in a user-writable location: {serviceName}",
                Reason = $"The svchost-hosted service \"{serviceName}\" (look it up by this exact name on the Services tab) has a ServiceDll of \"{expanded}\", under a Temp/AppData/Public/user-profile location. Simplified path-prefix heuristic, not a full ACL/icacls writability check (that's a separate, more thorough check).",
                Path = location,
                WhatDisablingDoes = "svchost.exe typically runs as SYSTEM or another high-privilege account - a DLL loaded from a location an ordinary user can write to is a privilege-escalation risk if it's actually writable by less-privileged accounts. Verify what installed this before removing anything. Quick flag, not a verdict.",
                RelatedEntry = entry,
            });
        }
        catch
        {
            // Parameters subkey inaccessible (or absent - most services don't use svchost hosting) - skip.
        }
    }

    private static string[] BuildUserWritableRoots()
    {
        var roots = new List<string>();
        try { roots.Add(Environment.GetEnvironmentVariable("TEMP") ?? string.Empty); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); } catch { }
        try { roots.Add(Environment.ExpandEnvironmentVariables("%Public%")); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); } catch { }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // #822: WMI permanent event subscriptions - the classic "fileless" persistence mechanism:
    // invisible to Task Manager entirely, since nothing here is a process until its filter's WQL
    // condition actually fires. root\subscription is empty on the overwhelming majority of clean
    // machines, and WMI itself can be unavailable in constrained environments, so the whole thing is
    // one try/catch that degrades to nothing rather than a scan failure.
    private static void AddWmiEventSubscriptionItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            var scope = new ManagementScope(@"root\subscription");
            scope.Connect();

            var filterDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var filterSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM __EventFilter")))
            using (var filterResults = filterSearcher.Get())
            {
                foreach (ManagementObject mo in filterResults)
                {
                    using (mo)
                    {
                        var name = mo["Name"] as string ?? "(unnamed)";
                        var query = mo["Query"] as string ?? string.Empty;
                        var relativePath = mo.Path.RelativePath;
                        filterDescriptions[relativePath] = $"{name}: {query}";

                        items.Add(new AutorunEntry
                        {
                            Category = "WMI Subscription",
                            Name = $"Filter: {name}",
                            RawCommand = query,
                            ResolvedPath = string.Empty,
                            Publisher = "Unknown",
                            SignatureStatus = "Unknown",
                            Location = $@"root\subscription\{relativePath}",
                            Enabled = true,
                        });
                    }
                }
            }

            var consumerDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var consumerSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM __EventConsumer")))
            using (var consumerResults = consumerSearcher.Get())
            {
                foreach (ManagementObject mo in consumerResults)
                {
                    using (mo)
                    {
                        var className = mo.ClassPath.ClassName;
                        var name = mo["Name"] as string ?? "(unnamed)";
                        var relativePath = mo.Path.RelativePath;

                        // CommandLineEventConsumer and ActiveScriptEventConsumer are the two
                        // actually-dangerous consumer subtypes (they run an arbitrary command/script);
                        // every other consumer class is still listed, just without special-cased detail.
                        string description = className switch
                        {
                            "CommandLineEventConsumer" => $"runs command line: {mo["CommandLineTemplate"] as string}",
                            "ActiveScriptEventConsumer" => $"runs {mo["ScriptingEngine"] as string} script: {(mo["ScriptText"] as string) ?? (mo["ScriptFileName"] as string) ?? "(script text unavailable)"}",
                            _ => $"{className} consumer",
                        };
                        consumerDescriptions[relativePath] = $"{name} ({className}) - {description}";

                        items.Add(new AutorunEntry
                        {
                            Category = "WMI Subscription",
                            Name = $"Consumer: {name}",
                            RawCommand = description,
                            ResolvedPath = string.Empty,
                            Publisher = "Unknown",
                            SignatureStatus = "Unknown",
                            Location = $@"root\subscription\{relativePath}",
                            Enabled = true,
                        });
                    }
                }
            }

            using var bindingSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM __FilterToConsumerBinding"));
            using var bindingResults = bindingSearcher.Get();
            foreach (ManagementObject mo in bindingResults)
            {
                using (mo)
                {
                    var filterRef = mo["Filter"] as string ?? string.Empty;
                    var consumerRef = mo["Consumer"] as string ?? string.Empty;
                    var filterKey = ExtractWmiRelativePath(filterRef);
                    var consumerKey = ExtractWmiRelativePath(consumerRef);
                    var filterDesc = filterDescriptions.TryGetValue(filterKey, out var fd) ? fd : filterRef;
                    var consumerDesc = consumerDescriptions.TryGetValue(consumerKey, out var cd) ? cd : consumerRef;

                    var location = $"root\\subscription binding: {filterRef} -> {consumerRef}";
                    var entry = new AutorunEntry
                    {
                        Category = "WMI Subscription",
                        Name = "Filter-to-consumer binding",
                        RawCommand = $"{filterDesc} => {consumerDesc}",
                        ResolvedPath = string.Empty,
                        Publisher = "Unknown",
                        SignatureStatus = "Unknown",
                        Location = location,
                        Enabled = true,
                    };
                    items.Add(entry);

                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.High,
                        Title = "WMI permanent event subscription bound",
                        Reason = $"Fileless WMI persistence is invisible to Task Manager entirely; a bound CommandLine/ActiveScript consumer runs its command/script whenever its filter's WQL condition fires. Filter: {filterDesc}. Consumer: {consumerDesc}.",
                        Path = location,
                        WhatDisablingDoes = "Removing the __FilterToConsumerBinding (and the underlying filter/consumer instances, via WMI/PowerShell's Remove-WmiObject or wbemtest) stops this from firing again; only do this for a subscription you don't recognize - some legitimate management/monitoring tooling does use this mechanism deliberately. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
        }
        catch
        {
            // root\subscription empty/absent (the normal case) or WMI unavailable in this
            // environment - contribute nothing.
        }
    }

    /// <summary>WMI object-path references (as seen in __FilterToConsumerBinding's Filter/Consumer
    /// values) look like `\\.\root\subscription:__EventFilter.Name="X"` - this strips everything up
    /// to and including the last colon so the remainder matches ManagementPath.RelativePath's
    /// format ("__EventFilter.Name=\"X\"") used as the dictionary key above.</summary>
    private static string ExtractWmiRelativePath(string wmiRefPath)
    {
        int idx = wmiRefPath.LastIndexOf(':');
        return idx >= 0 ? wmiRefPath[(idx + 1)..] : wmiRefPath;
    }

    // #823: Active Setup stub commands - StubPath runs once per user the first time that user logs
    // on after the component's Version differs from what's recorded in that user's own HKCU mirror,
    // a legitimate-but-still-a-persistence-mechanism Windows uses for per-user first-run setup. A
    // mismatched/missing HKCU version means the StubPath command will run again at this user's next
    // logon - surfaced as a name suffix rather than a separate field, per the item's own guidance.
    private static void AddActiveSetupItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Active Setup\Installed Components";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    var stubPath = sub?.GetValue("StubPath") as string;
                    if (string.IsNullOrWhiteSpace(stubPath)) continue;

                    var hklmVersion = sub?.GetValue("Version") as string ?? string.Empty;
                    string? hkcuVersion = null;
                    try
                    {
                        using var hkcuSub = Registry.CurrentUser.OpenSubKey($@"{keyPath}\{subName}");
                        hkcuVersion = hkcuSub?.GetValue("Version") as string;
                    }
                    catch
                    {
                        // No HKCU mirror for this user yet - treated the same as "missing" below.
                    }

                    bool willRerun = !hklmVersion.Equals(hkcuVersion, StringComparison.OrdinalIgnoreCase);

                    var friendlyName = sub?.GetValue(null) as string;
                    var displayName = string.IsNullOrWhiteSpace(friendlyName) ? subName : friendlyName;
                    if (willRerun) displayName += " (will re-run at next logon)";

                    var location = $@"HKLM\{keyPath}\{subName}\StubPath";
                    var entry = BuildEntry("Active Setup", displayName, stubPath, location);
                    items.Add(entry);

                    if (willRerun)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.Low,
                            Title = $"Active Setup component will re-run at next logon: {subName}",
                            Reason = $"This user's HKCU version marker for \"{subName}\" is {(hkcuVersion is null ? "missing" : $"\"{hkcuVersion}\"")}, which doesn't match the HKLM version \"{hklmVersion}\" - Windows will re-run its StubPath command (\"{stubPath}\") the next time this user logs on.",
                            Path = location,
                            WhatDisablingDoes = "Setting the HKCU Version value to match HKLM prevents the re-run; only do this if you understand what the stub command does. Quick flag, not a verdict - a version mismatch is often just a pending legitimate per-user setup step (e.g. a newly-created user profile).",
                            RelatedEntry = entry,
                        });
                    }
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent) - contribute nothing.
        }
    }

    // #824: Explorer legacy load/run values and the per-user logon script - all three still execute
    // at logon and none of them surface anywhere in Windows' own UI (not even the classic
    // msconfig/Task Manager Startup list), which is exactly why they're worth a dedicated check.
    private static void AddLegacyLogonItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string windowsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(windowsKeyPath);
            if (key is not null)
            {
                AddLegacyLogonValue(items, findings, key, $@"HKCU\{windowsKeyPath}", "load");
                AddLegacyLogonValue(items, findings, key, $@"HKCU\{windowsKeyPath}", "run");
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing from this half.
        }

        try
        {
            using var envKey = Registry.CurrentUser.OpenSubKey("Environment");
            if (envKey is not null)
            {
                AddLegacyLogonValue(items, findings, envKey, @"HKCU\Environment", "UserInitMprLogonScript");
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing from this half.
        }
    }

    private static void AddLegacyLogonValue(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey key, string location, string valueName)
    {
        var raw = key.GetValue(valueName) as string;
        if (string.IsNullOrWhiteSpace(raw)) return; // empty/absent is the expected, normal case

        var entryLocation = $@"{location}\{valueName}";
        var entry = BuildEntry("Legacy Logon", valueName, raw, entryLocation);
        items.Add(entry);

        findings.Add(new SecurityFinding
        {
            Severity = FindingSeverity.Medium,
            Title = $"Legacy logon value set: {valueName}",
            Reason = $"\"{valueName}\" is set to \"{raw}\" - it still executes at logon and no Windows UI surfaces it.",
            Path = entryLocation,
            WhatDisablingDoes = "Clearing this value stops it from running at the next logon; only do this for a command you don't recognize. Quick flag, not a verdict - a small amount of legacy IME/accessibility software still uses \"load\"/\"run\" deliberately.",
            RelatedEntry = entry,
        });
    }

    // #825: Group Policy script audit - both the modern registry-based Startup/Shutdown/Logon/
    // Logoff script registrations (walked one level of numbered subkeys, then a nested numbered
    // subkey holding CmdLine/Parameters, tolerated defensively the same way RunOnceEx above is) and
    // a best-effort raw read of the legacy scripts.ini/psscripts.ini files GPO also still writes.
    // Genuinely common leftover on ex-corporate machines where a stale local/cached GPO still runs
    // something at every boot/logon long after the machine left that domain.
    private static void AddGroupPolicyScriptItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddGpScriptRegistryItems(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Startup", "GP Script (Startup)");
        AddGpScriptRegistryItems(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Shutdown", "GP Script (Shutdown)");
        AddGpScriptRegistryItems(items, findings, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon", "GP Script (Logon)");
        AddGpScriptRegistryItems(items, findings, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logoff", "GP Script (Logoff)");

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        AddGpScriptIniFile(items, findings, System.IO.Path.Combine(windowsDir, @"System32\GroupPolicy\Machine\Scripts\scripts.ini"), "GP scripts.ini (Machine)");
        AddGpScriptIniFile(items, findings, System.IO.Path.Combine(windowsDir, @"System32\GroupPolicy\Machine\Scripts\psscripts.ini"), "GP psscripts.ini (Machine)");
        AddGpScriptIniFile(items, findings, System.IO.Path.Combine(windowsDir, @"System32\GroupPolicy\User\Scripts\scripts.ini"), "GP scripts.ini (User)");
        AddGpScriptIniFile(items, findings, System.IO.Path.Combine(windowsDir, @"System32\GroupPolicy\User\Scripts\psscripts.ini"), "GP psscripts.ini (User)");
    }

    private static void AddGpScriptRegistryItems(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey hive, string hiveLabel, string keyPath, string category)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var numberedName in key.GetSubKeyNames())
            {
                try
                {
                    using var numberedKey = key.OpenSubKey(numberedName);
                    if (numberedKey is null) continue;

                    foreach (var innerName in numberedKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var innerKey = numberedKey.OpenSubKey(innerName);
                            var cmdLine = innerKey?.GetValue("CmdLine") as string;
                            if (string.IsNullOrWhiteSpace(cmdLine)) continue;

                            var parameters = innerKey?.GetValue("Parameters") as string;
                            var raw = string.IsNullOrWhiteSpace(parameters) ? cmdLine : $"{cmdLine} {parameters}";
                            var location = $@"{hiveLabel}\{keyPath}\{numberedName}\{innerName}";
                            var entry = BuildEntry(category, $"{numberedName}/{innerName}", raw, location);
                            items.Add(entry);

                            findings.Add(new SecurityFinding
                            {
                                Severity = FindingSeverity.Low,
                                Title = $"{category} script configured",
                                Reason = $"\"{raw}\" runs via Group Policy scripts - common on ex-corporate machines where a leftover (often stale/cached) policy still runs something at every boot/logon.",
                                Path = location,
                                WhatDisablingDoes = "Removing the policy (via gpedit.msc, or `gpupdate /force` after leaving the domain/removing the GPO) stops it from running; deleting this registry branch directly works too but won't survive the next policy refresh if the GPO is still actually applied. Quick flag, not a verdict.",
                                RelatedEntry = entry,
                            });
                        }
                        catch
                        {
                            // One bad inner subkey shouldn't stop the rest.
                        }
                    }
                }
                catch
                {
                    // One bad numbered subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case outside a domain) - contribute nothing.
        }
    }

    private static void AddGpScriptIniFile(List<AutorunEntry> items, List<SecurityFinding> findings, string filePath, string category)
    {
        try
        {
            if (!System.IO.File.Exists(filePath)) return;

            var content = System.IO.File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(content)) return; // present but empty - nothing to surface

            var entry = new AutorunEntry
            {
                Category = category,
                Name = System.IO.Path.GetFileName(filePath),
                RawCommand = content.Trim(),
                ResolvedPath = filePath,
                Publisher = "Unknown",
                SignatureStatus = "Unknown",
                Location = filePath,
                Enabled = true,
            };
            items.Add(entry);

            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.Low,
                Title = $"{category} has content",
                Reason = $"\"{filePath}\" is non-empty - common on ex-corporate machines where a leftover policy still runs something at every boot/logon.",
                Path = filePath,
                WhatDisablingDoes = "Removing the owning Group Policy Object (via gpedit.msc, or by leaving the domain) is the clean fix; deleting this file directly can be undone by the next policy refresh if the GPO is still actually applied. Quick flag, not a verdict.",
                RelatedEntry = entry,
            });
        }
        catch
        {
            // File unreadable (permissions, in use, ...) - contribute nothing from this file.
        }
    }

    // #826: Browser Helper Objects and legacy Internet Explorer hooks - each subkey/value name here
    // is a CLSID, resolved to a friendly name and backing DLL the same way ShellExtensionService
    // resolves shell-extension CLSIDs (HKEY_CLASSES_ROOT\CLSID\{guid}). Legacy (IE itself is gone
    // from modern Windows, but Explorer's own BHO loading and the registry keys both still exist and
    // some third-party installers still populate them), so these rows carry no finding on their own
    // - only an unsigned resolved DLL is worth a (Low-severity) flag.
    private static void AddBrowserHelperObjectItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddClsidSubkeyHooks(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", "Browser Helper Object");
        AddClsidSubkeyHooks(items, findings, Registry.LocalMachine, "HKLM (32-bit)", @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", "Browser Helper Object");
        AddClsidSubkeyHooks(items, findings, Registry.CurrentUser, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", "Browser Helper Object");

        AddClsidValueHooks(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Internet Explorer\Toolbar", "IE Toolbar");
        AddClsidValueHooks(items, findings, Registry.LocalMachine, "HKLM (32-bit)", @"SOFTWARE\Wow6432Node\Microsoft\Internet Explorer\Toolbar", "IE Toolbar");
        AddClsidSubkeyHooks(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Internet Explorer\Extensions", "IE Hook");
        AddClsidSubkeyHooks(items, findings, Registry.LocalMachine, "HKLM (32-bit)", @"SOFTWARE\Wow6432Node\Microsoft\Internet Explorer\Extensions", "IE Hook");
        AddClsidValueHooks(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Internet Explorer\URLSearchHooks", "IE Hook");
        AddClsidSubkeyHooks(items, findings, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Internet Explorer\Explorer Bars", "IE Hook");
    }

    private static void AddClsidSubkeyHooks(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey hive, string hiveLabel, string keyPath, string category)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var clsid in key.GetSubKeyNames())
            {
                AddClsidHookEntry(items, findings, category, clsid, $@"{hiveLabel}\{keyPath}\{clsid}");
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case on modern Windows) - contribute nothing.
        }
    }

    private static void AddClsidValueHooks(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey hive, string hiveLabel, string keyPath, string category)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            foreach (var clsid in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(clsid)) continue;
                AddClsidHookEntry(items, findings, category, clsid, $@"{hiveLabel}\{keyPath}\{clsid}");
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case on modern Windows) - contribute nothing.
        }
    }

    private static void AddClsidHookEntry(List<AutorunEntry> items, List<SecurityFinding> findings, string category, string clsid, string location)
    {
        var (name, dllPath) = ResolveClsidToNameAndDll(clsid);
        var displayName = string.IsNullOrWhiteSpace(name) ? clsid : name;
        bool exists = !string.IsNullOrWhiteSpace(dllPath) && System.IO.File.Exists(dllPath);
        var status = exists ? SignatureCheckService.GetStatus(dllPath) : "Unknown";

        var entry = new AutorunEntry
        {
            Category = category,
            Name = displayName,
            RawCommand = clsid,
            ResolvedPath = dllPath,
            Publisher = "Unknown",
            SignatureStatus = status,
            Location = location,
            Enabled = true,
        };
        items.Add(entry);

        if (exists && status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.Low,
                Title = $"Unsigned {category.ToLowerInvariant()}: {displayName}",
                Reason = $"\"{displayName}\" ({clsid}) loads \"{dllPath}\" and doesn't carry a valid Authenticode signature. Legacy mechanism, but still surfaced since Explorer/IE will still load it if registered.",
                Path = location,
                WhatDisablingDoes = "Removing this registration stops the hook from loading; only do this for one you don't recognize. Quick flag, not a verdict.",
                RelatedEntry = entry,
            });
        }
    }

    /// <summary>Resolves a CLSID to its friendly name and InprocServer32 DLL path via
    /// HKEY_CLASSES_ROOT\CLSID\{guid}, the same lookup ShellExtensionService.ResolveClsid performs
    /// for shell-extension CLSIDs - kept as a small local copy here (rather than exposing
    /// ShellExtensionService's private helper) since it's used by several independent categories in
    /// this file (#826-828).</summary>
    private static (string Name, string DllPath) ResolveClsidToNameAndDll(string clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
            if (key is null) return (string.Empty, string.Empty);

            string name = key.GetValue(null) as string ?? string.Empty;
            string dll = string.Empty;
            using (var inproc = key.OpenSubKey("InprocServer32"))
            {
                dll = inproc?.GetValue(null) as string ?? string.Empty;
            }
            return (name, string.IsNullOrWhiteSpace(dll) ? string.Empty : Environment.ExpandEnvironmentVariables(dll));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    // #827: Per-user COM hijack detector - a CLSID registered under both HKCU\Software\Classes\CLSID
    // and HKLM\SOFTWARE\Classes\CLSID silently resolves to the HKCU copy for that user (COM's
    // documented per-user-overrides-machine-wide precedence), so any CLSID present in both is worth
    // a High finding regardless of where its DLL lives. Separately, any HKCU CLSID whose
    // InprocServer32 DLL resolves into a Temp/AppData location is flagged even without a shadow
    // match. Only the flagged CLSIDs are added as rows/findings - listing every benign per-user
    // CLSID (there can be hundreds) would just be noise, and the HashSet-then-single-pass shape
    // keeps the actual comparison cheap regardless.
    private static void AddComHijackItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            var hklmClsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID");
                if (hklmKey is not null)
                {
                    foreach (var name in hklmKey.GetSubKeyNames()) hklmClsids.Add(name);
                }
            }
            catch
            {
                // If HKLM enumeration fails, the shadow-check below simply never matches - the
                // user-writable-path check still runs independently.
            }

            using var hkcuKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID");
            if (hkcuKey is null) return;

            foreach (var clsid in hkcuKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = hkcuKey.OpenSubKey(clsid);
                    string? dllPath = null;
                    using (var inproc = sub?.OpenSubKey("InprocServer32"))
                    {
                        dllPath = inproc?.GetValue(null) as string;
                    }
                    var expandedDll = string.IsNullOrWhiteSpace(dllPath) ? string.Empty : Environment.ExpandEnvironmentVariables(dllPath);
                    var location = $@"HKCU\Software\Classes\CLSID\{clsid}";

                    bool shadowsHklm = hklmClsids.Contains(clsid);
                    bool loadsFromUserWritable = !string.IsNullOrWhiteSpace(expandedDll) &&
                        (expandedDll.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
                         expandedDll.Contains("Temp", StringComparison.OrdinalIgnoreCase));

                    if (!shadowsHklm && !loadsFromUserWritable) continue;

                    var entry = new AutorunEntry
                    {
                        Category = "COM Hijack",
                        Name = clsid,
                        RawCommand = dllPath ?? string.Empty,
                        ResolvedPath = expandedDll,
                        Publisher = "Unknown",
                        SignatureStatus = !string.IsNullOrWhiteSpace(expandedDll) && System.IO.File.Exists(expandedDll) ? SignatureCheckService.GetStatus(expandedDll) : "Unknown",
                        Location = location,
                        Enabled = true,
                    };
                    items.Add(entry);

                    if (shadowsHklm)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.High,
                            Title = $"Per-user COM registration shadows machine-wide CLSID {clsid}",
                            Reason = $"A per-user COM registration for CLSID {clsid} shadows the machine-wide one for this user - silently redirects whatever calls this CLSID.",
                            Path = location,
                            WhatDisablingDoes = "Deleting this HKCU CLSID subkey restores the machine-wide registration for this user; only do this for a CLSID you don't recognize. Quick flag, not a verdict.",
                            RelatedEntry = entry,
                        });
                    }

                    if (loadsFromUserWritable)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.High,
                            Title = $"Per-user COM object loads from a user-writable location: {clsid}",
                            Reason = $"Per-user COM object CLSID {clsid} loads \"{expandedDll}\" from a user-writable temp/AppData location.",
                            Path = location,
                            WhatDisablingDoes = "Deleting this HKCU CLSID subkey stops the object from being loadable via COM; only do this for a CLSID you don't recognize. Quick flag, not a verdict.",
                            RelatedEntry = entry,
                        });
                    }
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible - contribute nothing.
        }
    }

    // #828: Shell verb and delay-load hijack check - ShellServiceObjectDelayLoad entries load a
    // CLSID into Explorer at startup (resolved to name/DLL the same way #826/#827 do), and the
    // default shell\open\command for a handful of high-value ProgIDs decides what actually launches
    // for that file/protocol type system-wide. ms-settings is deliberately informational-only (no
    // reliable single "stock" command text exists across Windows builds for this protocol handler).
    private static void AddShellVerbHijackItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddDelayLoadItems(items, findings, Registry.CurrentUser, "HKCU");
        AddDelayLoadItems(items, findings, Registry.LocalMachine, "HKLM");

        static bool IsStockQuotedPercent1(string cmd) => cmd.Trim().Equals("\"%1\" %*", StringComparison.OrdinalIgnoreCase);

        AddShellOpenCommandItem(items, findings, "exefile", IsStockQuotedPercent1);
        AddShellOpenCommandItem(items, findings, "piffile", IsStockQuotedPercent1);
        AddShellOpenCommandItem(items, findings, "comfile", IsStockQuotedPercent1);
        AddShellOpenCommandItem(items, findings, "batfile", IsStockQuotedPercent1);
        AddShellOpenCommandItem(items, findings, "cmdfile", IsStockQuotedPercent1);
        AddShellOpenCommandItem(items, findings, "txtfile", cmd => cmd.Contains("notepad", StringComparison.OrdinalIgnoreCase));
        AddShellOpenCommandItem(items, findings, "mscfile", cmd => cmd.Contains("mmc.exe", StringComparison.OrdinalIgnoreCase));
        AddShellOpenCommandItem(items, findings, "ms-settings", null); // informational only - no reliable stock form across builds
    }

    private static void AddDelayLoadItems(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey hive, string hiveLabel)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellServiceObjectDelayLoad";
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            string location = $@"{hiveLabel}\{keyPath}";
            foreach (var valueName in key.GetValueNames())
            {
                var clsid = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(clsid)) continue;

                var (resolvedName, dllPath) = ResolveClsidToNameAndDll(clsid.Trim());
                var entryLocation = $@"{location}\{valueName}";
                bool exists = !string.IsNullOrWhiteSpace(dllPath) && System.IO.File.Exists(dllPath);
                var status = exists ? SignatureCheckService.GetStatus(dllPath) : "Unknown";

                var entry = new AutorunEntry
                {
                    Category = "Shell Verb Hijack",
                    Name = valueName,
                    RawCommand = clsid,
                    ResolvedPath = dllPath,
                    Publisher = "Unknown",
                    SignatureStatus = status,
                    Location = entryLocation,
                    Enabled = true,
                };
                items.Add(entry);

                if (exists && status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = $"Unsigned ShellServiceObjectDelayLoad entry: {valueName}",
                        Reason = $"\"{valueName}\" delay-loads CLSID {clsid} (\"{(string.IsNullOrWhiteSpace(resolvedName) ? "unresolved" : resolvedName)}\" - \"{dllPath}\") into Explorer at startup, and this DLL doesn't carry a valid Authenticode signature.",
                        Path = entryLocation,
                        WhatDisablingDoes = "Deleting this value stops the object from delay-loading into Explorer at startup; only do this for one you don't recognize. Quick flag, not a verdict.",
                        RelatedEntry = entry,
                    });
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent) - contribute nothing.
        }
    }

    private static void AddShellOpenCommandItem(List<AutorunEntry> items, List<SecurityFinding> findings, string progId, Func<string, bool>? isStock)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var raw = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var location = $@"HKCR\{progId}\shell\open\command";
            var entry = new AutorunEntry
            {
                Category = "Shell Open Command",
                Name = progId,
                RawCommand = raw,
                ResolvedPath = StartupManagerService.ExtractPath(raw),
                Publisher = "Unknown",
                SignatureStatus = "Unknown",
                Location = location,
                Enabled = true,
            };
            items.Add(entry);

            if (isStock is null) return; // ms-settings - informational only, no reliable stock form
            if (isStock(raw)) return;

            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = $"Non-standard shell open command: {progId}",
                Reason = $"The default shell\\open\\command for \"{progId}\" is \"{raw}\", which isn't the stock form Windows normally ships. Every file/action of this type launches through this command.",
                Path = location,
                WhatDisablingDoes = "Restoring the stock command value fixes normal launch behavior for this file type; only do this if you don't recognize the change. Quick flag, not a verdict.",
                RelatedEntry = entry,
            });
        }
        catch
        {
            // Key inaccessible (or absent - unusual for these well-known ProgIDs, but possible on a
            // heavily locked-down or non-standard install) - contribute nothing.
        }
    }

    // #830: Drivers32 codecs, AutoPlay handlers, and the per-user screensaver executable - three
    // small, easily-overlooked auto-loaded/auto-launched locations. Unlike most persistence
    // categories in this file, all three are common and expected to be non-empty on a normal
    // system, so no finding is raised unless the resolved binary is actually unsigned.
    private static void AddCodecAutoPlayScreensaverItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        AddDrivers32Items(items, findings);
        AddAutoPlayHandlerItems(items);
        AddScreensaverItem(items, findings);
    }

    private static void AddDrivers32Items(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            string location = $@"HKLM\{keyPath}";
            foreach (var valueName in key.GetValueNames())
            {
                var dllName = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(dllName)) continue;

                var displayName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName;
                var resolved = System.IO.Path.IsPathRooted(dllName) ? dllName : System.IO.Path.Combine(Environment.SystemDirectory, dllName);
                AddUnsignedOnlyEntry(items, findings, "Codec", displayName, dllName, resolved, $@"{location}\{valueName}",
                    $"Codec \"{displayName}\" (\"{dllName}\") doesn't carry a valid Authenticode signature.");
            }
        }
        catch
        {
            // Key inaccessible (or absent) - contribute nothing.
        }
    }

    /// <summary>AutoPlay handler subkeys reference a ProgID/shell verb, not a DLL/exe path
    /// directly, so there's no resolved binary to check a signature on here - listed only, per
    /// #830's own "keep it simple" scope note (subkey name plus whatever string values are found).</summary>
    private static void AddAutoPlayHandlerItems(List<AutorunEntry> items)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers\Handlers";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return;

            string location = $@"HKLM\{keyPath}";
            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null) continue;

                    var parts = new List<string>();
                    foreach (var valueName in sub.GetValueNames())
                    {
                        if (sub.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s))
                            parts.Add($"{valueName}={s}");
                    }

                    items.Add(new AutorunEntry
                    {
                        Category = "AutoPlay Handler",
                        Name = subName,
                        RawCommand = string.Join("; ", parts),
                        ResolvedPath = string.Empty,
                        Publisher = "Unknown",
                        SignatureStatus = "Unknown",
                        Location = $@"{location}\{subName}",
                        Enabled = true,
                    });
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent) - contribute nothing.
        }
    }

    private static void AddScreensaverItem(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var raw = key?.GetValue("SCRNSAVE.EXE") as string;
            if (string.IsNullOrWhiteSpace(raw)) return; // no screensaver configured - the common case

            var resolved = System.IO.Path.IsPathRooted(raw) ? raw : System.IO.Path.Combine(Environment.SystemDirectory, raw);
            AddUnsignedOnlyEntry(items, findings, "Screensaver", System.IO.Path.GetFileName(raw), raw, resolved, @"HKCU\Control Panel\Desktop\SCRNSAVE.EXE",
                $"The configured screensaver \"{raw}\" runs automatically after the idle timeout and doesn't carry a valid Authenticode signature.");
        }
        catch
        {
            // Key inaccessible (or absent - no screensaver configured) - contribute nothing.
        }
    }

    /// <summary>Shared helper for the several #830/#833 categories that only produce a finding
    /// when the resolved binary is unsigned (Low severity) - every entry here is otherwise common
    /// and expected, unlike most of this file's other persistence categories.</summary>
    private static void AddUnsignedOnlyEntry(List<AutorunEntry> items, List<SecurityFinding> findings, string category, string name, string rawCommand, string resolvedPath, string location, string reason)
    {
        bool exists = !string.IsNullOrWhiteSpace(resolvedPath) && System.IO.File.Exists(resolvedPath);
        var status = exists ? SignatureCheckService.GetStatus(resolvedPath) : "Unknown";
        var entry = new AutorunEntry
        {
            Category = category,
            Name = name,
            RawCommand = rawCommand,
            ResolvedPath = resolvedPath,
            Publisher = "Unknown",
            SignatureStatus = status,
            Location = location,
            Enabled = true,
        };
        items.Add(entry);

        if (!exists || !status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase)) return;

        findings.Add(new SecurityFinding
        {
            Severity = FindingSeverity.Low,
            Title = $"Unsigned {category.ToLowerInvariant()}: {name}",
            Reason = reason,
            Path = location,
            WhatDisablingDoes = "Removing this registration/file stops it from being used; only do this for one you don't recognize. Quick flag, not a verdict.",
            RelatedEntry = entry,
        });
    }

    // #831: scheduled-task security lens - findings-only pass (no new AutorunEntry rows; these
    // tasks are already visible on the Startup tab's own Scheduled Tasks grid) over the aggregated
    // per-task XML data ScheduledTaskService.QuerySecurityInfoAsync reads in one shell-out. Flags,
    // per task: the Hidden flag; registration outside \Microsoft\Windows\; an action that runs a
    // script host (powershell/wscript/cscript/mshta/rundll32); an action executable living in a
    // user-writable location (same heuristic as #821's BuildUserWritableRoots); and running as
    // SYSTEM while registered outside \Microsoft\ at all. One combined finding per flagged task
    // (not one per issue) - same "list what's wrong, then one finding" shape #820/#821 already use
    // for kernel drivers/service hygiene. NOTE: the "XML in System32\Tasks has no matching registry
    // entry (or vice versa)" cross-check is intentionally NOT implemented here - see this chunk's
    // report for why (Implemented-partially).
    private static readonly string[] ScheduledTaskScriptHosts = { "powershell", "wscript", "cscript", "mshta", "rundll32" };

    private static void AddScheduledTaskSecurityFindings(List<SecurityFinding> findings)
    {
        List<ScheduledTaskXmlInfo> tasks;
        try
        {
            tasks = ScheduledTaskService.QuerySecurityInfoAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return; // degrade to nothing, same as every other optional data source in this file
        }

        var userWritableRoots = BuildUserWritableRoots();

        foreach (var task in tasks)
        {
            try
            {
                bool underMicrosoftWindows = task.FolderPath.Equals(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
                    || task.FolderPath.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
                bool underMicrosoft = task.FolderPath.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase)
                    || task.FolderPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
                bool isSystem = task.RunAsUser.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase)
                    || task.RunAsUser.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
                bool isScriptHost = ScheduledTaskScriptHosts.Any(h => task.ActionCommand.Contains(h, StringComparison.OrdinalIgnoreCase));

                var actionExePath = StartupManagerService.ExtractPath(task.ActionCommand);
                var expandedActionPath = string.IsNullOrWhiteSpace(actionExePath) ? string.Empty : Environment.ExpandEnvironmentVariables(actionExePath);
                bool isUserWritable = !string.IsNullOrWhiteSpace(expandedActionPath) &&
                    userWritableRoots.Any(root => !string.IsNullOrWhiteSpace(root) && expandedActionPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));

                var issues = new List<string>();
                if (task.IsHidden) issues.Add("is hidden from Task Scheduler's default view (Hidden flag set)");
                if (!underMicrosoftWindows) issues.Add($"is registered outside \\Microsoft\\Windows\\ (folder: {task.FolderPath})");
                if (isScriptHost) issues.Add($"runs a script host in its action (\"{task.ActionCommand}\")");
                if (isUserWritable) issues.Add($"its action executable lives in a user-writable location (\"{expandedActionPath}\")");
                if (isSystem && !underMicrosoft) issues.Add("runs as SYSTEM but its registered folder isn't under \\Microsoft\\");

                if (issues.Count == 0) continue;

                var severity = (isUserWritable || (isSystem && !underMicrosoft)) ? FindingSeverity.High
                    : isScriptHost ? FindingSeverity.Medium
                    : FindingSeverity.Low;

                findings.Add(new SecurityFinding
                {
                    Severity = severity,
                    Title = $"Scheduled task worth checking: {task.Name}",
                    Reason = $"Task \"{task.Name}\" (look it up by this exact name on the Startup tab's Scheduled Tasks grid) {string.Join("; ", issues)}.",
                    Path = task.Name,
                    WhatDisablingDoes = "Disabling the task (Task Scheduler, or the Startup tab's own Scheduled Tasks grid Enable/Disable action) stops it from running without deleting its registration; only do this for one you don't recognize - plenty of legitimate first-party and third-party software registers tasks outside \\Microsoft\\Windows\\, runs script-host actions, or hides its task deliberately. Quick flag, not a verdict.",
                });
            }
            catch
            {
                // One bad task record shouldn't stop the rest.
            }
        }
    }

    // #832: BITS transfer job audit - `bitsadmin.exe /list /allusers /verbose` output isn't a
    // documented stable contract, so this parses leniently: each job's block is delimited by the
    // next "DISPLAY:" line (bitsadmin always starts a job's block with that field), then every
    // "LABEL: value" line within a block is captured into a dictionary and only the fields this
    // method actually wants are read back out - an unexpected/reordered/missing field degrades
    // that one field rather than the whole row. bitsadmin.exe is a deprecated tool (absent on some
    // newer Windows builds/SKUs) - wrapped in one broad try/catch, degrades to empty on failure.
    private static void AddBitsJobItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        try
        {
            string output = RunCapturedSync("bitsadmin.exe", "/list /allusers /verbose", TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(output)) return;

            foreach (var block in SplitBitsJobBlocks(output))
            {
                var fields = ParseLabeledLines(block);
                // DISPLAY is present on essentially every bitsadmin verbose job block - use it as
                // the "is this actually a job block" signal, not just leftover header/footer text.
                if (!fields.TryGetValue("DISPLAY", out var display) || string.IsNullOrWhiteSpace(display)) continue;

                fields.TryGetValue("TYPE", out var type);
                fields.TryGetValue("STATE", out var state);
                fields.TryGetValue("OWNER", out var owner);
                string? remote = FirstPresent(fields, "REMOTE FILE NAME", "REMOTE NAME", "REMOTE URL", "URL");
                string? local = FirstPresent(fields, "LOCAL FILE NAME", "LOCAL NAME");

                var rawParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(type)) rawParts.Add($"Type: {type}");
                if (!string.IsNullOrWhiteSpace(remote)) rawParts.Add($"Remote: {remote}");
                if (!string.IsNullOrWhiteSpace(local)) rawParts.Add($"Local: {local}");
                if (!string.IsNullOrWhiteSpace(owner)) rawParts.Add($"Owner: {owner}");
                if (!string.IsNullOrWhiteSpace(state)) rawParts.Add($"State: {state}");

                var location = $"bitsadmin /list: {display}";
                var entry = new AutorunEntry
                {
                    Category = "BITS Job",
                    Name = display,
                    RawCommand = string.Join(", ", rawParts),
                    ResolvedPath = local ?? string.Empty,
                    Publisher = "Unknown",
                    SignatureStatus = "Unknown",
                    Location = location,
                    Enabled = true,
                };
                items.Add(entry);

                bool suspendedOrError = !string.IsNullOrWhiteSpace(state) &&
                    (state.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || state.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
                if (!suspendedOrError) continue;

                findings.Add(new SecurityFinding
                {
                    Severity = FindingSeverity.Low,
                    Title = $"BITS job in {state} state: {display}",
                    Reason = $"BITS transfer job \"{display}\" (owner: {(string.IsNullOrWhiteSpace(owner) ? "Unknown" : owner)}) is in state \"{state}\". BITS jobs run detached from any visible process, and this app can't reliably determine how long-lived a job actually is from bitsadmin's text output, so only the state itself is flagged here (long-lived duration math was skipped, per this item's own scope note).",
                    Path = location,
                    WhatDisablingDoes = "Cancelling the job (`bitsadmin /cancel <jobid>` or `bitsadmin /reset` for all of them) removes it; only do this for a job you don't recognize, since legitimate software (Windows Update, some installers) uses BITS deliberately and a suspended/errored job is often just a stalled/interrupted download waiting to resume. Quick flag, not a verdict.",
                    RelatedEntry = entry,
                });
            }
        }
        catch
        {
            // bitsadmin.exe unavailable (deprecated, removed on some Windows versions/SKUs)/
            // failed/timed out - contribute nothing, same tradeoff as this file's other
            // shelled-out data sources.
        }
    }

    private static List<string> SplitBitsJobBlocks(string output)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.TrimStart().StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                rawLine.TrimStart()["DISPLAY".Length..].TrimStart().StartsWith(":", StringComparison.Ordinal) &&
                current.Count > 0)
            {
                blocks.Add(string.Join("\n", current));
                current.Clear();
            }
            current.Add(rawLine);
        }
        if (current.Count > 0) blocks.Add(string.Join("\n", current));
        return blocks;
    }

    private static Dictionary<string, string> ParseLabeledLines(string block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            int colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim().ToUpperInvariant();
            var value = line[(colonIdx + 1)..].Trim().Trim('\'', '"');
            if (key.Length == 0 || value.Length == 0) continue;
            if (!result.ContainsKey(key)) result[key] = value; // first occurrence wins
        }
        return result;
    }

    private static string? FirstPresent(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (fields.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    // #833: Office/Outlook COM add-in inventory - HKCU/HKLM (and Wow6432Node) Addins subkeys per
    // Office app, LoadBehavior decoded to a friendly string and the add-in's DLL resolved through
    // its ProgID -> CLSID -> InprocServer32 (the same CLSID resolution #826-828 already use), plus
    // a simple directory listing of the Word/Excel STARTUP folders (both the per-user AppData
    // copy and, when present, the Program Files copy under whatever Office version folder is
    // actually installed - found by directory search rather than a hardcoded "OfficeNN", since
    // that number changes across Office versions). No finding unless a resolved add-in DLL or
    // startup-folder file is unsigned (Low severity) - both are common and expected on a system
    // with Office installed.
    private static readonly string[] OfficeApps = { "Word", "Excel", "Outlook", "PowerPoint" };

    private static void AddOfficeAddinItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        foreach (var app in OfficeApps)
        {
            AddOfficeAddinRegistryItems(items, findings, Registry.CurrentUser, "HKCU", $@"SOFTWARE\Microsoft\Office\{app}\Addins", app);
            AddOfficeAddinRegistryItems(items, findings, Registry.LocalMachine, "HKLM", $@"SOFTWARE\Microsoft\Office\{app}\Addins", app);
            AddOfficeAddinRegistryItems(items, findings, Registry.LocalMachine, "HKLM (32-bit)", $@"SOFTWARE\Wow6432Node\Microsoft\Office\{app}\Addins", app);
        }

        AddOfficeStartupFolderItems(items, findings, Environment.ExpandEnvironmentVariables(@"%AppData%\Microsoft\Word\STARTUP"), "Word");
        AddOfficeStartupFolderItems(items, findings, Environment.ExpandEnvironmentVariables(@"%AppData%\Microsoft\Excel\XLSTART"), "Excel");
        foreach (var dir in FindProgramFilesOfficeFolders("STARTUP"))
            AddOfficeStartupFolderItems(items, findings, dir, "Word (Program Files)");
        foreach (var dir in FindProgramFilesOfficeFolders("XLSTART"))
            AddOfficeStartupFolderItems(items, findings, dir, "Excel (Program Files)");
    }

    private static void AddOfficeAddinRegistryItems(List<AutorunEntry> items, List<SecurityFinding> findings, RegistryKey hive, string hiveLabel, string keyPath, string appName)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return;

            string location = $@"{hiveLabel}\{keyPath}";
            foreach (var progId in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(progId);
                    if (sub is null) continue;

                    string loadBehaviorText = sub.GetValue("LoadBehavior") is int lb
                        ? lb switch
                        {
                            0 => "Disabled",
                            3 or 9 => "Loads at startup",
                            8 => "Loaded on demand",
                            _ => "Other",
                        }
                        : "Unknown";

                    var friendlyName = sub.GetValue("FriendlyName") as string ?? sub.GetValue("Description") as string;
                    var displayName = string.IsNullOrWhiteSpace(friendlyName) ? progId : friendlyName;

                    var dllPath = ResolveProgIdToDll(progId);
                    bool exists = !string.IsNullOrWhiteSpace(dllPath) && System.IO.File.Exists(dllPath);
                    var status = exists ? SignatureCheckService.GetStatus(dllPath) : "Unknown";
                    var entryLocation = $@"{location}\{progId}";

                    var entry = new AutorunEntry
                    {
                        Category = "Office Add-in",
                        Name = $"{appName}: {displayName}",
                        RawCommand = $"LoadBehavior: {loadBehaviorText}",
                        ResolvedPath = dllPath,
                        Publisher = "Unknown",
                        SignatureStatus = status,
                        Location = entryLocation,
                        Enabled = loadBehaviorText != "Disabled",
                    };
                    items.Add(entry);

                    if (exists && status.Equals("Unsigned", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Severity = FindingSeverity.Low,
                            Title = $"Unsigned Office add-in: {appName} - {displayName}",
                            Reason = $"The {appName} add-in \"{displayName}\" ({progId}) loads \"{dllPath}\" and doesn't carry a valid Authenticode signature. LoadBehavior: {loadBehaviorText}.",
                            Path = entryLocation,
                            WhatDisablingDoes = "Setting LoadBehavior to 0 (via regedit, or disabling the add-in from within the Office app's own Add-ins dialog) stops it from loading; only do this for one you don't recognize. Quick flag, not a verdict.",
                            RelatedEntry = entry,
                        });
                    }
                }
                catch
                {
                    // One bad subkey shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // Key inaccessible (or absent - the common case without the matching Office app
            // installed) - contribute nothing.
        }
    }

    private static string ResolveProgIdToDll(string progId)
    {
        try
        {
            using var progIdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\CLSID");
            var clsid = progIdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(clsid)) return string.Empty;

            var (_, dll) = ResolveClsidToNameAndDll(clsid.Trim());
            return dll;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> FindProgramFilesOfficeFolders(string subFolderName)
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            var officeRoot = System.IO.Path.Combine(root, "Microsoft Office", "root");
            if (!System.IO.Directory.Exists(officeRoot)) continue;

            string[] versionDirs;
            try { versionDirs = System.IO.Directory.GetDirectories(officeRoot, "Office*"); }
            catch { continue; }

            foreach (var versionDir in versionDirs)
            {
                var candidate = System.IO.Path.Combine(versionDir, subFolderName);
                if (System.IO.Directory.Exists(candidate)) yield return candidate;
            }
        }
    }

    private static void AddOfficeStartupFolderItems(List<AutorunEntry> items, List<SecurityFinding> findings, string folderPath, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath)) return;

            foreach (var file in System.IO.Directory.GetFiles(folderPath))
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                AddUnsignedOnlyEntry(items, findings, "Office Startup Folder", $"{label}: {fileName}", file, file, file,
                    $"\"{fileName}\" in the {label} startup folder (\"{folderPath}\") loads automatically and doesn't carry a valid Authenticode signature.");
            }
        }
        catch
        {
            // Folder inaccessible - contribute nothing.
        }
    }

    // #834: AppX/UWP - implemented reliably for the actionable half only. Provisioned packages
    // (this method) reinstall for every new user profile on the machine, which is the genuinely
    // useful/actionable half of this item, read via a single `Get-AppxProvisionedPackage -Online`
    // PowerShell call (DISM-backed, documented cmdlet) rather than raw interop. The "auto-start"
    // half (which AppX packages register a StartupTask and what state it's in) is
    // Implemented-partially: reading it needs an undocumented AppModel\SystemAppData registry
    // shape with no stable contract across Windows builds - too fragile to build against reliably
    // here, per this item's own note. Informational rows only - no findings.
    private static void AddProvisionedAppxPackageItems(List<AutorunEntry> items)
    {
        try
        {
            string output = RunCapturedSync("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-AppxProvisionedPackage -Online | Select-Object DisplayName,PackageName | ConvertTo-Csv -NoTypeInformation\"",
                TimeSpan.FromSeconds(20));
            if (string.IsNullOrWhiteSpace(output)) return;

            var lines = output.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count < 2) return; // header only (or command failed/unavailable) - no packages to show

            const string location = "Get-AppxProvisionedPackage -Online";
            foreach (var line in lines.Skip(1))
            {
                var fields = ParseSimpleCsvLine(line);
                if (fields.Count == 0 || string.IsNullOrWhiteSpace(fields[0])) continue;

                items.Add(new AutorunEntry
                {
                    Category = "Provisioned AppX Package",
                    Name = fields[0],
                    RawCommand = fields.Count > 1 ? fields[1] : string.Empty,
                    ResolvedPath = string.Empty,
                    Publisher = "Unknown",
                    SignatureStatus = "Unknown",
                    Location = location,
                    Enabled = true,
                });
            }
        }
        catch
        {
            // PowerShell/DISM cmdlet unavailable/failed/timed out - contribute nothing.
        }
    }

    /// <summary>Small hand-rolled CSV-line parser (quoted fields, "" escapes an embedded quote) -
    /// the same shape ScheduledTaskService.ParseCsvLine already uses for schtasks' own CSV output,
    /// kept as a local copy here since PowerShell's ConvertTo-Csv output is this file's only CSV
    /// consumer and taking a shared dependency for one dozen-line parser isn't worth it.</summary>
    private static List<string> ParseSimpleCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    // #835: startup shortcut target tampering - resolves every .lnk in the Start Menu, Desktop,
    // Taskbar-pinned folder, and both Startup folders (Environment.SpecialFolder.Startup/
    // CommonStartup - the exact same two paths StartupManagerService.Sample already scans, reused
    // rather than re-derived) via the WScript.Shell COM object. This app has no existing
    // .lnk-parsing code anywhere (checked: no WshShell/IShellLink/.lnk usage in Services/ before
    // this), so this is late-bound reflection (Type.GetTypeFromProgID + InvokeMember) rather than
    // `dynamic` - avoids taking a new Microsoft.CSharp project reference for one call site. A
    // shortcut that throws on CreateShortcut/property access (malformed, inaccessible, or a
    // non-file-system .lnk target) is skipped rather than failing the whole scan.
    //
    // ResolvedPath/Location are deliberately the .lnk file's OWN path, not its resolved target -
    // SecurityViewModel.OpenContainingFolder opens Explorer at ResolvedPath, and the useful thing
    // to jump to for a shortcut finding is the shortcut itself (in the Start Menu/Desktop/Startup
    // folder it actually lives in), not the target program's install folder.
    private static readonly string[] BrowserExeNames = { "chrome.exe", "msedge.exe", "firefox.exe", "iexplore.exe" };

    private static void AddShortcutTamperingItems(List<AutorunEntry> items, List<SecurityFinding> findings)
    {
        var folders = new (string Path, bool Recursive)[]
        {
            (Environment.ExpandEnvironmentVariables(@"%AppData%\Microsoft\Windows\Start Menu\Programs"), true),
            (Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu\Programs"), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.ExpandEnvironmentVariables(@"%Public%\Desktop"), false),
            (Environment.ExpandEnvironmentVariables(@"%AppData%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), false),
        };

        var tempRoot = Environment.GetEnvironmentVariable("TEMP") ?? string.Empty;
        string appDataRoot;
        try { appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch { appDataRoot = string.Empty; }

        foreach (var (folderPath, recursive) in folders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath)) continue;

                var searchOption = recursive ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                foreach (var lnkPath in System.IO.Directory.EnumerateFiles(folderPath, "*.lnk", searchOption))
                {
                    AddOneShortcutItem(items, findings, lnkPath, tempRoot, appDataRoot);
                }
            }
            catch
            {
                // Folder inaccessible/enumeration failed - skip this folder, keep scanning the rest.
            }
        }
    }

    private static void AddOneShortcutItem(List<AutorunEntry> items, List<SecurityFinding> findings, string lnkPath, string tempRoot, string appDataRoot)
    {
        var resolved = TryResolveShortcut(lnkPath);
        if (resolved is null) return; // malformed/inaccessible - skip, per this item's own guidance

        var (target, arguments) = resolved.Value;
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(arguments)) return;

        var expandedTarget = string.IsNullOrWhiteSpace(target) ? string.Empty : Environment.ExpandEnvironmentVariables(target);
        bool targetExists = !string.IsNullOrWhiteSpace(expandedTarget) && System.IO.File.Exists(expandedTarget);
        var status = targetExists ? SignatureCheckService.GetStatus(expandedTarget) : "Unknown";

        var entry = new AutorunEntry
        {
            Category = "Shortcut",
            Name = System.IO.Path.GetFileNameWithoutExtension(lnkPath),
            RawCommand = string.IsNullOrWhiteSpace(arguments) ? target : $"{target} {arguments}",
            ResolvedPath = lnkPath,
            Publisher = "Unknown",
            SignatureStatus = status,
            Location = lnkPath,
            Enabled = true,
        };
        items.Add(entry);

        bool targetIsBrowser = !string.IsNullOrWhiteSpace(expandedTarget) &&
            BrowserExeNames.Any(b => System.IO.Path.GetFileName(expandedTarget).Equals(b, StringComparison.OrdinalIgnoreCase));
        bool argsLookLikeUrl = !string.IsNullOrWhiteSpace(arguments) &&
            (arguments.Contains("http", StringComparison.OrdinalIgnoreCase) || arguments.Contains("www.", StringComparison.OrdinalIgnoreCase));
        bool targetUnderTempOrAppData = !string.IsNullOrWhiteSpace(expandedTarget) &&
            ((!string.IsNullOrWhiteSpace(tempRoot) && expandedTarget.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(appDataRoot) && expandedTarget.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase)));

        if (targetIsBrowser && argsLookLikeUrl)
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = $"Browser shortcut launches a URL: {entry.Name}",
                Reason = $"\"{lnkPath}\" launches \"{System.IO.Path.GetFileName(expandedTarget)}\" with arguments \"{arguments}\" - a browser shortcut with a URL baked into its arguments is the classic hijacked-homepage/malvertising trick, since it silently overrides whatever homepage/new-tab page is actually configured in the browser.",
                Path = lnkPath,
                WhatDisablingDoes = "Use this entry's \"Open containing folder\" action to find the shortcut, then right-click it and choose Properties to inspect/clear the Target field's arguments (or just delete and re-create the shortcut) if you don't recognize the URL. Quick flag, not a verdict - some legitimate app launchers do pass a URL deliberately (e.g. a \"View documentation\" shortcut).",
                RelatedEntry = entry,
            });
        }
        else if (targetUnderTempOrAppData)
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.Medium,
                Title = $"Shortcut target under Temp/AppData: {entry.Name}",
                Reason = $"\"{lnkPath}\" points at \"{expandedTarget}\", under a Temp/AppData location - common for legitimately-installed AppData-based software (Electron apps, browser updaters, ...), but also a common malware drop location, so worth a glance.",
                Path = lnkPath,
                WhatDisablingDoes = "Use this entry's \"Open containing folder\" action to find the shortcut, then Properties to inspect its Target; delete the shortcut (and, if you don't recognize the target program, the target itself) if you don't recognize it. Quick flag, not a verdict.",
                RelatedEntry = entry,
            });
        }
    }

    /// <summary>Resolves a .lnk's target path and arguments via the WScript.Shell COM object,
    /// late-bound through reflection rather than `dynamic` - see AddShortcutTamperingItems' remarks
    /// on why. Returns null for anything that throws (malformed/inaccessible shortcut, or a
    /// non-file-system target COM doesn't expose as a plain path/arguments pair).</summary>
    private static (string Target, string Arguments)? TryResolveShortcut(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut is null) return null;

            var shortcutType = shortcut.GetType();
            var target = shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
            var arguments = shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty;
            return (target, arguments);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Synchronous shell-out-and-capture, mirroring ScheduledTaskService.RunCapturedAsync's
    /// concurrent-read/bounded-wait/kill-on-timeout shape but blocking - AutorunsService.Scan is a
    /// synchronous engine called from inside a Task.Run by SecurityViewModel, and #818's netsh call
    /// is the only shelled-out command in this file, so a small dedicated sync helper is simpler
    /// than threading async through the whole class for one caller.</summary>
    private static string RunCapturedSync(string exe, string args, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return string.Empty;
        }

        return outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
    }

    private static bool ReadDwordAsBool(RegistryKey key, string valueName)
        => key.GetValue(valueName) is int i && i != 0;

    /// <summary>Builds an AutorunEntry from a raw registry command string, resolving a bare path
    /// via StartupManagerService.ExtractPath (shared with the Startup tab's own parsing) and the
    /// signature status via SignatureCheckService's shared per-path cache.</summary>
    private static AutorunEntry BuildEntry(string category, string name, string rawCommand, string location)
    {
        var path = StartupManagerService.ExtractPath(rawCommand);
        return new AutorunEntry
        {
            Category = category,
            Name = name,
            RawCommand = rawCommand,
            ResolvedPath = path,
            Publisher = "Unknown",
            SignatureStatus = SignatureCheckService.GetStatus(path),
            Location = location,
            Enabled = true,
        };
    }
}
