using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, item 86: loads/saves VerifierEnableHistory to
/// %AppData%\TaskManagerPlus\verifier-enable-history.json (via AppPaths, so portable mode
/// redirects it too) - same shape/failure tolerance as HangHistoryService/hang-history.json. The
/// guided Verifier wizard (items 83/84) calls RecordEnabled once it successfully turns Verifier on;
/// StabilityViewModel reads it back on every refresh to compute "Verifier has been enabled for N
/// days" (and to decide whether the nag banner/Summary health-check entry should fire). A
/// successful /reset (item 82) or an explicit disable clears it back to null via ClearEnabled, so a
/// Verifier session that's since been turned off doesn't keep counting.
/// </summary>
public static class VerifierEnableHistoryService
{
    private static string SettingsPath => AppPaths.GetPath("verifier-enable-history.json");

    public static VerifierEnableHistory Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<VerifierEnableHistory>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return VerifierEnableHistory.Defaults;
    }

    public static void Save(VerifierEnableHistory settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if this can't persist, "enabled for N days" just won't survive an app
            // restart; nothing else in the app depends on this succeeding.
        }
    }

    /// <summary>Records "Verifier was turned on right now" - always overwrites EnabledAtUtc with
    /// the current time (re-running the wizard resets the clock, which is correct: a fresh
    /// standard/volatile session starting now is what's actually running, not whatever an older
    /// session left behind) and resets the reboot counter to start counting fresh from this boot.</summary>
    public static void RecordEnabled()
    {
        var settings = Load();
        settings.EnabledAtUtc = DateTime.UtcNow;
        settings.RebootsSinceEnabled = 0;
        settings.LastSeenBootUtc = ApproximateBootTimeUtc();
        Save(settings);
    }

    public static void ClearEnabled()
    {
        var settings = Load();
        if (settings.EnabledAtUtc is null) return;
        settings.EnabledAtUtc = null;
        settings.RebootsSinceEnabled = 0;
        settings.LastSeenBootUtc = null;
        Save(settings);
    }

    public static void SetNagAfterDays(int days)
    {
        var settings = Load();
        settings.NagAfterDays = Math.Max(1, days);
        Save(settings);
    }

    public static void SetNagAfterReboots(int reboots)
    {
        var settings = Load();
        settings.NagAfterReboots = Math.Max(1, reboots);
        Save(settings);
    }

    /// <summary>Item 86's "or reboots" half: compares the machine's current approximate boot time
    /// against the last one this history recorded, and bumps RebootsSinceEnabled when they differ
    /// by more than a couple of minutes (a wide tolerance so ordinary clock/tick-count jitter never
    /// registers as a false reboot). No-op when EnabledAtUtc is null - nothing to count reboots
    /// against. Call this once per refresh while Verifier is confirmed running, same cadence as
    /// RefreshVerifierStatusAsync itself.</summary>
    public static void RecordBootObservationIfChanged()
    {
        var settings = Load();
        if (settings.EnabledAtUtc is null) return;

        var currentBootUtc = ApproximateBootTimeUtc();
        if (settings.LastSeenBootUtc is not { } lastSeen)
        {
            settings.LastSeenBootUtc = currentBootUtc;
            Save(settings);
            return;
        }

        if ((currentBootUtc - lastSeen).Duration() > TimeSpan.FromMinutes(2))
        {
            settings.RebootsSinceEnabled++;
            settings.LastSeenBootUtc = currentBootUtc;
            Save(settings);
        }
    }

    /// <summary>Environment.TickCount64 (milliseconds since this boot) needs no WMI/registry call
    /// and is accurate enough for "did a reboot happen since we last checked" - a few seconds of
    /// drift either way is irrelevant against the 2-minute tolerance above.</summary>
    private static DateTime ApproximateBootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
