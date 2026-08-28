using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #682/#683/#684: PCIe link speed/width per GPU and NVMe device, plus a best-effort Thunderbolt-
/// enclosure detection - shelled out to `Get-PnpDeviceProperty`/`Get-PnpDevice` (PowerShell), per
/// this project's prefer-a-known-tool convention (item #682's own text calls this out explicitly:
/// there's no simpler public WMI class for PCIe link state, and raw SetupAPI/CfgMgr32 interop would
/// be a materially larger undertaking for what's ultimately a documented device-property read).
///
/// A real subprocess shell-out, so this is on-demand only (initial load + a manual refresh button
/// on the GPU/Storage tabs) - never on a per-tick timer, the same "PowerShell/tool shell-outs are a
/// heavier read than perf-counter/registry" tradeoff GpuRegistryService's pnputil-based driver
/// history already takes.
///
/// The whole device-and-property-and-Thunderbolt-ancestor walk happens in one PowerShell script (one
/// process, one JSON payload out) rather than one shell-out per device - a system with several NVMe
/// drives would otherwise mean several sequential subprocess round trips for what's fundamentally one
/// small query.
/// </summary>
public static class PciLinkService
{
    // Targets: PCI-bus GPUs (Class=Display) and NVMe controllers (Service stornvme/nvme - the
    // controller node, not the child DiskDrive node, since DEVPKEY_PciDevice_* properties live on
    // the actual PCI function). Walks up to 6 DEVPKEY_Device_Parent hops looking for a Thunderbolt
    // controller ancestor (#684) - bounded so a malformed/cyclic parent chain can't hang the script.
    // Windows PowerShell 5.1's ConvertTo-Json silently unwraps a single-element array back to a
    // bare object, so the 0/1/many cases are handled explicitly to always emit a JSON array.
    private const string Script = """
$ErrorActionPreference = 'SilentlyContinue'
$keys = 'DEVPKEY_PciDevice_CurrentLinkSpeed','DEVPKEY_PciDevice_CurrentLinkWidth','DEVPKEY_PciDevice_MaxLinkSpeed','DEVPKEY_PciDevice_MaxLinkWidth'
$targets = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'PCI\*' -and ($_.Class -eq 'Display' -or $_.Service -match '^(stornvme|nvme)$') }
$results = @()
foreach ($t in $targets) {
  $props = Get-PnpDeviceProperty -InstanceId $t.InstanceId -KeyName $keys
  $byKey = @{}
  foreach ($p in $props) { $byKey[$p.KeyName] = $p.Data }

  $isTb = $false
  $enclosure = $null
  $parentId = (Get-PnpDeviceProperty -InstanceId $t.InstanceId -KeyName 'DEVPKEY_Device_Parent').Data
  $hop = 0
  while ($parentId -and $hop -lt 6) {
    $pd = Get-PnpDevice -InstanceId $parentId -PresentOnly
    if ($pd -and $pd.FriendlyName -match 'Thunderbolt') { $isTb = $true; $enclosure = $pd.FriendlyName; break }
    $parentId = (Get-PnpDeviceProperty -InstanceId $parentId -KeyName 'DEVPKEY_Device_Parent').Data
    $hop++
  }

  $results += [PSCustomObject]@{
    InstanceId = $t.InstanceId
    Name = $t.FriendlyName
    Class = $t.Class
    CurrentLinkSpeed = $byKey['DEVPKEY_PciDevice_CurrentLinkSpeed']
    CurrentLinkWidth = $byKey['DEVPKEY_PciDevice_CurrentLinkWidth']
    MaxLinkSpeed = $byKey['DEVPKEY_PciDevice_MaxLinkSpeed']
    MaxLinkWidth = $byKey['DEVPKEY_PciDevice_MaxLinkWidth']
    IsThunderbolt = $isTb
    EnclosureName = $enclosure
  }
}
if ($results.Count -eq 0) { Write-Output '[]' }
elseif ($results.Count -eq 1) { Write-Output ('[' + ($results[0] | ConvertTo-Json -Compress) + ']') }
else { Write-Output ($results | ConvertTo-Json -Compress) }
""";

    private sealed class RawEntry
    {
        [JsonPropertyName("InstanceId")] public string? InstanceId { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Class")] public string? Class { get; set; }
        [JsonPropertyName("CurrentLinkSpeed")] public int? CurrentLinkSpeed { get; set; }
        [JsonPropertyName("CurrentLinkWidth")] public int? CurrentLinkWidth { get; set; }
        [JsonPropertyName("MaxLinkSpeed")] public int? MaxLinkSpeed { get; set; }
        [JsonPropertyName("MaxLinkWidth")] public int? MaxLinkWidth { get; set; }
        [JsonPropertyName("IsThunderbolt")] public bool IsThunderbolt { get; set; }
        [JsonPropertyName("EnclosureName")] public string? EnclosureName { get; set; }
    }

    /// <summary>Runs the script above, maps its JSON output to <see cref="PciLinkInfo"/>, and
    /// folds in PciLinkHistoryService's boot-over-boot drift comparison. Returns an empty list
    /// (never throws) on any failure - PowerShell missing/blocked, the PnpDevice module not present
    /// on this Windows build, or a malformed/empty result.</summary>
    public static async Task<List<PciLinkInfo>> ReadAllAsync()
    {
        var raw = new List<RawEntry>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(Script);

            using var proc = Process.Start(psi);
            if (proc is null) return new List<PciLinkInfo>();

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(20_000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return new List<PciLinkInfo>();
            }

            string output = await outputTask;
            await errorTask; // drain, ignored - stderr noise (module load warnings etc.) isn't surfaced

            if (!string.IsNullOrWhiteSpace(output))
            {
                raw = JsonSerializer.Deserialize<List<RawEntry>>(output,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<RawEntry>();
            }
        }
        catch
        {
            // PowerShell unreachable, blocked by policy, or the PnpDevice cmdlets aren't present -
            // degrade to "no PCIe link data available" (the GPU/Storage cards hide themselves).
        }

        var mapped = raw
            .Where(r => !string.IsNullOrEmpty(r.InstanceId))
            .Select(r => new PciLinkInfo
            {
                InstanceId = r.InstanceId!,
                Name = string.IsNullOrWhiteSpace(r.Name) ? "Unknown device" : r.Name!,
                Kind = string.Equals(r.Class, "Display", StringComparison.OrdinalIgnoreCase) ? "GPU" : "NVMe",
                CurrentLinkGen = r.CurrentLinkSpeed,
                CurrentLinkWidth = r.CurrentLinkWidth,
                MaxLinkGen = r.MaxLinkSpeed,
                MaxLinkWidth = r.MaxLinkWidth,
                IsThunderboltAttached = r.IsThunderbolt,
                EnclosureName = r.EnclosureName,
            })
            .ToList();

        return PciLinkHistoryService.RecordAndCompare(mapped);
    }
}
