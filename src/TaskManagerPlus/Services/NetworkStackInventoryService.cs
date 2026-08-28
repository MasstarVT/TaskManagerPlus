using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One WFP provider entry (#571) - a registered feature that adds filters (a VPN client,
/// an antivirus, "network optimizer" software, and Windows itself all register one each).</summary>
public sealed record WfpProviderInfo(string Name, string Description);

/// <summary>One WFP callout entry (#571) - the actual inspection/modification code a provider
/// plugs into the filtering pipeline; several stacked callouts on the same layer is the classic
/// "three security products fighting over the same traffic" symptom.</summary>
public sealed record WfpCalloutInfo(string Name, string Description);

/// <summary>One NDIS protocol/filter driver bound to one adapter (#571's registry half) -
/// <see cref="LooksNonMicrosoft"/> flags a driver whose own file isn't published by Microsoft (via
/// its file's Company Name - the same "good enough for a quick flag, not a verdict" tradeoff
/// SignatureCheckService already takes for a different question), never a claim it's malicious.</summary>
public sealed record BoundNetworkFilterDriver(string AdapterName, string ServiceName, string DisplayName, bool LooksNonMicrosoft);

public sealed record NetworkStackInventoryResult(
    List<WfpProviderInfo> Providers, List<WfpCalloutInfo> Callouts, bool WfpStateAvailable,
    List<BoundNetworkFilterDriver> BoundDrivers);

/// <summary>
/// Item #571 (suggestions.md "Firewall rules and blocked connections"): a read-only, informational
/// inventory of what's actually stacked on top of the network stack - a common, hard-to-see cause
/// of unexplained latency/drops that no single vendor's own UI surfaces (a VPN client, an
/// antivirus's network-inspection driver, and a "network optimizer" can all be simultaneously
/// installed and none of them shows you the other two).
///
/// Two independent sources, both best-effort:
/// - <see cref="ReadWfpStateAsync"/>: `netsh wfp show state file=-` (piped to stdout rather than a
///   temp file), a documented-but-informally-specified XML dump of the WFP engine's providers and
///   callouts. The exact element names below reflect the schema as observed; if a future Windows
///   build changes it, this degrades to an empty list (WfpStateAvailable stays true but the lists
///   are empty) rather than throwing or fabricating entries - same "never fabricate" convention as
///   every other best-effort parse in this app.
/// - <see cref="ReadBoundFilterDrivers"/>: every NDIS protocol/filter driver service under
///   HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Linkage\Bind whose Bind list names a given
///   adapter's device GUID - the same registry mechanism Device Manager's own per-adapter
///   "Properties" checklist reads from, and the item's own suggested source.
/// </summary>
public static class NetworkStackInventoryService
{
    public static async Task<NetworkStackInventoryResult> ReadAsync()
    {
        var (providers, callouts, wfpOk) = await ReadWfpStateAsync();
        var bound = ReadBoundFilterDrivers();
        return new NetworkStackInventoryResult(providers, callouts, wfpOk, bound);
    }

    private static async Task<(List<WfpProviderInfo> Providers, List<WfpCalloutInfo> Callouts, bool Available)> ReadWfpStateAsync()
    {
        try
        {
            string output = await RunAsync("netsh.exe", "wfp show state file=-", 45000);
            int start = output.IndexOf('<');
            int end = output.LastIndexOf('>');
            if (start < 0 || end <= start) return (new(), new(), false);

            var doc = XDocument.Parse(output[start..(end + 1)]);

            var providers = new List<WfpProviderInfo>();
            var providersRoot = doc.Descendants("providers").FirstOrDefault();
            if (providersRoot is not null)
            {
                foreach (var item in providersRoot.Elements("item"))
                {
                    string name = item.Element("displayData")?.Element("name")?.Value?.Trim() ?? string.Empty;
                    if (name.Length == 0) continue;
                    string desc = item.Element("displayData")?.Element("description")?.Value?.Trim() ?? string.Empty;
                    providers.Add(new WfpProviderInfo(name, desc));
                }
            }

            var callouts = new List<WfpCalloutInfo>();
            var calloutsRoot = doc.Descendants("callouts").FirstOrDefault();
            if (calloutsRoot is not null)
            {
                foreach (var item in calloutsRoot.Elements("item"))
                {
                    string name = item.Element("displayData")?.Element("name")?.Value?.Trim() ?? string.Empty;
                    if (name.Length == 0) continue;
                    string desc = item.Element("displayData")?.Element("description")?.Value?.Trim() ?? string.Empty;
                    callouts.Add(new WfpCalloutInfo(name, desc));
                }
            }

            return (
                providers.DistinctBy(p => p.Name).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                callouts.DistinctBy(c => c.Name).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                true);
        }
        catch
        {
            return (new(), new(), false);
        }
    }

    private static List<BoundNetworkFilterDriver> ReadBoundFilterDrivers()
    {
        var results = new List<BoundNetworkFilterDriver>();
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .Select(ni => (ni.Name, Guid: ni.Id.Trim('{', '}')))
                .Where(a => a.Guid.Length > 0)
                .ToList();
            if (adapters.Count == 0) return results;

            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return results;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                List<string> binds;
                string displayName;
                string? imagePath;
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(serviceName);
                    if (svcKey is null) continue;
                    using var linkageKey = svcKey.OpenSubKey("Linkage");
                    if (linkageKey?.GetValue("Bind") is not string[] bindArray || bindArray.Length == 0) continue;

                    binds = bindArray.ToList();
                    displayName = svcKey.GetValue("DisplayName") as string ?? serviceName;
                    imagePath = svcKey.GetValue("ImagePath") as string;
                }
                catch
                {
                    continue; // a handful of service keys can be access-restricted even to admins - skip, don't fail the whole sweep
                }

                bool nonMicrosoft = LooksNonMicrosoft(imagePath);
                foreach (var (adapterName, guid) in adapters)
                {
                    if (binds.Any(b => b.Contains(guid, StringComparison.OrdinalIgnoreCase)))
                        results.Add(new BoundNetworkFilterDriver(adapterName, serviceName, displayName, nonMicrosoft));
                }
            }
        }
        catch
        {
            // Best-effort - return whatever was gathered before the failure.
        }
        return results.OrderBy(r => r.AdapterName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resolves a service's ImagePath (which can be an NT path like
    /// "\SystemRoot\System32\drivers\x.sys", a "\??\"-prefixed native path, or a plain relative
    /// filename) to a real file, then checks its Company Name - the same technique this app would
    /// use for any "who published this file" question, applied here instead of
    /// SignatureCheckService's Authenticode check since a driver's Company Name is a cheaper, single
    /// read that doesn't need the file's bytes hashed. Never flags on a path this can't resolve or
    /// read - false (not flagged) is the safe default for missing information.</summary>
    private static bool LooksNonMicrosoft(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        try
        {
            string path = imagePath.Trim().Trim('"');
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            path = Environment.ExpandEnvironmentVariables(path);

            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(windowsDir, path[@"\SystemRoot\".Length..]);
            else if (path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(windowsDir, path);
            else if (!Path.IsPathRooted(path))
                path = Path.Combine(windowsDir, "System32", path);

            if (!File.Exists(path)) return false;

            string? company = FileVersionInfo.GetVersionInfo(path).CompanyName;
            return !string.IsNullOrWhiteSpace(company) && !company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string exe, string args, int timeoutMs)
    {
        try
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
        catch
        {
            return string.Empty;
        }
    }
}
