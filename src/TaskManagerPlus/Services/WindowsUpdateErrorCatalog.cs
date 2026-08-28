using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #772: shared offline lookup table for common Windows Update/servicing HRESULTs, with a
/// plain-language cause and fix - backs the error column everywhere on the Windows Health tab
/// (#769's WU-client history, #771's Setup-channel correlation, #773's feature-update failure
/// analysis). The bundled table only covers the handful of codes that account for most real-world
/// update failures (component store corruption, missing source files, disk/ESP space, WSUS/proxy
/// unreachable, driver/compatibility upgrade blocks, ...) - anything not in the table falls back to
/// `certutil -error &lt;code&gt;`, a documented, built-in Windows tool with a much larger HRESULT
/// table, the same "known tool over reimplementing" tradeoff every other shelled-out command in
/// this app takes. Never throws and never fabricates a cause: an unrecognized code that certutil
/// also doesn't know about just comes back as the bare code.
/// </summary>
public static class WindowsUpdateErrorCatalog
{
    private static readonly Dictionary<string, (string Cause, string Fix)> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x80073712"] = ("Component store corruption.",
            "Run `DISM /Online /Cleanup-Image /RestoreHealth`, then `sfc /scannow`."),
        ["0x800F081F"] = ("Source files not found.",
            "Supply an update source with `/Source:<path>` (e.g. a mounted Windows install ISO)."),
        ["0x800F0922"] = ("Not enough space on the boot/EFI system partition, or a rollback failure during a feature update.",
            "Free up space on the ESP, or check the rollback details under C:\\$WINDOWS.~BT\\Sources\\Panther."),
        ["0x80070643"] = ("A Windows Recovery Environment (WinRE) issue is blocking the update - the recovery-partition case.",
            "Check WinRE status (`reagentc /info`) and the recovery partition's free space."),
        ["0x80070070"] = ("Insufficient disk space to complete the update.",
            "Free up disk space, then retry the update."),
        ["0x8024402C"] = ("Windows Update couldn't reach the update service - a WSUS server or proxy is unreachable.",
            "Check WSUS/proxy connectivity and firewall rules."),
        ["0x80244022"] = ("The update server (WSUS) is too busy, or a proxy is unreachable.",
            "Retry later, or check WSUS/proxy connectivity."),
        ["0xC1900101"] = ("A driver blocked the feature update / in-place upgrade.",
            "Update or temporarily remove the blocking driver (check setupact.log for its name), then retry."),
        ["0xC1900208"] = ("A compatibility block prevented the feature update from proceeding.",
            "Check the compatibility report for the blocking app/device, then retry."),
    };

    /// <summary>Synchronous fast-path over the bundled table only (no shell-out) - null when the
    /// code isn't in the table, so a caller can decide whether the async certutil fallback is worth
    /// it. Safe to call from anywhere, including the UI thread.</summary>
    public static string? DescribeKnown(string? hresultHex)
    {
        if (string.IsNullOrWhiteSpace(hresultHex)) return null;
        string normalized = Normalize(hresultHex);
        return KnownCodes.TryGetValue(normalized, out var known) ? $"{known.Cause} Fix: {known.Fix}" : null;
    }

    /// <summary>Full lookup: the bundled table first, then `certutil -error &lt;code&gt;` for
    /// anything not covered. Always returns a non-null, non-throwing string - worst case, the bare
    /// normalized code with no further explanation.</summary>
    public static async Task<string> DescribeAsync(string? hresultHex)
    {
        if (string.IsNullOrWhiteSpace(hresultHex)) return "Unknown error";
        string normalized = Normalize(hresultHex);

        if (KnownCodes.TryGetValue(normalized, out var known))
            return $"{normalized} - {known.Cause} Fix: {known.Fix}";

        string? viaCertutil = await TryCertutilAsync(normalized);
        return viaCertutil is not null ? $"{normalized} - {viaCertutil}" : $"{normalized} (not in the bundled catalog; certutil had no match either)";
    }

    private static string Normalize(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        hex = hex.TrimStart('0');
        if (hex.Length == 0) hex = "0";
        return "0x" + hex.PadLeft(8, '0').ToUpperInvariant();
    }

    /// <summary>`certutil -error 0xNNNNNNNN` prints the symbolic name and a short description on
    /// its first output line (e.g. "CBS_E_STORE_CORRUPTION -- component store is corrupted") -
    /// certutil ships with every Windows install, so no extra dependency, matching this app's
    /// "prefer a known Windows tool" convention. Bounded by a short timeout and killed if it hangs,
    /// same as every other shelled-out command in this app.</summary>
    private static async Task<string?> TryCertutilAsync(string normalizedHex)
    {
        try
        {
            var psi = new ProcessStartInfo("certutil.exe", $"-error {normalizedHex}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(5000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return null;
            }

            string output = ((await outputTask) + (await errorTask)).Trim();
            if (output.Length == 0 || proc.ExitCode != 0) return null;

            // First non-empty line is the useful one - later lines are usually "CertUtil: -error
            // command completed successfully." noise.
            string firstLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return firstLine.Length > 0 ? firstLine : null;
        }
        catch
        {
            return null;
        }
    }
}
