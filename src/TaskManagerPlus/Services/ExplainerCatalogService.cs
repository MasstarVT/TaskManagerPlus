using System.IO;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #993: bundled, fully offline "what does this mean, what usually causes it, what
/// to check next" explainer pages for the built-in rule pack's most common findings (matched by
/// Rule.ExplainerId, populated on ~10 rules in RulesEngineService.BuiltInPackJson). Written out
/// once to AppPaths.SettingsDirectory\Explainers\*.html from the C# constants below - same
/// "bundled-then-persisted" seed pattern GlossaryService already establishes for glossary.json -
/// rather than kept only as embedded resources, so a user could in principle open/edit them
/// directly on disk too.
///
/// #993's task notes ask for "an in-app reader (WebBrowser/WebView2 if already used elsewhere,
/// otherwise a plain new Window with read-only rendering, or - if there's no precedent for either
/// - opening the file in the system default browser)". This app has no WebView2/WebBrowser
/// dependency anywhere (grep-confirmed) and no XAML-based rich-HTML renderer at all, so adding one
/// just for a handful of static help pages would be a new UI-framework dependency for a small
/// feature - the pragmatic choice taken here is the same one #992's "Learn more" link uses
/// (ExternalLinkService.TryOpen, Process.Start+UseShellExecute), which still satisfies "works with
/// no network" since the file itself is 100% local; it just renders in the user's already-installed
/// browser instead of a bespoke in-app viewer.
/// </summary>
public static class ExplainerCatalogService
{
    private static string ExplainersDirectory => AppPaths.GetPath("Explainers");

    private static readonly object Lock = new();
    private static bool _written;

    /// <summary>Returns the on-disk path to `explainerId`'s HTML page, writing out the whole
    /// catalog on first call if it isn't there yet - null for an unrecognized id (a rule with a
    /// typo'd ExplainerId degrades to no "Read more (offline)" button rather than a broken link,
    /// same "degrade, never fabricate" convention as everywhere else in this app).</summary>
    public static string? GetPath(string? explainerId)
    {
        if (string.IsNullOrWhiteSpace(explainerId) || !Pages.ContainsKey(explainerId)) return null;

        EnsureWrittenOut();
        string path = Path.Combine(ExplainersDirectory, explainerId + ".html");
        return File.Exists(path) ? path : null;
    }

    private static void EnsureWrittenOut()
    {
        if (_written) return;
        lock (Lock)
        {
            if (_written) return;
            try
            {
                Directory.CreateDirectory(ExplainersDirectory);
                string css = SummaryViewModel.BuildReportCss(Models.ReportTheme.Dark);
                foreach (var (id, (title, body)) in Pages)
                {
                    string path = Path.Combine(ExplainersDirectory, id + ".html");
                    if (File.Exists(path)) continue; // never overwrite a user's own edited copy
                    string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title><style>{css}</style></head><body><h1>{title}</h1>{body}</body></html>";
                    File.WriteAllText(path, html);
                }
            }
            catch
            {
                // Best-effort - a failed write just means "Read more (offline)" stays hidden for
                // findings this pass didn't manage to seed a file for (GetPath's File.Exists check
                // above catches that).
            }
            _written = true;
        }
    }

    /// <summary>id -> (title, inner-body HTML). Each is 3-4 short paragraphs: what it means, what
    /// usually causes it, what to check next - matched to the ~10 rules in RulesEngineService.
    /// BuiltInPackJson that set the same ExplainerId.</summary>
    private static readonly Dictionary<string, (string Title, string Body)> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disk-full"] = ("Disk critically full", """
            <p><b>What it means:</b> the drive Windows is currently reporting as critically full has almost no free space left. Windows itself, and most apps, need some free space to write temporary files, update themselves, and simply keep working - not just to store your own files.</p>
            <p><b>What usually causes it:</b> large media files (videos, game installs, ISOs), an oversized Downloads folder nobody's cleaned out, Windows' own accumulated update/temp files, or a drive that was simply always too small for what's being kept on it.</p>
            <p><b>What to check next:</b> Storage Sense or Disk Cleanup (both built into Windows) for a quick, safe pass; the Storage tab in this app for which folders are actually large; and whether anything large (a game library, a backup folder) belongs on a different drive entirely.</p>
            """),
        ["dirty-bit"] = ("Volume needs a chkdsk pass", """
            <p><b>What it means:</b> NTFS marks a volume "dirty" when it wasn't unmounted cleanly - most often after a crash, a forced power-off, or Windows itself detecting a filesystem inconsistency. It's a flag asking for a chkdsk pass before the volume is fully trusted again, not proof anything is actually broken.</p>
            <p><b>What usually causes it:</b> a power loss or hard reset while the drive was being written to, a system crash, or (rarely) a failing drive whose write errors are what tripped the flag in the first place.</p>
            <p><b>What to check next:</b> run the "Run chkdsk" fix this finding offers (schedules a scan-and-fix pass); if it keeps coming back after clean shutdowns, that's worth treating as a possible early sign of drive trouble, not just routine housekeeping.</p>
            """),
        ["cpu-hot"] = ("CPU critically hot", """
            <p><b>What it means:</b> the CPU package temperature sensor is reading a level modern CPUs generally treat as "about to throttle" - the chip will start deliberately slowing itself down to avoid damage, which is safe but costs performance.</p>
            <p><b>What usually causes it:</b> dust-clogged fans/heatsinks, a case with poor airflow, dried-out or poorly-applied thermal paste, a demanding sustained workload (gaming, rendering, compiling), or - on a laptop - simply using it on a soft surface that blocks the intake vents.</p>
            <p><b>What to check next:</b> that vents and fans are actually spinning and dust-free, whether this only happens under heavy load (expected) or even at idle (not expected - worth investigating), and this app's Energy & Thermals tab for a fuller temperature/fan picture.</p>
            """),
        ["dead-fan"] = ("Possible stopped fan", """
            <p><b>What it means:</b> a fan this app can read an RPM sensor for is reporting zero or near-zero speed while the system doesn't look like it should be idle-quiet - a pattern consistent with a fan that has physically stopped rather than one that's just correctly spun down.</p>
            <p><b>What usually causes it:</b> dust/debris jamming the blades, a failed fan bearing, a disconnected fan header, or (less commonly) a fan curve/BIOS setting that's stopped it more aggressively than intended.</p>
            <p><b>What to check next:</b> listen/look for whether the physical fan is actually spinning, check the BIOS fan-curve settings if accessible, and treat this as worth a closer look sooner rather than later - a genuinely stopped cooling fan can let temperatures climb quickly under load.</p>
            """),
        ["pagefile-full"] = ("Page file nearly full", """
            <p><b>What it means:</b> Windows' overflow memory space on disk (the page file) is nearly used up. The page file exists so the system can keep running even when physical RAM runs out, by temporarily writing some memory contents to disk - but it's much slower than real RAM.</p>
            <p><b>What usually causes it:</b> genuinely not having enough physical RAM for the current workload, a memory leak in a long-running app, or a page file that Windows sized smaller than what this system's real memory pressure needs.</p>
            <p><b>What to check next:</b> the Processes tab for anything with unusually high or steadily climbing memory use, and whether the page file's size is set to "System managed" (usually the safest default) rather than a small fixed size.</p>
            """),
        ["memory-thrashing"] = ("Possible memory thrashing", """
            <p><b>What it means:</b> the system is reading/writing memory pages from disk (the page file) at a high rate while very little RAM is free - a state commonly called "thrashing", where the system spends more effort swapping data in and out than doing useful work.</p>
            <p><b>What usually causes it:</b> too many memory-hungry apps/browser tabs open at once for the amount of installed RAM, a runaway process with a memory leak, or simply a workload (large data sets, many virtual machines) that's outgrown the RAM available.</p>
            <p><b>What to check next:</b> the Processes tab sorted by memory to find the biggest consumer, closing anything not actively needed right now, and whether this happens consistently enough that more RAM would be the real fix.</p>
            """),
        ["sustained-cpu"] = ("Sustained high CPU", """
            <p><b>What it means:</b> CPU usage has stayed above 90% for a sustained stretch, not just a brief spike - long enough that this app's dwell-time check treats it as an ongoing condition worth flagging rather than normal bursty activity.</p>
            <p><b>What usually causes it:</b> a single demanding app or process doing real, expected work (rendering, compiling, a big export), a background maintenance task (antivirus scan, Windows Update, search indexing), or - less innocently - a runaway/misbehaving process stuck in a loop.</p>
            <p><b>What to check next:</b> the Processes tab to see exactly what's using the CPU and whether that matches something you'd expect to be running right now.</p>
            """),
        ["network-errors"] = ("Network adapter errors", """
            <p><b>What it means:</b> the active network adapter is reporting a nonzero error count on its packets - not a "no internet" situation, but a sign that some traffic on this connection isn't going through cleanly.</p>
            <p><b>What usually causes it:</b> a damaged or poor-quality Ethernet cable, a loose connection, a flaky Wi-Fi signal, outdated network adapter drivers, or a genuinely failing network adapter.</p>
            <p><b>What to check next:</b> reseating or swapping the cable if wired, moving closer to the router if wireless, and the "Reset TCP/IP" fix this finding offers if the error count keeps climbing.</p>
            """),
        ["services-failed"] = ("Services failed to start", """
            <p><b>What it means:</b> one or more Windows services that are configured to start automatically didn't actually start. Depending on which service, this can mean anything from a barely-noticeable missing feature to a real functionality gap (printing, networking, audio).</p>
            <p><b>What usually causes it:</b> a dependency the service needs also failed to start, corrupted system files, a driver conflict, or a service that was disabled/misconfigured by another app's installer.</p>
            <p><b>What to check next:</b> the Services tab for which specific service(s) failed, the "Restart failed services" fix this finding offers, and - if that doesn't hold - an SFC/DISM system-file scan, both offered as follow-up fixes here too.</p>
            """),
        ["reboot-pending"] = ("Restart pending", """
            <p><b>What it means:</b> Windows has finished downloading and partially installing an update but is waiting for a restart to actually apply it and finish cleanup - a completely normal, expected state, not a problem with the system.</p>
            <p><b>What usually causes it:</b> a routine Windows Update, a driver installation, or occasionally an app installer that requested a restart to finish replacing in-use files.</p>
            <p><b>What to check next:</b> just restart when convenient. If a disk-full finding is also active, freeing up space first is worth doing before rebooting, since a pending update can fail to apply on a nearly-full drive.</p>
            """),
    };
}
