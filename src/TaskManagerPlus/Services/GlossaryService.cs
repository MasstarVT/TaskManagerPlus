using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #990: a local "what does this mean?" glossary for the jargon this app's own
/// findings/UI text actually uses (DPC latency, WHEA, TDR, commit charge, ...) - no network fetch,
/// ever. Seeded from a C# constant (<see cref="SeedJson"/>, the same "built-in pack seed" shape
/// RulesEngineService.BuiltInPackJson already uses) and written out once to
/// AppPaths.SettingsDirectory\glossary.json on first run, same as every other bundled-then-
/// persisted JSON file in this app. Loaded once into an in-memory dictionary; a lookup is a plain
/// dictionary hit, cheap enough to call from a tooltip/hover handler with no caching concerns of
/// its own.
/// </summary>
public static class GlossaryService
{
    private static string GlossaryPath => AppPaths.GetPath("glossary.json");

    private static readonly object Lock = new();
    private static List<GlossaryTerm>? _terms;
    private static Dictionary<string, GlossaryTerm>? _byTerm;

    /// <summary>Every glossary term, loaded once and cached for the process lifetime - the
    /// searchable "Glossary" panel's data source (see GlossaryViewModel).</summary>
    public static IReadOnlyList<GlossaryTerm> All
    {
        get { EnsureLoaded(); return _terms!; }
    }

    /// <summary>Exact (case-insensitive) term lookup - used by the dotted-underline tooltip
    /// behavior once it's already matched a span of text to a known term.</summary>
    public static GlossaryTerm? Find(string term)
    {
        EnsureLoaded();
        return _byTerm!.TryGetValue(term.Trim(), out var t) ? t : null;
    }

    private static void EnsureLoaded()
    {
        if (_terms is not null) return;
        lock (Lock)
        {
            if (_terms is not null) return;

            List<GlossaryTerm>? loaded = null;
            try
            {
                if (!File.Exists(GlossaryPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GlossaryPath)!);
                    File.WriteAllText(GlossaryPath, SeedJson);
                }
                var json = File.ReadAllText(GlossaryPath);
                loaded = JsonSerializer.Deserialize<List<GlossaryTerm>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Corrupt/unreadable glossary.json on disk - fall back to the in-memory seed below
                // rather than showing an empty glossary (degrade, never fabricate an empty result
                // when a good default is available).
            }

            if (loaded is null || loaded.Count == 0)
            {
                loaded = JsonSerializer.Deserialize<List<GlossaryTerm>>(SeedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }

            _terms = loaded.OrderBy(t => t.Term, StringComparer.OrdinalIgnoreCase).ToList();
            _byTerm = new Dictionary<string, GlossaryTerm>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _terms)
                _byTerm.TryAdd(t.Term, t);
        }
    }

    /// <summary>~20 terms this app's own findings/rule/troubleshoot text actually uses - see the
    /// grep-confirmed usage sites (RulesEngineService.BuiltInPackJson, TroubleshootViewModel's
    /// crash/sleep/games branches, StabilityViewModel's TDR count, RemediationActionCatalog).</summary>
    private const string SeedJson = """
    [
      { "Term": "DPC latency", "Definition": "How long a driver's Deferred Procedure Call keeps the CPU from handling other urgent work. High DPC latency shows up as audio crackling, dropped frames, or stutter even when overall CPU usage looks low." },
      { "Term": "Hard fault", "Definition": "A memory access that had to be satisfied from disk (the page file or a memory-mapped file) instead of RAM. A high hard-fault rate usually means the system doesn't have enough free RAM and is actively swapping." },
      { "Term": "TDR", "Definition": "Timeout Detection and Recovery - Windows resetting the GPU driver because it stopped responding in time, visible as a brief screen flicker/black-out. Occasional TDRs can be a driver bug; frequent ones often point to unstable overclocks or failing hardware." },
      { "Term": "Commit charge", "Definition": "The total memory the system has promised to back with either RAM or page file space, across every running process. It can exceed physical RAM (that's what the page file is for) - it's a measure of demand, not of RAM actually in use." },
      { "Term": "WHEA", "Definition": "Windows Hardware Error Architecture - the subsystem Windows uses to log hardware-detected errors (CPU, memory, PCIe) to the event log. A WHEA-Logger event is a sign of real hardware involvement, not a software glitch." },
      { "Term": "DRIPS", "Definition": "Deepest Runtime Idle Platform State - the lowest-power state a Modern Standby PC can reach while \"asleep\". A device or driver that keeps blocking DRIPS is why some laptops drain battery noticeably while sleeping." },
      { "Term": "Standby list", "Definition": "RAM Windows has freed from a closed app but is still holding onto (with its old contents) in case that data is needed again soon - reclaimed instantly and for free if a new app actually needs the memory." },
      { "Term": "C-state", "Definition": "A CPU power-saving state entered when a core has no work to do - C0 is fully active, higher numbers (C1, C3, C6...) sleep more of the core for deeper power savings but take longer to wake back up." },
      { "Term": "Thermal throttle", "Definition": "The CPU or GPU deliberately lowering its clock speed because it's running too hot, to protect itself from damage. It trades performance for temperature automatically - it's a safety feature, not a fault." },
      { "Term": "Bugcheck", "Definition": "Windows' internal name for what's commonly called a \"blue screen\" (BSOD) - the OS detected a condition it can't safely continue past, halted immediately, and wrote a bugcheck code plus a memory dump describing what it was doing." },
      { "Term": "Page file", "Definition": "A file on disk Windows uses as overflow space for RAM when physical memory runs low. Much slower than real RAM, so heavy page-file use (visible as a high hard-fault rate) is a sign of memory pressure, not a bug." },
      { "Term": "Working set", "Definition": "The set of physical RAM pages a specific process currently has mapped in and actively using - roughly \"how much RAM this process is really using right now\", as opposed to memory it has reserved but isn't touching." },
      { "Term": "IRQ", "Definition": "Interrupt Request - the signal a piece of hardware sends the CPU to say \"stop what you're doing, I need attention\". Very frequent or slow-to-handle IRQs from one device are a common cause of stutter or high DPC time." },
      { "Term": "Dwell time", "Definition": "How long a condition has to stay true before this app's rules engine treats it as a real, sustained problem rather than a one-off blip - e.g. \"CPU above 90% for at least 30 seconds\", not just one high sample." },
      { "Term": "Dirty bit", "Definition": "A flag NTFS sets on a volume when it wasn't unmounted cleanly (a crash, forced power-off, or a detected inconsistency) - it tells Windows a chkdsk pass is needed before the volume can be fully trusted again." },
      { "Term": "Minidump", "Definition": "A small crash-dump file Windows writes after a blue screen, containing the loaded driver list, stack trace, and bugcheck details for the moment of the crash - much smaller than a full memory dump, but usually enough to diagnose the cause." },
      { "Term": "Reliability index", "Definition": "This app's 0-10 rollup of recent crashes, unexpected shutdowns, and hardware error events into a single stability score - a higher number means a more stable recent history, similar in spirit to Windows' own Reliability Monitor score." },
      { "Term": "SRUM", "Definition": "System Resource Usage Monitor - a Windows component that quietly tracks per-app CPU, network, and energy usage over time, even when no monitoring tool is open. This app reads it for per-app energy estimates and network usage history." },
      { "Term": "Modern Standby", "Definition": "A low-power sleep mode (also called S0 Low Power Idle) used on many newer laptops instead of the traditional S3 sleep - the system stays technically \"on\" at very low power so it can keep receiving notifications, which is also why background app activity can drain the battery while \"asleep\"." },
      { "Term": "Wake timer", "Definition": "A scheduled wake-up Windows or an app has registered so the PC turns itself on (or resumes from sleep) at a specific time, e.g. for a scheduled task or Windows Update maintenance window." }
    ]
    """;
}
