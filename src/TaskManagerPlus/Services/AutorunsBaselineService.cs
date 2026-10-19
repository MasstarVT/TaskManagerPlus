using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 13, #803: "Save baseline" writes the full persistence enumeration to
/// autoruns-baseline.json under AppPaths.SettingsDirectory; "Compare to baseline" then reports
/// Added/Removed/Changed rows since the machine was last known-good. Diffing a known-good snapshot
/// is far more informative than staring at a couple hundred legitimate entries every scan, so this
/// is the single highest-value thing the Security tab's Persistence section offers.
///
/// The settings folder is writable by anything running as the user, so a planted persistence
/// entry could simply be appended to the baseline to vanish from the diff. Each save therefore
/// writes a sidecar MAC (JournalIntegrityService, admin-only key) over the file's SHA-256, and
/// Diff refuses - with the reason - to compare against a baseline that is unsigned, changed, or
/// can't be verified, rather than diffing against it. Residual: this proves the app wrote the
/// file, not that it's the newest one - an older baseline this app saved can be put back.
/// </summary>
public static class AutorunsBaselineService
{
    private const string BaselineFileName = "autoruns-baseline.json";
    private const string MacDomain = "autoruns-baseline";
    private static string BaselinePath => AppPaths.GetPath(BaselineFileName);
    private static string MacPath => BaselinePath + ".mac";

    public static bool HasBaseline()
    {
        try
        {
            return File.Exists(BaselinePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Saves <paramref name="entries"/> as the new baseline. Signed is false when no
    /// trusted signing key is available (not elevated, or read-only mode before the key exists) -
    /// the baseline is still saved, but Diff will refuse it.</summary>
    public static (bool Saved, bool Signed, string? Error) SaveBaseline(IEnumerable<AutorunEntry> entries)
    {
        try
        {
            string path = BaselinePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!AppPaths.PreserveUnreadable(path))
                return (false, false, "the existing baseline couldn't be backed up before replacing it, so it was left as it is");

            var json = JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true });
            // Sign the exact bytes being written (WriteAllTextAtomic writes UTF-8 without a BOM),
            // never a re-read another process could have swapped in between.
            string hash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(json)));
            string? mac = JournalIntegrityService.Sign(MacDomain, BaselineFileName, hash);

            // The old MAC goes first: a crash part-way leaves an unsigned baseline, never a MAC
            // beside data it doesn't cover.
            if (File.Exists(MacPath)) File.Delete(MacPath);
            AppPaths.WriteAllTextAtomic(path, json);
            if (mac is not null) AppPaths.WriteAllTextAtomic(MacPath, mac);
            return (true, mac is not null, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    /// <summary>Compares <paramref name="current"/> against the last saved baseline, keyed on
    /// (Category, Location, Name) - the combination that identifies "the same persistence slot"
    /// across two scans, regardless of what command/DLL it currently points at. HasBaseline is
    /// false, with <paramref name="notComparedReason"/> saying why, when there is no baseline or it
    /// can't be read or verified.</summary>
    public static AutorunsDiffResult Diff(IEnumerable<AutorunEntry> current, out string? notComparedReason)
    {
        var result = new AutorunsDiffResult { HasBaseline = false };
        string path = BaselinePath;

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                notComparedReason = "No baseline saved yet - use \"Save baseline\" first.";
                return result;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            notComparedReason = $"The saved baseline couldn't be read ({ex.Message}) - save a new one.";
            return result;
        }

        // Verified and parsed from the same bytes, so nothing can change between the two.
        string? mac = null;
        try { if (File.Exists(MacPath)) mac = File.ReadAllText(MacPath).Trim(); }
        catch { /* unreadable sidecar = unsigned */ }
        if (string.IsNullOrEmpty(mac))
        {
            AppPaths.MarkUnreadable(path);
            notComparedReason = "The saved baseline isn't signed by this app (an older version saved it, or this app couldn't sign it at the time), " +
                "and any program running as you can edit that file - so it isn't compared. Save a new baseline from a scan you trust.";
            return result;
        }
        if (!JournalIntegrityService.Verify(MacDomain, mac, BaselineFileName, Convert.ToHexString(SHA256.HashData(bytes))))
        {
            AppPaths.MarkUnreadable(path);
            notComparedReason = "The saved baseline changed since this app saved it, or can't be verified right now (this needs the app running elevated), " +
                "so it isn't compared - an edited baseline could hide a new entry. Save a new baseline from a scan you trust.";
            return result;
        }

        List<AutorunEntry>? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<List<AutorunEntry>>(bytes);
        }
        catch
        {
            baseline = null;
        }
        if (baseline is null)
        {
            AppPaths.MarkUnreadable(path);
            notComparedReason = "The saved baseline couldn't be parsed - save a new one.";
            return result;
        }

        notComparedReason = null;
        result.HasBaseline = true;

        var baselineByKey = new Dictionary<string, AutorunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in baseline)
        {
            if (e is null) continue; // a null row in otherwise-valid JSON
            baselineByKey[KeyOf(e)] = e; // last write wins on a duplicate key
        }

        var currentByKey = new Dictionary<string, AutorunEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in current) currentByKey[KeyOf(e)] = e;

        foreach (var (key, entry) in currentByKey)
        {
            if (!baselineByKey.TryGetValue(key, out var baseEntry))
            {
                result.Added.Add(entry);
            }
            else if (!string.Equals(baseEntry.RawCommand, entry.RawCommand, StringComparison.Ordinal)
                     || !string.Equals(baseEntry.ResolvedPath, entry.ResolvedPath, StringComparison.Ordinal)
                     || baseEntry.Enabled != entry.Enabled)
            {
                result.Changed.Add(entry);
            }
        }

        foreach (var (key, entry) in baselineByKey)
        {
            if (!currentByKey.ContainsKey(key))
                result.Removed.Add(entry);
        }

        return result;
    }

    private static string KeyOf(AutorunEntry e)
    {
        // Active Setup's re-run suffix is state, not identity (and older baselines saved it).
        string name = e.Name ?? string.Empty;
        if (name.EndsWith(AutorunsService.ActiveSetupRerunSuffix, StringComparison.Ordinal))
            name = name[..^AutorunsService.ActiveSetupRerunSuffix.Length];
        return $"{e.Category}␟{e.Location}␟{name}";
    }
}
