using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #935: append-only feedback.jsonl under AppPaths.SettingsDirectory - one JSON object per line,
/// written when the user clicks "Not a problem" on a Health Check finding (SummaryViewModel).
/// Purely local: nothing in this app reads this file back over a network or uploads it anywhere -
/// it exists only so a "that's not actually an issue" reaction is recorded somewhere on the user's
/// own machine, not lost the moment the finding clears. Best-effort like every other settings/log
/// file in this app.
/// </summary>
public static class FeedbackService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    private static string LogPath => AppPaths.GetPath("feedback.jsonl");

    public static void Append(FeedbackEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
        }
        catch
        {
            // Best-effort - a failed write shouldn't crash the app; the click still un-shows the
            // finding via the follow-up suppress offer regardless.
        }
    }
}
