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
/// The list is cleared the moment SystemSpecsViewModel next observes RebootPending go false (see
/// SummaryViewModel.RefreshHealthIssues) - once Windows' own pending-reboot flag clears (the user
/// actually restarted, or whatever set it resolved another way), whatever this app recorded is
/// stale either way, so there's nothing honest left to keep.
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
