using System.ServiceProcess;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #896: cross-references #895's bloatware inventory against currently installed
/// services/scheduled tasks/startup entries by a simple name/publisher substring heuristic, so
/// each match can be Disabled through the SAME existing control method its Kind already uses
/// elsewhere in this app (ServiceControlService.SetStartupType / ScheduledTaskService.SetEnabledAsync
/// / StartupManagerService.SetEnabled) - this class only MATCHES and describes candidates, it never
/// itself toggles anything (that stays in SecurityViewModel, calling straight into those existing
/// methods, exactly the way ServicesViewModel/StartupViewModel already do for their own buttons).
///
/// DiagTrack and the Customer Experience Improvement Program scheduled tasks are surfaced
/// unconditionally (not only when a substring match happens to fire) since they're well-known
/// telemetry surfaces worth calling out by name regardless of whether they show up in the
/// Uninstall/AppX inventory at all - they're OS components, not "installed software."
/// </summary>
public static class OemCleanupService
{
    // Generic words that would match almost anything - excluded from the substring-matching
    // token set so e.g. "software" or "service" doesn't turn every row into a false match.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "inc", "incorporated", "corp", "corporation", "ltd", "co", "llc",
        "app", "application", "software", "desktop", "service", "services", "program", "system",
        "systems", "technologies", "technology", "solutions", "group", "company",
    };

    public static async Task<List<OemCleanupCandidate>> ScanAsync(IReadOnlyList<BloatwareEntry> bloatware)
    {
        var candidates = new List<OemCleanupCandidate>();

        var tokenSources = bloatware
            .Where(b => b.Tier is BloatwareTier.OemUtility or BloatwareTier.OemUpdaterTelemetry or BloatwareTier.Trialware or BloatwareTier.StoreBloat)
            .Select(b => (b.Name, Tokens: ExtractTokens(b)))
            .Where(t => t.Tokens.Count > 0)
            .ToList();

        // Services.
        try
        {
            foreach (var sc in ServiceController.GetServices())
            {
                try
                {
                    string haystack = $"{sc.ServiceName} {sc.DisplayName}";
                    var match = FindMatch(tokenSources, haystack);
                    if (match is not null)
                    {
                        candidates.Add(new OemCleanupCandidate
                        {
                            SourceName = match,
                            Kind = OemCleanupKind.Service,
                            TargetName = sc.ServiceName,
                            TargetDetail = sc.DisplayName,
                            IsCurrentlyEnabled = SafeStartType(sc) != ServiceStartMode.Disabled,
                        });
                    }
                    else if (sc.ServiceName.Equals("DiagTrack", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(new OemCleanupCandidate
                        {
                            SourceName = "Connected User Experiences and Telemetry (DiagTrack)",
                            Kind = OemCleanupKind.Service,
                            TargetName = sc.ServiceName,
                            TargetDetail = sc.DisplayName,
                            IsCurrentlyEnabled = SafeStartType(sc) != ServiceStartMode.Disabled,
                            IsTelemetrySpecialCase = true,
                        });
                    }
                }
                catch { /* one bad service shouldn't stop the rest */ }
                finally { sc.Dispose(); }
            }
        }
        catch { /* ServiceController.GetServices() unavailable - contribute nothing */ }

        // Scheduled tasks.
        try
        {
            var tasks = await ScheduledTaskService.ListAsync();
            foreach (var task in tasks)
            {
                string haystack = $"{task.Name} {task.TaskToRun} {task.Author}";
                var match = FindMatch(tokenSources, haystack);
                bool isCeip = task.Name.Contains(@"Customer Experience Improvement Program", StringComparison.OrdinalIgnoreCase);
                if (match is not null || isCeip)
                {
                    candidates.Add(new OemCleanupCandidate
                    {
                        SourceName = match ?? "Customer Experience Improvement Program",
                        Kind = OemCleanupKind.ScheduledTask,
                        TargetName = task.Name,
                        TargetDetail = task.TaskToRun,
                        IsCurrentlyEnabled = task.IsEnabled,
                        IsTelemetrySpecialCase = isCeip,
                    });
                }
            }
        }
        catch { /* schtasks unavailable - contribute nothing */ }

        // Startup entries (registry Run / Startup folders).
        try
        {
            var items = new StartupManagerService().Sample();
            foreach (var item in items)
            {
                string haystack = $"{item.Name} {item.Command}";
                var match = FindMatch(tokenSources, haystack);
                if (match is not null)
                {
                    candidates.Add(new OemCleanupCandidate
                    {
                        SourceName = match,
                        Kind = OemCleanupKind.StartupItem,
                        TargetName = item.Name,
                        TargetDetail = item.Command,
                        IsCurrentlyEnabled = item.IsEnabled,
                        StartupItemRef = item,
                    });
                }
            }
        }
        catch { /* registry read unavailable - contribute nothing */ }

        return candidates
            .OrderByDescending(c => c.IsTelemetrySpecialCase)
            .ThenBy(c => c.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ServiceStartMode SafeStartType(ServiceController sc)
    {
        try { return sc.StartType; } catch { return ServiceStartMode.Manual; }
    }

    private static List<string> ExtractTokens(BloatwareEntry entry)
    {
        var words = $"{entry.Name} {entry.Publisher}"
            .Split(new[] { ' ', '-', '_', ',', '.', '(', ')', '™', '®' }, StringSplitOptions.RemoveEmptyEntries);
        return words
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Simple substring heuristic per the item's own text - the first bloatware source
    /// whose Publisher or any extracted name-token appears (case-insensitive) in the candidate's
    /// combined name/path/description text wins.</summary>
    private static string? FindMatch(List<(string Name, List<string> Tokens)> tokenSources, string haystack)
    {
        if (haystack.Length == 0) return null;
        foreach (var (name, tokens) in tokenSources)
        {
            if (tokens.Any(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return null;
    }
}
