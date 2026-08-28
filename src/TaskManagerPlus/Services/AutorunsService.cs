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
