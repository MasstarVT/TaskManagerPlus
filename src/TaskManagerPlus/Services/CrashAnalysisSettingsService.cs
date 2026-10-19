using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, item 25: loads/saves CrashAnalysisSettings to
/// %AppData%\TaskManagerPlus\crash-analysis.json (via AppPaths, so portable mode redirects it
/// too) - same shape as PollIntervalSettingsService.</summary>
public static class CrashAnalysisSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("crash-analysis.json");

    public static CrashAnalysisSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<CrashAnalysisSettings>(json);
                if (settings is not null)
                {
                    // The folder becomes part of the elevated cdb's symbol path, so anything but one
                    // plain local folder (blank, UNC, a second srv* leg, ...) falls back to the default.
                    // A removable drive that simply isn't plugged in keeps its setting; every use
                    // re-validates strictly and falls back to the default while it's absent.
                    if (ValidateSymbolCacheFolder(settings.SymbolCacheFolder, allowAbsentDrive: true) is not null)
                    {
                        AppPaths.MarkUnreadable(SettingsPath);
                        settings.SymbolCacheFolder = CrashAnalysisSettings.Defaults.SymbolCacheFolder;
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return CrashAnalysisSettings.Defaults;
    }

    public static void Save(CrashAnalysisSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            AppPaths.WriteAllTextAtomic(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }

    /// <summary>Null when <paramref name="folder"/> is one fully qualified folder on a local drive
    /// (e.g. C:\Symbols); otherwise why not. It is spliced into "srv*&lt;folder&gt;*https://..."
    /// for an elevated debugger, so a '*', ';' or quote could add another symbol-server leg or
    /// argument, and a UNC or network-drive path would read symbols from someone else's share.
    /// <paramref name="allowAbsentDrive"/> accepts a drive letter with no volume behind it (only
    /// for keeping a saved setting - never for a path about to be used).</summary>
    public static string? ValidateSymbolCacheFolder(string? folder, bool allowAbsentDrive = false)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Enter a folder for the symbol cache.";
        string f = folder.Trim();
        if (f.Length < 3 || !char.IsAsciiLetter(f[0]) || f[1] != ':' || f[2] != '\\')
            return "Use a full local folder path such as C:\\Symbols (not a network or relative path).";
        if (f.IndexOfAny(new[] { '*', ';', '"', '%', '?', '<', '>', '|' }) >= 0 || f.Any(char.IsControl)
            || f.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || f.IndexOf(':', 2) >= 0)
            return "The symbol cache folder can't contain * ; \" % ? < > | or a second ':'.";
        if (f.Split('\\').Any(segment => segment == ".."))
            return "The symbol cache folder can't contain '..'.";
        try
        {
            var type = new DriveInfo(f[..1]).DriveType;
            if (allowAbsentDrive && type == DriveType.NoRootDirectory) return null;
            if (type is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                return $"{f[..2]} isn't a local drive.";
        }
        catch
        {
            return $"{f[..2]} isn't a local drive.";
        }
        return null;
    }
}
