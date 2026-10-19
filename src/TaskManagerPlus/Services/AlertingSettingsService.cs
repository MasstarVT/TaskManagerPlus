using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#964: load/save for alerting.json (quiet hours + the per-rule alert-channel override
/// map) - fails silently to AlertingSettings' defaults on a missing/corrupt file.</summary>
public static class AlertingSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SettingsPath => AppPaths.GetPath("alerting.json");

    public static AlertingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AlertingSettings>(json, JsonOpts);
                if (loaded is not null) return Sanitize(loaded);
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // corrupt/unreadable - fall back to defaults (quiet hours off, no overrides), keeping a
            // copy of the user's file for the next Save
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return new AlertingSettings();
    }

    /// <summary>System.Text.Json hands back a fresh, case-sensitive dictionary (or null), and the
    /// enum converter accepts any integer. Rebuilt case-insensitively so a later override for the
    /// same rule replaces the old entry instead of sitting beside it; blank ids and undefined
    /// channels are dropped. Quiet-hour bounds outside one day fall back to the defaults.</summary>
    private static AlertingSettings Sanitize(AlertingSettings settings)
    {
        var defaults = new AlertingSettings();
        var overrides = new Dictionary<string, AlertChannel>(StringComparer.OrdinalIgnoreCase);
        int raw = settings.RuleChannelOverrides?.Count ?? 0;
        foreach (var (ruleId, channel) in settings.RuleChannelOverrides ?? new Dictionary<string, AlertChannel>())
        {
            if (!string.IsNullOrWhiteSpace(ruleId) && Enum.IsDefined(channel)) overrides[ruleId] = channel;
        }
        if (overrides.Count != raw) AppPaths.MarkUnreadable(SettingsPath);
        settings.RuleChannelOverrides = overrides;

        static bool InOneDay(TimeSpan t) => t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
        if (!InOneDay(settings.QuietHoursStart)) settings.QuietHoursStart = defaults.QuietHoursStart;
        if (!InOneDay(settings.QuietHoursEnd)) settings.QuietHoursEnd = defaults.QuietHoursEnd;
        return settings;
    }

    public static void Save(AlertingSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { /* best-effort */ }
    }
}
