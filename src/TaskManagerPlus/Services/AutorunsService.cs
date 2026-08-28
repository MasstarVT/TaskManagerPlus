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
