using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #796: a small, generic "what did this app itself change in the registry, and can I undo it"
/// journal - append-only history (registry-changes.json under AppPaths.SettingsDirectory, same
/// load/append/save shape as SfcIntegrityService's integrity-history.json), per-entry Undo (writes
/// the captured old value straight back, or deletes the value if it didn't exist before this app
/// created it), and per-entry export as a standalone .reg file.
///
/// Scope note (read this before assuming every registry write in the app is covered): this app
/// has accumulated a great many registry-writing actions across its ~800-item backlog
/// (StartupApproved flags, Fast Startup, Prefetch, service start types, update-pause values,
/// DeviceGuard policy, ...). Retrofitting every single one to route through this journal is out of
/// scope for the chunk that introduced it - CLAUDE.md's own "quick flag/on-demand, not exhaustive"
/// tradeoff applies to the journal's own coverage too. What IS wired in as of this chunk:
///   - StartupManagerService.SetEnabled (the StartupApproved enable/disable binary flag)
///   - FastStartupService.DisableFastStartup (HiberbootEnabled)
///   - PrefetchAuditService.RestoreDefaults (EnablePrefetcher/EnableSuperfetch/SysMain Start)
///   - RegistryHealthService's own #795 EnablePeriodicBackup toggle
/// Every other registry-writing service in this app still writes directly, same as before this
/// chunk - a genuinely representative sample rather than blanket coverage. See this chunk's final
/// report for the exact list.
/// </summary>
public static class RegistryChangeJournalService
{
    private static readonly object FileLock = new();
    private static string FilePath => AppPaths.GetPath("registry-changes.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<RegistryChangeEntry> LoadHistory()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<RegistryChangeEntry>();
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<RegistryChangeEntry>>(json) ?? new List<RegistryChangeEntry>();
            }
            catch
            {
                // Missing/corrupt file - degrade to empty history, same as every other settings
                // file in this app (CLAUDE.md's settings-persistence convention).
                return new List<RegistryChangeEntry>();
            }
        }
    }

    /// <summary>Appends one entry and returns the full, now-current history - callers should
    /// re-render their in-memory list from the return value rather than assuming their own append
    /// was the only writer this session (same pattern SfcIntegrityService.AppendAndSave uses).</summary>
    public static List<RegistryChangeEntry> Append(RegistryChangeEntry entry)
    {
        lock (FileLock)
        {
            var history = LoadHistoryUnlocked();
            history.Add(entry);
            SaveUnlocked(history);
            return history;
        }
    }

    /// <summary>Convenience for callers that already read the old value themselves - builds the
    /// entry and appends it in one call, swallowing any journal-write failure (a failed journal
    /// write should never make the underlying registry write itself look like it failed - the
    /// actual write already happened by the time this runs).</summary>
    public static void Record(string source, string description, string hive, string subKeyPath,
        string valueName, RegistryValueKind kind, string? oldValueText, string? newValueText)
    {
        try
        {
            Append(new RegistryChangeEntry
            {
                Timestamp = DateTime.Now,
                Source = source,
                Description = description,
                Hive = hive,
                SubKeyPath = subKeyPath,
                ValueName = valueName,
                ValueKind = kind.ToString(),
                OldValueText = oldValueText,
                NewValueText = newValueText,
            });
        }
        catch
        {
            // Best-effort - the registry write this journals already succeeded or failed on its
            // own terms; a journal I/O problem shouldn't be surfaced as a failure of that write.
        }
    }

    /// <summary>Writes OldValueText back (or deletes the value entirely when OldValueText is null,
    /// i.e. this app created a value that didn't exist before), then marks the entry Undone in the
    /// journal file. Needs the same elevation this whole app already runs with for an HKLM write.</summary>
    public static (bool Success, string? Error) Undo(RegistryChangeEntry entry)
    {
        try
        {
            RegistryKey baseHive = HiveFromName(entry.Hive);
            using var key = baseHive.CreateSubKey(entry.SubKeyPath, writable: true);
            if (key is null) return (false, $"Couldn't open {entry.FullKeyText} (access denied or the key no longer exists).");

            if (entry.OldValueText is null)
            {
                key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            }
            else
            {
                if (!Enum.TryParse<RegistryValueKind>(entry.ValueKind, out var kind))
                    return (false, $"Unrecognized value kind \"{entry.ValueKind}\" - can't safely undo.");

                object value = kind switch
                {
                    RegistryValueKind.DWord => int.Parse(entry.OldValueText, CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord => long.Parse(entry.OldValueText, CultureInfo.InvariantCulture),
                    RegistryValueKind.Binary => Convert.FromHexString(entry.OldValueText),
                    _ => entry.OldValueText,
                };
                key.SetValue(entry.ValueName, value, kind);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        MarkUndone(entry.Id);
        return (true, null);
    }

    private static void MarkUndone(Guid id)
    {
        lock (FileLock)
        {
            var history = LoadHistoryUnlocked();
            var match = history.FirstOrDefault(e => e.Id == id);
            if (match is null) return;
            match.Undone = true;
            SaveUnlocked(history);
        }
    }

    /// <summary>#796: exports one entry's NEW value as a standalone, double-clickable .reg file -
    /// reapplying the change elsewhere, or as a record of exactly what this app wrote. Uses the
    /// same value-kind encodings regedit itself writes (dword:, hex: for Binary, hex(2): for
    /// ExpandString as null-terminated UTF-16LE, plain quoted text for String).</summary>
    public static string BuildRegFileContent(RegistryChangeEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        string hiveLongName = entry.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
        sb.AppendLine($"[{hiveLongName}\\{entry.SubKeyPath}]");

        string valueLine = entry.NewValueText is null
            ? $"\"{EscapeRegName(entry.ValueName)}\"=-" // NewValueText null means "this write deleted the value"
            : $"\"{EscapeRegName(entry.ValueName)}\"={EncodeRegValue(entry.ValueKind, entry.NewValueText)}";
        sb.AppendLine(valueLine);
        return sb.ToString();
    }

    private static string EncodeRegValue(string kindText, string valueText)
    {
        if (!Enum.TryParse<RegistryValueKind>(kindText, out var kind))
            return $"\"{EscapeRegString(valueText)}\"";

        switch (kind)
        {
            case RegistryValueKind.DWord:
                return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dw)
                    ? $"dword:{(uint)dw:x8}" : $"\"{EscapeRegString(valueText)}\"";
            case RegistryValueKind.QWord:
                return long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long qw)
                    ? $"hex(b):{string.Join(",", BitConverter.GetBytes(qw).Select(b => b.ToString("x2")))}" : $"\"{EscapeRegString(valueText)}\"";
            case RegistryValueKind.Binary:
                try
                {
                    var bytes = Convert.FromHexString(valueText);
                    return $"hex:{string.Join(",", bytes.Select(b => b.ToString("x2")))}";
                }
                catch { return $"\"{EscapeRegString(valueText)}\""; }
            case RegistryValueKind.ExpandString:
                var expandBytes = Encoding.Unicode.GetBytes(valueText + "\0");
                return $"hex(2):{string.Join(",", expandBytes.Select(b => b.ToString("x2")))}";
            default:
                return $"\"{EscapeRegString(valueText)}\"";
        }
    }

    private static string EscapeRegString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeRegName(string s) => EscapeRegString(s);

    private static RegistryKey HiveFromName(string hive) =>
        hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;

    private static List<RegistryChangeEntry> LoadHistoryUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<RegistryChangeEntry>();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<RegistryChangeEntry>>(json) ?? new List<RegistryChangeEntry>();
        }
        catch
        {
            return new List<RegistryChangeEntry>();
        }
    }

    private static void SaveUnlocked(List<RegistryChangeEntry> history)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            // #1026: write-to-temp-then-rename instead of truncating the real file in place - a
            // crash/power loss mid-write would otherwise leave partial JSON, which LoadHistoryUnlocked
            // silently degrades to an empty history, permanently destroying the whole undo record.
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(history, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence, same as every other settings file in this app.
        }
    }
}
