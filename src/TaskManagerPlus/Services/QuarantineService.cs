using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #899(c): "move (never shred) files the user chooses to remove into a timestamped
/// quarantine folder." Quarantine always File.Move's (never File.Delete's) into
/// AppPaths.SettingsDirectory\Quarantine\&lt;yyyyMMdd-HHmmss&gt;\&lt;originalFileName&gt; - Restore
/// reverses that exact move. Deliberately separate from DefenderService's own quarantine browser
/// (#863, which reads/restores/purges Windows Defender's OWN quarantine store) - this is this
/// app's own, much simpler "get a suspicious file out of its running location without destroying
/// evidence" mechanism for files the Persistence grid flags, not a Defender feature.
/// </summary>
public static class QuarantineService
{
    public static (bool Success, string? QuarantinePath, string? Error) Quarantine(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return (false, null, "File not found - it may have already been moved or deleted.");

            string folder = AppPaths.GetPath("Quarantine", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);

            string destination = Path.Combine(folder, Path.GetFileName(filePath));
            // Extremely unlikely (same-second quarantine of two same-named files), but never
            // silently overwrite an existing quarantined file - append a numeric suffix instead.
            int suffix = 1;
            string finalDestination = destination;
            while (File.Exists(finalDestination))
            {
                finalDestination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(filePath)}_{suffix}{Path.GetExtension(filePath)}");
                suffix++;
            }

            File.Move(filePath, finalDestination);

            // Across volumes File.Move is copy-then-delete, and Windows reports success even when
            // the delete fails (a running or locked binary) - the original would still be in place.
            if (File.Exists(filePath))
            {
                try { File.Delete(finalDestination); } catch { /* best-effort - leave the copy rather than fail differently */ }
                return (false, null, "The file was copied but the original couldn't be removed (it may be running or locked), so nothing was quarantined.");
            }
            return (true, finalDestination, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Undo for a FileQuarantine journal entry - moves the file back to its original
    /// path. Fails cleanly (doesn't overwrite) if something new already exists at the original
    /// path.</summary>
    public static (bool Success, string? Error) Restore(string quarantinePath, string originalPath)
    {
        try
        {
            if (!File.Exists(quarantinePath)) return (false, "Quarantined file not found - it may have already been restored or removed.");
            if (File.Exists(originalPath)) return (false, "A file already exists at the original path - not overwriting it.");

            string? originalDir = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(originalDir)) Directory.CreateDirectory(originalDir);

            File.Move(quarantinePath, originalPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
