using System.Diagnostics;
using System.IO;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #900: "Build evidence bundle" - collects what the user has ALREADY LOADED this
/// session (the redacted security posture report, an autoruns baseline diff if one was run, any
/// hashes computed this session, and whatever event-log timelines were already loaded) into one
/// timestamped folder under AppPaths.SettingsDirectory\EvidenceBundles\&lt;yyyyMMdd-HHmmss&gt;\,
/// writes a README.txt naming what was included vs. skipped because it hadn't been run yet, then
/// opens the folder in Explorer. This app never re-triggers an expensive scan just to build the
/// bundle, and never uploads/sends the result anywhere itself - the whole point is a folder the
/// user can attach to a forum post/ticket themselves.
/// </summary>
public static class EvidenceBundleService
{
    public sealed record Section(string FileName, string Title, string? Content);

    public static (bool Success, string? FolderPath, string? Error) BuildBundle(IReadOnlyList<Section> sections)
    {
        try
        {
            string folder = AppPaths.GetPath("EvidenceBundles", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);

            var readme = new StringBuilder();
            readme.AppendLine("Task Manager Plus - Security evidence bundle");
            readme.AppendLine($"Built {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            readme.AppendLine();
            readme.AppendLine("This app never uploads or sends this folder anywhere on its own - it's yours to");
            readme.AppendLine("attach to a forum post/support ticket if and when you choose to.");
            readme.AppendLine();
            readme.AppendLine("Sections in this bundle - only what had already been run/loaded this session is");
            readme.AppendLine("included; nothing here triggered a fresh scan just to build the bundle:");
            readme.AppendLine();

            foreach (var section in sections)
            {
                if (section.Content is null)
                {
                    readme.AppendLine($"  [SKIPPED] {section.Title} - not run this session, so nothing to include.");
                    continue;
                }

                readme.AppendLine($"  [INCLUDED] {section.Title} -> {section.FileName}");
                File.WriteAllText(Path.Combine(folder, section.FileName), section.Content);
            }

            File.WriteAllText(Path.Combine(folder, "README.txt"), readme.ToString());

            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
            catch { /* best-effort - the bundle itself is already built either way */ }

            return (true, folder, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
