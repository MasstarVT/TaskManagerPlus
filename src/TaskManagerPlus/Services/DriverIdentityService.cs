using System.Diagnostics;
using System.Globalization;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #211: joins a bare driver filename (e.g. "rtwlane.sys", as resolved from a DPC/ISR routine
/// address by DpcModuleMapService) to real metadata - shells out to "driverquery /v /fo csv" for
/// the loaded-driver table (Module Name, Display Name, Link Date) and "pnputil /enum-drivers" for
/// the driver-store package details (Provider Name, Driver Version, Signer Name), the same known-
/// tool-over-raw-interop tradeoff every other shell-out service in this app takes.
///
/// The join between the two is best-effort, not exact: pnputil's "Original Name" is the package's
/// original .inf filename, not the .sys binary driverquery reports, so there's no reliable shared
/// key between the two tools' output. This matches driverquery's module name against each pnputil
/// entry's Original/Published name by substring (e.g. "nvlddmkm" found inside "nvlddmkm.inf") -
/// good enough to resolve many common vendor drivers, expected to miss plenty of others. A miss
/// just means Version/Provider/Signer stay blank ("Unknown" in the UI), never a guessed value -
/// same tier of honesty as SystemSpecsService.ReadChipsetDriverInfo's own best-effort name match.
///
/// Outdated flagging reuses SystemSpecsService.ReadOutdatedDrivers' exact 2-year cutoff and "worth
/// a manual check" framing (there's no real "update available" API for third-party drivers any
/// more than there is for the System Specs tab's own outdated-driver list).
/// </summary>
public static class DriverIdentityService
{
    private static readonly TimeSpan OutdatedCutoff = TimeSpan.FromDays(365 * 2);

    public static async Task<Dictionary<string, DriverIdentityInfo>> LoadAsync()
    {
        var result = new Dictionary<string, DriverIdentityInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dqRows = await RunDriverQueryAsync();
            var pnpRows = await RunPnpUtilAsync();
            var cutoff = DateTime.Now - OutdatedCutoff;

            foreach (var row in dqRows)
            {
                if (string.IsNullOrWhiteSpace(row.ModuleName)) continue;
                string key = row.ModuleName.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                    ? row.ModuleName
                    : row.ModuleName + ".sys";

                string baseName = key[..^4]; // strip ".sys"
                var pnpMatch = baseName.Length >= 4
                    ? pnpRows.FirstOrDefault(p =>
                        p.OriginalName.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
                        p.PublishedName.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                    : null;

                bool isOutdated = row.LinkDate is { } d && d < cutoff;

                result[key] = new DriverIdentityInfo
                {
                    FileName = key,
                    Version = pnpMatch?.Version ?? string.Empty,
                    DriverDate = row.LinkDate?.ToString("yyyy-MM-dd") ?? pnpMatch?.Date ?? string.Empty,
                    Provider = pnpMatch?.Provider ?? row.DisplayName,
                    Signer = pnpMatch?.Signer ?? string.Empty,
                    InfName = pnpMatch?.PublishedName ?? string.Empty,
                    IsOutdated = isOutdated,
                };
            }
        }
        catch
        {
            // best-effort - a partial or empty map just means fewer driver rows get identity text
        }
        return result;
    }

    private sealed record DqRow(string ModuleName, string DisplayName, DateTime? LinkDate);
    private sealed record PnpDriverEntry(string PublishedName, string OriginalName, string Provider, string Version, string Date, string Signer);

    private static async Task<List<DqRow>> RunDriverQueryAsync()
    {
        var rows = new List<DqRow>();
        var (ok, output) = await RunAsync("driverquery.exe", "/v /fo csv", TimeSpan.FromSeconds(25));
        if (!ok || string.IsNullOrWhiteSpace(output)) return rows;

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return rows;

        var header = SplitCsvLine(lines[0]);
        int idxModule = header.FindIndex(h => h.Equals("Module Name", StringComparison.OrdinalIgnoreCase));
        int idxDisplay = header.FindIndex(h => h.Equals("Display Name", StringComparison.OrdinalIgnoreCase));
        int idxLinkDate = header.FindIndex(h => h.Equals("Link Date", StringComparison.OrdinalIgnoreCase));
        if (idxModule < 0) return rows;

        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsvLine(line);
            if (idxModule >= cols.Count) continue;

            string module = cols[idxModule];
            string display = idxDisplay >= 0 && idxDisplay < cols.Count ? cols[idxDisplay] : string.Empty;
            DateTime? linkDate = idxLinkDate >= 0 && idxLinkDate < cols.Count &&
                                  DateTime.TryParse(cols[idxLinkDate], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;
            rows.Add(new DqRow(module, display, linkDate));
        }
        return rows;
    }

    private static async Task<List<PnpDriverEntry>> RunPnpUtilAsync()
    {
        var entries = new List<PnpDriverEntry>();
        var (ok, output) = await RunAsync("pnputil.exe", "/enum-drivers", TimeSpan.FromSeconds(25));
        if (!ok || string.IsNullOrWhiteSpace(output)) return entries;

        string published = "", original = "", provider = "", verDate = "", signer = "";
        void Flush()
        {
            if (original.Length > 0 || published.Length > 0)
            {
                string date = string.Empty, version = verDate;
                int sp = verDate.IndexOf(' ');
                if (sp > 0) { date = verDate[..sp]; version = verDate[(sp + 1)..]; }
                entries.Add(new PnpDriverEntry(published, original, provider, version, date, signer));
            }
            published = original = provider = verDate = signer = string.Empty;
        }

        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string keyPart = line[..colon].Trim();
            string valuePart = line[(colon + 1)..].Trim();

            if (keyPart.Equals("Published Name", StringComparison.OrdinalIgnoreCase)) { Flush(); published = valuePart; }
            else if (keyPart.Equals("Original Name", StringComparison.OrdinalIgnoreCase)) original = valuePart;
            else if (keyPart.Equals("Provider Name", StringComparison.OrdinalIgnoreCase)) provider = valuePart;
            else if (keyPart.Equals("Driver Version", StringComparison.OrdinalIgnoreCase)) verDate = valuePart;
            else if (keyPart.Equals("Signer Name", StringComparison.OrdinalIgnoreCase)) signer = valuePart;
        }
        Flush();
        return entries;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string exe, string args, TimeSpan timeout)
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
            if (proc is null) return (false, "couldn't start process");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            await proc.WaitForExitAsync(cts.Token);

            string combined = (await outTask) + (await errTask);
            return (proc.ExitCode == 0, combined.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
