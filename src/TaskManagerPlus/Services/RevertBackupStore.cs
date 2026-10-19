using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>
/// The pre-change values a one-click Revert writes back to the system (WER LocalDumps, the
/// Reliability Analysis WMIEnable value, the Special Pool PoolTag). Revert runs elevated, so a copy
/// any same-user process could write would let it pick what gets written to HKLM. These files live
/// in the admin-only AdminDataPaths "Backups" area and carry a JournalIntegrityService MAC over
/// exactly the values the restore reads; anything else - a missing MAC, a wrong one, the area not
/// being available - reads as "no usable backup" and the caller disables its Revert with the reason.
///
/// Earlier versions kept these files in the settings folder. Those copies are never read back:
/// <see cref="LegacyNote"/> names the file so the user can restore by hand if it's theirs.
/// </summary>
internal static class RevertBackupStore
{
    private const string Area = "Backups";

    private sealed class Envelope<T>
    {
        public T? Payload { get; set; }
        public string? Mac { get; set; }
    }

    /// <summary>Null on success, otherwise why the backup couldn't be written (the caller then
    /// refuses the change - a change with no way back is worse than no change).</summary>
    public static string? Save<T>(string fileName, string domain, T payload, string?[] macFields)
    {
        string? dir = AdminDataPaths.TryGetArea(Area, out var error);
        if (dir is null) return error ?? "the protected backup folder isn't available";
        string? mac = JournalIntegrityService.Sign(domain, macFields);
        if (mac is null) return "the backup couldn't be signed (this app's integrity key isn't available)";
        try
        {
            AppPaths.WriteAllTextAtomic(Path.Combine(dir, fileName),
                JsonSerializer.Serialize(new Envelope<T> { Payload = payload, Mac = mac }, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The verified backup, or null. <paramref name="problem"/> is set when a file exists
    /// but can't be used, so the caller can say why Revert is unavailable.</summary>
    public static T? Load<T>(string fileName, string domain, Func<T, string?[]> macFields, out string? problem) where T : class
    {
        problem = null;
        try
        {
            // Checked before TryGetArea so a plain read never creates the admin folder.
            if (!File.Exists(Path.Combine(AdminDataPaths.RootPath, Area, fileName))) return null;
            string? dir = AdminDataPaths.TryGetArea(Area, out var error);
            if (dir is null)
            {
                problem = $"The saved backup can't be checked ({error}) - revert is disabled.";
                return null;
            }
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) return null;

            var envelope = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path));
            if (envelope?.Payload is not { } payload || !JournalIntegrityService.Verify(domain, envelope.Mac, macFields(payload)))
            {
                problem = $"Can't verify this app wrote the saved backup ({path}) - revert is disabled.";
                return null;
            }
            return payload;
        }
        catch
        {
            problem = "The saved backup couldn't be read - revert is disabled.";
            return null;
        }
    }

    public static void Delete(string fileName)
    {
        try
        {
            if (!File.Exists(Path.Combine(AdminDataPaths.RootPath, Area, fileName))) return;
            string? dir = AdminDataPaths.TryGetArea(Area, out _);
            if (dir is not null) File.Delete(Path.Combine(dir, fileName));
        }
        catch { /* best-effort - a leftover backup only keeps Revert offered */ }
    }

    /// <summary>A note naming a backup an earlier version left in the settings folder, or null.
    /// It's in a folder other programs running as this user can write, so it is never restored
    /// automatically; it isn't moved or deleted either.</summary>
    public static string? LegacyNote(string fileName)
    {
        try
        {
            string path = AppPaths.GetPath(fileName);
            return File.Exists(path)
                ? $"An older backup from a previous version is at {path}. It sits in a folder other programs can write to, so it isn't used for Revert - restore its values by hand if you need them."
                : null;
        }
        catch
        {
            return null;
        }
    }
}
