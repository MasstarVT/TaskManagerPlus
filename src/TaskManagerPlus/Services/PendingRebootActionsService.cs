using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #978: tracks which of this app's own remediation runs declared RequiresReboot, so the "restart
/// pending" banner can cross-check the app's own actions against SystemSpecsService's existing
/// RebootPending registry detection and read "restart pending — including from N fix(es) you ran"
/// instead of two disconnected reboot signals. Small JSON file (pending-reboot-actions.json under
/// AppPaths.SettingsDirectory), same fail-silently-to-empty convention as every other settings/log
/// file in this app.
///
/// Entries recorded before the current boot are dropped (<see cref="LoadCurrentBoot"/>) - a
/// restart is exactly what they were waiting for. This deliberately doesn't key on
/// SystemSpecsViewModel.RebootPending going false: that flag is only refreshed on demand (and is
/// still its default false at startup), so clearing on it deleted every entry seconds after it
/// was written.
/// </summary>
public static class PendingRebootActionsService
{
    private static string FilePath => AppPaths.GetPath("pending-reboot-actions.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static List<PendingRebootAction> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<PendingRebootAction>();
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<PendingRebootAction>>(json, JsonOpts);
            return list ?? new List<PendingRebootAction>();
        }
        catch
        {
            return new List<PendingRebootAction>();
        }
    }

    public static void Add(string actionTitle)
    {
        try
        {
            var all = LoadAll();
            all.Add(new PendingRebootAction { ActionTitle = actionTitle });
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
        }
        catch
        {
            // Best-effort - the action itself already ran; a failed tracking-file write shouldn't
            // surface as if the action failed.
        }
    }

    /// <summary>Entries recorded during the current boot session; older ones are pruned from the
    /// file (rewritten only when something was actually dropped).</summary>
    public static List<PendingRebootAction> LoadCurrentBoot()
    {
        var all = LoadAll();
        if (all.Count == 0) return all;

        DateTime bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var current = all.Where(a => a.RanAtUtc >= bootUtc).ToList();
        if (current.Count != all.Count)
        {
            try
            {
                if (current.Count == 0) File.Delete(FilePath);
                else File.WriteAllText(FilePath, JsonSerializer.Serialize(current, JsonOpts));
            }
            catch
            {
                // Best-effort - the filtered list is still what gets shown.
            }
        }
        return current;
    }

    public static void ClearAll()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // Best-effort.
        }
    }
}
