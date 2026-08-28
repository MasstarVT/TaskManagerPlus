using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #496: two independent reads bundled behind one Load button since neither is expensive on its
/// own - non-base NDIS component bindings per adapter (registry - see ScanNdisFilters' remarks for
/// why this can't be narrowed to lightweight filters specifically) and the Winsock LSP catalog
/// (`netsh winsock show catalog`).
/// </summary>
public static class NetworkFilterService
{
    private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static async Task<(List<NdisFilterBinding> Filters, List<WinsockCatalogEntry> Winsock)> ScanAsync()
    {
        var filters = await Task.Run(ScanNdisFilters);
        var winsock = await ScanWinsockCatalogAsync();
        return (filters, winsock);
    }

    /// <summary>Well-known base protocol/service names that show up in nearly every adapter's own
    /// UpperBind list alongside any genuine third-party filter - excluded so this list reads as
    /// "what's stacked on this adapter beyond the normal Windows network stack" rather than
    /// reprinting TCP/IP on every single row. Not exhaustive (a hand-maintained list, same
    /// disclaimer as LegacyFilterDriverService's base-file-system exclusion set) - an unrecognized
    /// Microsoft component would still show up here, correctly labelled "Microsoft" via the
    /// company-name check below, just not hidden.</summary>
    private static readonly HashSet<string> KnownBaseNetworkComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "tcpip", "tcpip6", "netbt", "lltdio", "mslldp", "rspndr", "ndisuio",
        "wanarp", "wanarpv6", "rasl2tp", "raspppoe", "rassstp", "pptpminiport",
        "ndiswan", "ndiswanlegacy", "netbios", "netbiossmb", "lanmanworkstation",
        "lanmanserver", "rdmandk", "vwifibus", "iphlpsvc",
    };

    /// <summary>#496 (NDIS half): NDIS bindings are recorded per-adapter, not per-filter - each
    /// adapter's own device-setup-class instance key (Control\Class\{4d36e972-...}\NNNN, the SAME
    /// Net class the suggestion text points at) has a \Linkage\UpperBind REG_MULTI_SZ naming every
    /// component (by service name) bound directly above it, protocols and lightweight filters
    /// alike - there's no separate "filters only" registry list to read instead (confirmed against
    /// a real Windows 11 machine's own Control\Class\{4d36e974-...} NetService class, which turned
    /// out to hold no instance data at all on a normal system - that legacy NDIS-5-era per-class
    /// registration mechanism appears vestigial on the modern stack). KnownBaseNetworkComponents
    /// above trims the expected base protocols out of that per-adapter list; each service that
    /// survives is looked up under Services\&lt;name&gt; for its own ImagePath, and classified
    /// third-party the same file-version-company-name way DriverInventoryService's #461 check
    /// already classifies ordinary drivers.</summary>
    private static List<NdisFilterBinding> ScanNdisFilters()
    {
        var byService = new Dictionary<string, NdisFilterBinding>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var adapterNamesByGuid = ReadAdapterFriendlyNames();

            using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{NetClassGuid}");
            if (classKey is null) return new List<NdisFilterBinding>();

            foreach (var instanceName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var instance = classKey.OpenSubKey(instanceName);
                    if (instance is null) continue;

                    string adapterName = instance.GetValue("NetCfgInstanceId") is string cfgId &&
                                          adapterNamesByGuid.TryGetValue(cfgId, out var friendly)
                        ? friendly
                        : instance.GetValue("DriverDesc") as string ?? instanceName;

                    using var linkage = instance.OpenSubKey("Linkage");
                    if (linkage?.GetValue("UpperBind") is not string[] upperBind) continue;

                    foreach (var serviceName in upperBind)
                    {
                        if (string.IsNullOrWhiteSpace(serviceName)) continue;
                        if (KnownBaseNetworkComponents.Contains(serviceName)) continue;

                        if (!byService.TryGetValue(serviceName, out var binding))
                        {
                            binding = BuildBinding(serviceName);
                            byService[serviceName] = binding;
                        }
                        if (!binding.BoundAdapters.Contains(adapterName, StringComparer.OrdinalIgnoreCase))
                            binding.BoundAdapters.Add(adapterName);
                    }
                }
                catch
                {
                    // One malformed/access-denied adapter instance shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Degrade to empty, same as every other registry sweep in this app.
        }
        return byService.Values.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static NdisFilterBinding BuildBinding(string serviceName)
    {
        string displayName = serviceName;
        bool? isThirdParty = null;
        string? componentId = null;
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (svc is not null)
            {
                displayName = svc.GetValue("DisplayName") as string is { Length: > 0 } d ? d : serviceName;
                componentId = svc.GetValue("ComponentId") as string;

                if (svc.GetValue("ImagePath") as string is { Length: > 0 } imagePath)
                {
                    string resolved = ClassFilterDriverService.ResolveDriverPath(imagePath);
                    try
                    {
                        if (File.Exists(resolved))
                        {
                            string? company = FileVersionInfo.GetVersionInfo(resolved).CompanyName?.Trim();
                            isThirdParty = string.IsNullOrEmpty(company) || !company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { /* can't read the file - leave IsThirdParty unknown rather than guess */ }
                }
            }
        }
        catch { /* service key unreadable - still list the raw name from UpperBind */ }

        return new NdisFilterBinding
        {
            FilterServiceName = serviceName,
            DisplayName = displayName,
            ComponentId = componentId,
            IsThirdParty = isThirdParty,
            BoundAdapters = new List<string>(),
        };
    }

    private static Dictionary<string, string> ReadAdapterFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var netKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Network\{NetClassGuid}");
            if (netKey is null) return map;

            foreach (var adapterGuid in netKey.GetSubKeyNames())
            {
                try
                {
                    using var conn = netKey.OpenSubKey($@"{adapterGuid}\Connection");
                    if (conn?.GetValue("Name") is string friendly && friendly.Length > 0)
                        map[adapterGuid] = friendly;
                }
                catch { /* skip this adapter */ }
            }
        }
        catch { /* degrade to empty map - callers fall back to the adapter's own DriverDesc */ }
        return map;
    }

    // ------------------------------------------------------------------------------------------
    // #496 (Winsock half): `netsh winsock show catalog`.
    // ------------------------------------------------------------------------------------------

    private static readonly Regex CatalogFieldRegex = new(@"^\s*([A-Za-z][A-Za-z0-9 /]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    private static async Task<List<WinsockCatalogEntry>> ScanWinsockCatalogAsync()
    {
        var results = new List<WinsockCatalogEntry>();
        string output;
        try
        {
            output = await RunCapturedAsync("netsh.exe", "winsock show catalog", timeoutMs: 20000);
        }
        catch
        {
            return results;
        }

        string windowsDir;
        try { windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        catch { windowsDir = string.Empty; }
        string system32 = windowsDir.Length > 0 ? Path.Combine(windowsDir, "System32") : string.Empty;

        string entryType = string.Empty, description = string.Empty, providerPath = string.Empty, catalogId = string.Empty;
        bool hasEntry = false;

        void Flush()
        {
            if (!hasEntry) return;
            results.Add(new WinsockCatalogEntry
            {
                EntryType = entryType,
                Description = description,
                ProviderPath = providerPath.Length > 0 ? providerPath : null,
                CatalogEntryId = catalogId.Length > 0 ? catalogId : null,
                IsThirdParty = ClassifyThirdParty(providerPath, system32),
            });
            entryType = description = providerPath = catalogId = string.Empty;
            hasEntry = false;
        }

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Contains("Winsock Catalog Provider Entry", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                hasEntry = true;
                continue;
            }

            var match = CatalogFieldRegex.Match(line);
            if (!match.Success) continue;
            string key = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();

            if (key.Equals("Entry Type", StringComparison.OrdinalIgnoreCase)) { entryType = value; hasEntry = true; }
            else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (key.Equals("Provider Path", StringComparison.OrdinalIgnoreCase)) providerPath = Environment.ExpandEnvironmentVariables(value);
            else if (key.Equals("Catalog Entry ID", StringComparison.OrdinalIgnoreCase)) catalogId = value;
        }
        Flush();

        return results;
    }

    /// <summary>Same "under System32 and Microsoft-signed by file-version company name" signal
    /// DriverInventoryService's #461 third-party check already uses for drivers - here checked
    /// against a Winsock provider DLL instead. Any DLL this process can't read (rare - most LSP
    /// DLLs are world-readable) is treated as third-party rather than silently assumed Microsoft.</summary>
    private static bool ClassifyThirdParty(string? providerPath, string system32)
    {
        if (string.IsNullOrWhiteSpace(providerPath)) return false; // nothing to check - don't flag
        try
        {
            if (system32.Length == 0 || !providerPath.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!File.Exists(providerPath)) return true;

            var info = FileVersionInfo.GetVersionInfo(providerPath);
            string? company = info.CompanyName?.Trim();
            return string.IsNullOrEmpty(company) || !company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Same concurrent-read/bounded-wait/kill-on-timeout shape as PnpUtilService's own
    /// RunCapturedAsync - duplicated here rather than shared, matching this app's existing
    /// self-contained-service convention.</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

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
            return string.Empty;
        }

        return (await outputTask) + (await errorTask);
    }
}
