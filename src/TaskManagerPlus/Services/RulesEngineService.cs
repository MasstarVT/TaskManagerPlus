using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #916-921, #927: the Health Check card's rules engine. Loads Rule definitions from every
/// *.json file under AppPaths.SettingsDirectory\Rules\ (seeding a built-in pack there on first
/// run, see <see cref="BuiltInPackJson"/>), watches that folder for hot reload, evaluates rule
/// Conditions (#918's hand-rolled expression tree - no scripting engine) against a metric bag built
/// fresh each call from the app's already-live ViewModels (#917, <see cref="BuildMetricBag"/>),
/// applies #923's enable/disable+severity overrides and #920's sustained-condition dwell time, and
/// filters #924's suppressions before handing SummaryViewModel a list of findings.
///
/// Owned once by MainViewModel and shared by SummaryViewModel (the live Health Check feed) and
/// RulesEditorViewModel (the Settings-drawer rule editor/test/import-export panel) - a single
/// instance means one FileSystemWatcher and one loaded rule set, not two independently-reloading
/// copies drifting apart.
/// </summary>
public sealed class RulesEngineService : IDisposable
{
    // ----- paths --------------------------------------------------------------------------

    private static string RulesDirectory => AppPaths.GetPath("Rules");
    private static string BuiltInPackPath => Path.Combine(RulesDirectory, "built-in-pack.json");

    /// <summary>#922: the one file the rule editor ever writes to for a title/body/severity/
    /// condition edit or a brand-new custom rule - never the built-in pack file, so an app update
    /// that replaces built-in-pack.json can't clobber a user's customization. Also #926's import
    /// target. Loaded last (after every other pack file) so a rule id here transparently replaces
    /// the same id loaded from an earlier pack rather than tripping the duplicate-id check.</summary>
    private const string UserOverridesFileName = "user-overrides.json";
    private static string UserOverridesPath => Path.Combine(RulesDirectory, UserOverridesFileName);

    private const string BuiltInPackFileName = "built-in-pack.json";

    private static string OverridesSettingsPath => AppPaths.GetPath("rules-overrides.json");
    private static string SuppressionsSettingsPath => AppPaths.GetPath("suppressions.json");

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ----- state -----------------------------------------------------------------------------

    private readonly PerformanceViewModel _performance;
    private readonly object _lock = new();
    private List<LoadedRule> _loadedRules = new();
    private List<RuleValidationResult> _validationResults = new();
    private Dictionary<string, RuleOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private List<RuleSuppression> _suppressions = new();

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;

    /// <summary>Fired after LoadPacks (initial load, hot reload, or a save/import that re-loads)
    /// completes. May fire on a FileSystemWatcher threadpool thread - subscribers touching WPF
    /// collections must marshal back to the UI thread themselves (see RulesEditorViewModel/
    /// SummaryViewModel's handlers).</summary>
    public event Action? Reloaded;

    public IReadOnlyList<LoadedRule> Rules { get { lock (_lock) return _loadedRules.ToList(); } }
    public IReadOnlyList<RuleValidationResult> ValidationResults { get { lock (_lock) return _validationResults.ToList(); } }
    public IReadOnlyList<RuleSuppression> Suppressions { get { lock (_lock) return _suppressions.ToList(); } }

    public RulesEngineService(PerformanceViewModel performance)
    {
        _performance = performance;
        EnsureBuiltInPackSeeded();
        _overrides = LoadOverrides();
        _suppressions = LoadSuppressions();
        LoadPacks();
        StartWatching();
    }

    // ----- loading / hot reload (#921) --------------------------------------------------------

    private static void EnsureBuiltInPackSeeded()
    {
        try
        {
            Directory.CreateDirectory(RulesDirectory);
            if (!File.Exists(BuiltInPackPath))
                File.WriteAllText(BuiltInPackPath, BuiltInPackJson);
        }
        catch
        {
            // Best-effort - if the folder isn't writable yet, LoadPacks below just finds nothing
            // and the Health Check card falls back to whichever hand-rolled checks remain.
        }
    }

    public void LoadPacks()
    {
        try { Directory.CreateDirectory(RulesDirectory); } catch { /* best-effort */ }

        var results = new List<RuleValidationResult>();
        var byId = new Dictionary<string, LoadedRule>(StringComparer.OrdinalIgnoreCase);

        string[] files;
        try { files = Directory.GetFiles(RulesDirectory, "*.json"); }
        catch { files = Array.Empty<string>(); }

        var ordered = files
            .Where(f => !string.Equals(Path.GetFileName(f), UserOverridesFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? userOverridesFile = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), UserOverridesFileName, StringComparison.OrdinalIgnoreCase));

        foreach (var path in ordered)
            LoadOneFile(path, byId, results, isOverrideFile: false);

        if (userOverridesFile is not null)
            LoadOneFile(userOverridesFile, byId, results, isOverrideFile: true);

        lock (_lock)
        {
            _loadedRules = byId.Values.ToList();
            _validationResults = results;
            ApplyOverridesLocked();
        }
        Reloaded?.Invoke();
    }

    private static void LoadOneFile(string path, Dictionary<string, LoadedRule> byId, List<RuleValidationResult> results, bool isOverrideFile)
    {
        string fileName = Path.GetFileName(path);
        var vr = new RuleValidationResult { FileName = fileName };

        RulePackFile pack;
        try
        {
            string json = File.ReadAllText(path);
            pack = string.IsNullOrWhiteSpace(json)
                ? new RulePackFile()
                : JsonSerializer.Deserialize<RulePackFile>(json, JsonOpts) ?? new RulePackFile();
        }
        catch (Exception ex)
        {
            // A malformed pack disables itself (0 rules, IsValid = false) - it never takes any
            // other pack, including the built-in one, down with it.
            vr.IsValid = false;
            vr.Warnings.Add($"Parse error: {ex.Message}");
            results.Add(vr);
            return;
        }

        bool isBuiltIn = string.Equals(fileName, BuiltInPackFileName, StringComparison.OrdinalIgnoreCase);
        int loadedCount = 0;

        foreach (var rule in pack.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                vr.Warnings.Add("A rule with no Id was skipped.");
                continue;
            }

            string? conditionError = ValidateCondition(rule.Condition);
            if (conditionError is not null)
            {
                vr.Warnings.Add($"Rule '{rule.Id}': {conditionError} - rule disabled.");
                continue;
            }

            foreach (var unknown in FindUnknownMetrics(rule.Condition).Distinct())
                vr.Warnings.Add($"Rule '{rule.Id}': references unrecognized metric key '{unknown}' (will simply never match if it's really absent from the live metric bag).");

            if (!isOverrideFile && byId.ContainsKey(rule.Id))
            {
                vr.Warnings.Add($"Rule '{rule.Id}': duplicate id (already loaded from another pack) - skipped.");
                continue;
            }

            byId[rule.Id] = new LoadedRule
            {
                Rule = rule,
                SourceFile = fileName,
                IsBuiltIn = isBuiltIn,
                IsUserOverride = isOverrideFile,
                Enabled = true,
                EffectiveSeverity = rule.Severity,
            };
            loadedCount++;
        }

        vr.RuleCount = loadedCount;
        results.Add(vr);
    }

    private void ApplyOverridesLocked()
    {
        foreach (var lr in _loadedRules)
        {
            if (_overrides.TryGetValue(lr.Rule.Id, out var ov))
            {
                lr.Enabled = ov.Enabled ?? true;
                lr.EffectiveSeverity = ov.SeverityOverride ?? lr.Rule.Severity;
            }
            else
            {
                lr.Enabled = true;
                lr.EffectiveSeverity = lr.Rule.Severity;
            }
        }
    }

    // ----- validation helpers (#921) ----------------------------------------------------------

    private static readonly HashSet<string> KnownOps =
        new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "lt", "lte", "gt", "gte", "exists" };

    private static string? ValidateCondition(RuleCondition cond)
    {
        if (cond.All is { } all)
        {
            if (all.Count == 0) return "'all' has no child conditions";
            foreach (var c in all) { var e = ValidateCondition(c); if (e is not null) return e; }
            return null;
        }
        if (cond.Any is { } any)
        {
            if (any.Count == 0) return "'any' has no child conditions";
            foreach (var c in any) { var e = ValidateCondition(c); if (e is not null) return e; }
            return null;
        }
        if (cond.Not is { } not) return ValidateCondition(not);

        if (string.IsNullOrWhiteSpace(cond.Metric) || string.IsNullOrWhiteSpace(cond.Op))
            return "leaf condition is missing metric/op";
        if (!KnownOps.Contains(cond.Op.Trim()))
            return $"unknown operator '{cond.Op}'";
        return null;
    }

    /// <summary>Known metric-bag key prefixes/patterns, for the (non-fatal, informational)
    /// "unknown metric" validation warning - see <see cref="BuildMetricBag"/> for what actually
    /// populates the bag at evaluation time. Per-drive keys and #927's synthetic finding.* keys are
    /// matched by prefix since they're dynamic (one per volume / one per rule id).</summary>
    private static readonly HashSet<string> KnownMetricKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "cpu.percent", "cpu.clockGhz",
        "mem.percent", "mem.availablePercent", "mem.pageFilePercent", "mem.hardFaultsPerSec", "mem.committedGb",
        "disk.percent", "disk.maxPercentUsed", "disk.maxPercentUsedLabel",
        "disk.anyDirty", "disk.dirtyLabel", "disk.anyHealthWarning", "disk.healthWarningLabel", "disk.healthWarningText",
        "network.hasErrors", "network.receiveBps", "network.sendBps",
        "thermal.cpuPackageC", "thermal.deadFanDetected",
        "services.failedCount",
        "system.rebootPending", "system.outdatedDriverCount", "system.multipleAvActive",
        "process.defenderCpuPercent", "process.defenderRunning",
        // #961: history-only keys an Aggregate leaf can read from health-history.jsonl via
        // BackgroundHealthStoreService.GetMetricValue - not present in the live metric bag itself,
        // but a legitimate metric key on an aggregate condition, so listed here too.
        "disk.queueLength", "disk.latencyMs",
    };

    private static bool IsKnownMetric(string metric) =>
        KnownMetricKeys.Contains(metric) ||
        metric.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) ||
        metric.StartsWith("finding.", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> FindUnknownMetrics(RuleCondition cond)
    {
        if (cond.Metric is { Length: > 0 } m && !IsKnownMetric(m)) yield return m;
        if (cond.All is { } all) foreach (var c in all) foreach (var u in FindUnknownMetrics(c)) yield return u;
        if (cond.Any is { } any) foreach (var c in any) foreach (var u in FindUnknownMetrics(c)) yield return u;
        if (cond.Not is { } not) foreach (var u in FindUnknownMetrics(not)) yield return u;
    }

    // ----- hot reload watcher (#921) ----------------------------------------------------------

    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(RulesDirectory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnWatcherEvent;
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Best-effort - hot reload just won't be live (e.g. the folder briefly doesn't exist);
            // LoadPacks still runs once at startup and whenever the app itself edits a pack file.
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires multiple events per single logical write (a text editor's save,
        // or this app's own File.WriteAllText) - coalesce with a short debounce rather than
        // re-parsing every pack file two or three times per edit.
        //
        // The swap is locked because FileSystemWatcher raises these callbacks on threadpool
        // threads and several can be in flight at once: unsynchronized, two events could each
        // read the old timer, dispose it, and then both assign - leaving the losing racer's timer
        // neither cancelled nor disposed, so it still fires a redundant LoadPacks (and a duplicate
        // Reloaded event) 300ms later, leaking one Timer per lost race. LoadPacks itself takes
        // _lock, but only from inside the timer callback below - never while this lock is held -
        // so this cannot deadlock against it.
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                try { LoadPacks(); } catch { /* best-effort - next real change retries */ }
            }, null, 300, System.Threading.Timeout.Infinite);
        }
    }

    // ----- metric bag (#917) --------------------------------------------------------------------

    /// <summary>Builds the read-only named metric bag the rules engine's conditions read by key -
    /// populated fresh, each call, from data these ViewModels are already polling (no new sampling
    /// of its own). Static so both SummaryViewModel (the live Health Check feed) and
    /// RulesEditorViewModel (the editor's live preview + "capture current metrics" test-fixture
    /// button, #925) build it identically.</summary>
    public static Dictionary<string, object> BuildMetricBag(
        PerformanceViewModel performance,
        EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs,
        ServicesViewModel services,
        ProcessesViewModel processes)
        => BuildMetricBag(performance, energyThermals, systemSpecs, services, processes, out _);

    /// <summary>#937: same bag as the 5-argument overload, plus `unavailableMetrics` - metric keys
    /// a collector genuinely tried and failed to populate this pass (sensor absent/access-denied/
    /// timed out), as opposed to a key this system's metric bag simply never carries. Only
    /// SummaryViewModel's live Health Check feed currently threads this through to
    /// RulesEngineService.Evaluate (RulesEditorViewModel's live-preview/test-fixture flows use the
    /// simpler overload above, since they don't render a "couldn't check" list).</summary>
    public static Dictionary<string, object> BuildMetricBag(
        PerformanceViewModel performance,
        EnergyThermalsViewModel energyThermals,
        SystemSpecsViewModel systemSpecs,
        ServicesViewModel services,
        ProcessesViewModel processes,
        out HashSet<string> unavailableMetrics)
    {
        unavailableMetrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu.percent"] = performance.CpuCurrentPercent,
            ["cpu.clockGhz"] = performance.CpuCurrentClockGhz,
            ["mem.percent"] = performance.RamPercent,
            ["mem.availablePercent"] = performance.RamAvailablePercent,
            ["mem.pageFilePercent"] = performance.PageFilePercent,
            ["mem.hardFaultsPerSec"] = performance.HardFaultsPerSec,
            ["mem.committedGb"] = performance.CommittedGb,
            ["disk.percent"] = performance.DiskPercent,
            ["network.hasErrors"] = performance.HasNetworkErrors,
            ["network.receiveBps"] = performance.NetworkReceiveBps,
            ["network.sendBps"] = performance.NetworkSendBps,
            ["thermal.deadFanDetected"] = energyThermals.DeadFanDetected,
            ["services.failedCount"] = (double)services.Services.Count(s => s.HasFailedToStart),
            ["system.rebootPending"] = systemSpecs.RebootPending,
            ["system.outdatedDriverCount"] = (double)systemSpecs.OutdatedDrivers.Count,
            ["system.multipleAvActive"] = systemSpecs.MultipleActiveAvWarning,
        };

        // A sensor-less machine simply omits this key - exists() reads false, everything else
        // reads "absent" rather than a fabricated 0. #937: also flagged unavailable so any rule
        // referencing it gets a distinct "could not check" status instead of silently reading as
        // a clean/false condition.
        if (energyThermals.CpuPackageTempC is { } cpuTemp) bag["thermal.cpuPackageC"] = cpuTemp;
        else unavailableMetrics.Add("thermal.cpuPackageC");

        // Per-drive keys (spec's `disk.<drive>.percentUsed` shape) plus a couple of aggregate
        // "worst volume" keys so a single built-in rule can flag "some drive is full" without
        // needing one generated rule per drive letter.
        double maxPercentUsed = 0; string maxLabel = string.Empty;
        bool anyDirty = false; string dirtyLabel = string.Empty;
        foreach (var volume in systemSpecs.Volumes)
        {
            string key = SanitizeMetricKey(volume.Primary);
            if (key.Length > 0)
            {
                bag[$"disk.{key}.percentUsed"] = volume.PercentUsed;
                bag[$"disk.{key}.isDirty"] = volume.IsDirty;
            }
            if (volume.PercentUsed > maxPercentUsed) { maxPercentUsed = volume.PercentUsed; maxLabel = volume.Primary; }
            if (volume.IsDirty && !anyDirty) { anyDirty = true; dirtyLabel = volume.Primary; }
        }
        bag["disk.maxPercentUsed"] = maxPercentUsed;
        bag["disk.maxPercentUsedLabel"] = maxLabel;
        bag["disk.anyDirty"] = anyDirty;
        bag["disk.dirtyLabel"] = dirtyLabel;

        bool anyHealthWarning = false; string healthLabel = string.Empty, healthText = string.Empty;
        foreach (var disk in systemSpecs.Disks)
        {
            string key = SanitizeMetricKey(disk.Primary);
            if (key.Length > 0) bag[$"disk.health.{key}.warning"] = disk.IsHealthWarning;
            if (disk.IsHealthWarning && !anyHealthWarning) { anyHealthWarning = true; healthLabel = disk.Primary; healthText = disk.HealthText; }
        }
        bag["disk.anyHealthWarning"] = anyHealthWarning;
        bag["disk.healthWarningLabel"] = healthLabel;
        bag["disk.healthWarningText"] = healthText;

        var defender = processes.Processes.FirstOrDefault(p => p.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase));
        bag["process.defenderCpuPercent"] = defender?.CpuPercent ?? 0.0;
        bag["process.defenderRunning"] = defender is not null;

        return bag;
    }

    private static string SanitizeMetricKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in raw.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_');
    }

    // ----- condition evaluation (#918) ----------------------------------------------------------

    public static bool EvaluateCondition(RuleCondition cond, IReadOnlyDictionary<string, object> bag, out string? error)
    {
        error = null;

        if (cond.All is { Count: > 0 } all)
            return all.All(c => EvaluateCondition(c, bag, out _));
        if (cond.Any is { Count: > 0 } any)
            return any.Any(c => EvaluateCondition(c, bag, out _));
        if (cond.Not is not null)
            return !EvaluateCondition(cond.Not, bag, out _);

        if (string.IsNullOrWhiteSpace(cond.Metric) || string.IsNullOrWhiteSpace(cond.Op))
        {
            error = "leaf condition missing metric/op";
            return false;
        }

        // #961: an aggregate-over-history leaf reads BackgroundHealthStoreService's
        // health-history.jsonl instead of the live bag - see RuleCondition.Aggregate's remarks.
        if (!string.IsNullOrWhiteSpace(cond.Aggregate))
            return EvaluateAggregateLeaf(cond, out error);

        bool exists = bag.TryGetValue(cond.Metric, out var raw) && raw is not null;
        string op = cond.Op.Trim().ToLowerInvariant();
        return EvaluateLeafOp(op, exists, raw, cond.Value, out error);
    }

    /// <summary>#961: evaluates one aggregate leaf (Metric/Aggregate/OverSeconds/AggregateThreshold)
    /// against BackgroundHealthStoreService's on-demand (short-TTL-memoized) aggregate computation,
    /// then compares the resulting number via Op/Value exactly like any other leaf's numeric
    /// comparison. A window with no stored data at all reads as "not exists" (op=="exists" false,
    /// every comparison op false) rather than a fabricated 0 - degrade to Unknown, never fabricate.</summary>
    private static bool EvaluateAggregateLeaf(RuleCondition cond, out string? error)
    {
        error = null;
        double? aggregateValue = BackgroundHealthStoreService.ComputeAggregateCached(
            cond.Metric!, cond.Aggregate!, cond.OverSeconds ?? 86400, cond.AggregateThreshold);

        string op = cond.Op!.Trim().ToLowerInvariant();
        if (op == "exists") return aggregateValue.HasValue;
        if (aggregateValue is not { } value) return false;

        return op switch
        {
            "eq" => TryNum(cond.Value, out var eqv) && Math.Abs(value - eqv) < 1e-9,
            "ne" => !(TryNum(cond.Value, out var nev) && Math.Abs(value - nev) < 1e-9),
            "lt" => TryNum(cond.Value, out var ltv) && value < ltv,
            "lte" => TryNum(cond.Value, out var ltev) && value <= ltev,
            "gt" => TryNum(cond.Value, out var gtv) && value > gtv,
            "gte" => TryNum(cond.Value, out var gtev) && value >= gtev,
            _ => Fail(out error, $"unknown operator '{op}'"),
        };
    }

    private static bool Fail(out string? error, string message)
    {
        error = message;
        return false;
    }

    /// <summary>Shared by EvaluateCondition (the hot evaluation path - PreviewAll runs this for
    /// every loaded rule, every ~2s tick) and EvaluateConditionCapturing (#929 - only run once,
    /// against an already-fired rule, to build its drill-down trail) so the actual comparison
    /// logic lives in exactly one place.</summary>
    private static bool EvaluateLeafOp(string op, bool exists, object? raw, object? value, out string? error)
    {
        error = null;
        if (op == "exists") return exists;

        // Degrade to Unknown/hidden, never fabricate: a metric genuinely absent from the bag (no
        // sensor, no drive with that letter, ...) makes every comparison op simply not match,
        // rather than treating a missing value as some default.
        if (!exists) return false;

        switch (op)
        {
            case "eq": return CompareEquals(raw!, value);
            case "ne": return !CompareEquals(raw!, value);
            case "lt": return TryNum(raw, out var a1) && TryNum(value, out var b1) && a1 < b1;
            case "lte": return TryNum(raw, out var a2) && TryNum(value, out var b2) && a2 <= b2;
            case "gt": return TryNum(raw, out var a3) && TryNum(value, out var b3) && a3 > b3;
            case "gte": return TryNum(raw, out var a4) && TryNum(value, out var b4) && a4 >= b4;
            default:
                error = $"unknown operator '{op}'";
                return false;
        }
    }

    /// <summary>#929: identical evaluation semantics to EvaluateCondition, but additionally
    /// appends a ConditionReading for every comparison leaf it visits (a bare `exists` check has
    /// no threshold to show and is skipped) - only ever run once, against an already-fired rule's
    /// condition, to build the Health Check card's "Why am I seeing this?" drill-down trail.</summary>
    public static bool EvaluateConditionCapturing(RuleCondition cond, IReadOnlyDictionary<string, object> bag, List<ConditionReading> readings, out string? error)
    {
        error = null;

        if (cond.All is { Count: > 0 } all)
            return all.All(c => EvaluateConditionCapturing(c, bag, readings, out _));
        if (cond.Any is { Count: > 0 } any)
            return any.Any(c => EvaluateConditionCapturing(c, bag, readings, out _));
        if (cond.Not is not null)
            return !EvaluateConditionCapturing(cond.Not, bag, readings, out _);

        if (string.IsNullOrWhiteSpace(cond.Metric) || string.IsNullOrWhiteSpace(cond.Op))
        {
            error = "leaf condition missing metric/op";
            return false;
        }

        // #961: an aggregate leaf's drill-down reading shows the computed aggregate value itself
        // (e.g. "3" for a countAbove rule), not a live-bag lookup.
        if (!string.IsNullOrWhiteSpace(cond.Aggregate))
        {
            double? aggregateValue = BackgroundHealthStoreService.ComputeAggregateCached(
                cond.Metric, cond.Aggregate!, cond.OverSeconds ?? 86400, cond.AggregateThreshold);
            bool aggResult = EvaluateAggregateLeaf(cond, out error);
            string aggOp = cond.Op.Trim().ToLowerInvariant();
            if (!string.Equals(aggOp, "exists", StringComparison.OrdinalIgnoreCase))
            {
                readings.Add(new ConditionReading
                {
                    Metric = $"{cond.Metric} ({cond.Aggregate})",
                    Op = aggOp,
                    ActualValueText = aggregateValue is { } av ? FormatValue(av) : "(unavailable)",
                    ThresholdText = FormatValue(cond.Value),
                    WasUnavailable = aggregateValue is null,
                });
            }
            return aggResult;
        }

        bool exists = bag.TryGetValue(cond.Metric, out var raw) && raw is not null;
        string op = cond.Op.Trim().ToLowerInvariant();
        bool result = EvaluateLeafOp(op, exists, raw, cond.Value, out error);

        if (!string.Equals(op, "exists", StringComparison.OrdinalIgnoreCase))
        {
            readings.Add(new ConditionReading
            {
                Metric = cond.Metric,
                Op = op,
                ActualValueText = exists ? FormatValue(raw) : "(unavailable)",
                ThresholdText = FormatValue(cond.Value),
                WasUnavailable = !exists,
            });
        }
        return result;
    }

    /// <summary>#937: every metric key a condition tree references, regardless of known/unknown -
    /// used to check whether a rule touched a metric BuildMetricBag marked unavailable this pass.</summary>
    private static IEnumerable<string> AllReferencedMetrics(RuleCondition cond)
    {
        if (cond.Metric is { Length: > 0 } m) yield return m;
        if (cond.All is { } all) foreach (var c in all) foreach (var mm in AllReferencedMetrics(c)) yield return mm;
        if (cond.Any is { } any) foreach (var c in any) foreach (var mm in AllReferencedMetrics(c)) yield return mm;
        if (cond.Not is { } not) foreach (var mm in AllReferencedMetrics(not)) yield return mm;
    }

    private static bool TryNum(object? o, out double d)
    {
        switch (o)
        {
            case double dd: d = dd; return true;
            case float ff: d = ff; return true;
            case int ii: d = ii; return true;
            case long ll: d = ll; return true;
            case bool bb: d = bb ? 1 : 0; return true;
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var jd)) { d = jd; return true; }
                if (je.ValueKind == JsonValueKind.True) { d = 1; return true; }
                if (je.ValueKind == JsonValueKind.False) { d = 0; return true; }
                break;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var sd):
                d = sd; return true;
        }
        d = 0;
        return false;
    }

    private static bool CompareEquals(object raw, object? value)
    {
        if (TryNum(raw, out var a) && TryNum(value, out var b)) return Math.Abs(a - b) < 1e-9;
        string? sa = ToStringVal(raw);
        string? sb = ToStringVal(value);
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToStringVal(object? o) => o switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je => je.ToString(),
        _ => o.ToString(),
    };

    private static string FormatValue(object? v) => v switch
    {
        null => "?",
        double d => d.ToString("0.#", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble().ToString("0.#", CultureInfo.InvariantCulture),
        JsonElement je when je.ValueKind == JsonValueKind.True => "true",
        JsonElement je when je.ValueKind == JsonValueKind.False => "false",
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
        JsonElement je => je.ToString(),
        _ => v.ToString() ?? "",
    };

    /// <summary>#922: a simple plain-English rendering of a condition tree for the rule editor's
    /// list (e.g. "mem.availablePercent &lt; 10").</summary>
    public static string Summarize(RuleCondition cond)
    {
        if (cond.All is { Count: > 0 } all) return "(" + string.Join(" AND ", all.Select(Summarize)) + ")";
        if (cond.Any is { Count: > 0 } any) return "(" + string.Join(" OR ", any.Select(Summarize)) + ")";
        if (cond.Not is not null) return "NOT " + Summarize(cond.Not);
        if (string.IsNullOrWhiteSpace(cond.Metric)) return "(empty condition)";
        if (string.Equals(cond.Op, "exists", StringComparison.OrdinalIgnoreCase)) return $"{cond.Metric} exists";

        string opText = (cond.Op ?? "").Trim().ToLowerInvariant() switch
        {
            "eq" => "==",
            "ne" => "!=",
            "lt" => "<",
            "lte" => "<=",
            "gt" => ">",
            "gte" => ">=",
            _ => cond.Op ?? "?",
        };
        return $"{cond.Metric} {opText} {FormatValue(cond.Value)}";
    }

    /// <summary>#927: true when this condition tree references a `finding.&lt;ruleId&gt;.fired`
    /// synthetic metric anywhere - marks a rule as composite (dependent on another rule's outcome),
    /// which PreviewAll evaluates in a second pass.</summary>
    private static bool ReferencesFinding(RuleCondition cond)
    {
        if (cond.Metric is { Length: > 0 } m && m.StartsWith("finding.", StringComparison.OrdinalIgnoreCase)) return true;
        if (cond.All is { } all && all.Any(ReferencesFinding)) return true;
        if (cond.Any is { } any && any.Any(ReferencesFinding)) return true;
        if (cond.Not is { } not && ReferencesFinding(not)) return true;
        return false;
    }

    // ----- sustained conditions (#920) ----------------------------------------------------------

    /// <summary>Metric keys with a known rolling-history buffer whose units exactly match the
    /// live bag value (percent, in each case here) - see PerformanceViewModel's remarks on
    /// CpuHistory/RamHistory/DiskHistory. Deliberately doesn't include network/committed-memory
    /// history: those buffers are raw bytes-per-tick, not the same unit as the bag's derived
    /// display values, so dwell time against them would silently compare the wrong scale - those
    /// metrics fall through to the instantaneous degrade below instead, same as any metric with no
    /// history buffer at all.</summary>
    private static readonly Dictionary<string, Func<PerformanceViewModel, ObservableCollection<double>>> HistoryAccessors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu.percent"] = p => p.CpuHistory,
            ["mem.percent"] = p => p.RamHistory,
            ["disk.percent"] = p => p.DiskHistory,
        };

    /// <summary>#920: evaluates one rule's condition, honoring SustainedForSeconds where possible.
    /// Dwell time is only evaluated for real when the rule's whole condition is a single leaf
    /// (no all/any/not) against a metric in <see cref="HistoryAccessors"/> with a comparison
    /// operator (not exists) - every other shape (a combinator, a metric with no known history
    /// buffer, an exists check) degrades to a plain instantaneous evaluation of the full condition,
    /// which is the documented, intentional fallback rather than a crash or a guess.</summary>
    private bool EvaluateWithSustain(LoadedRule lr, IReadOnlyDictionary<string, object> bag, out string? error)
    {
        var cond = lr.Rule.Condition;
        int? sustain = lr.Rule.SustainedForSeconds;
        if (sustain is not { } seconds || seconds <= 0)
            return EvaluateCondition(cond, bag, out error);

        bool isSingleLeaf = cond.All is null && cond.Any is null && cond.Not is null
            && !string.IsNullOrWhiteSpace(cond.Metric) && !string.IsNullOrWhiteSpace(cond.Op)
            && !string.Equals(cond.Op, "exists", StringComparison.OrdinalIgnoreCase);

        if (isSingleLeaf && HistoryAccessors.TryGetValue(cond.Metric!, out var accessor))
        {
            var history = accessor(_performance);
            double pollSeconds = Math.Max(0.1, _performance.PollIntervalSeconds);
            int samplesNeeded = Math.Max(1, (int)Math.Ceiling(seconds / pollSeconds));
            int available = Math.Min(samplesNeeded, history.Count);

            if (available == 0)
            {
                error = null;
                return EvaluateCondition(cond, bag, out error);
            }

            error = null;
            for (int i = history.Count - available; i < history.Count; i++)
            {
                var sampleBag = new Dictionary<string, object>(bag, StringComparer.OrdinalIgnoreCase) { [cond.Metric!] = history[i] };
                if (!EvaluateCondition(cond, sampleBag, out error)) return false;
            }
            // Only genuinely "sustained" once there's enough history to cover the full window -
            // otherwise a metric that just started trending up would falsely read as sustained.
            return available >= samplesNeeded;
        }

        // No history available for this metric/shape - degrade to instantaneous rather than crash
        // or silently ignore SustainedForSeconds.
        return EvaluateCondition(cond, bag, out error);
    }

    // ----- evaluation entry points ----------------------------------------------------------

    /// <summary>#922/#925: every enabled rule's raw would-fire outcome against `bag`, honoring
    /// #920's dwell time and #927's two-pass composite evaluation - no suppression filtering (that
    /// only matters for the live Health Check display, not "is this rule pack correct").
    ///
    /// #927: non-composite rules (whose condition doesn't reference any `finding.*` key) are
    /// evaluated first, each publishing a `finding.&lt;id&gt;.fired` key into a working copy of the
    /// bag; composite rules are evaluated in a second pass against that augmented bag. This is a
    /// fixed two-pass evaluation, not a full dependency graph - a composite rule that references
    /// another composite rule's outcome simply sees that key as absent (=> false) in this same
    /// pass, which also quietly breaks any cycle instead of recursing forever.</summary>
    public List<RulePreviewResult> PreviewAll(IReadOnlyDictionary<string, object> baseBag) => PreviewAllInternal(baseBag, out _);

    /// <summary>Does the actual work for PreviewAll, additionally handing back the fully augmented
    /// working bag (base bag + every non-composite rule's `finding.&lt;id&gt;.fired` key) so
    /// Evaluate can capture #929's condition-reading trail (and resolve #932's ImpactTemplate)
    /// against the same bag a composite rule actually fired against, not just the caller's base
    /// bag.</summary>
    private List<RulePreviewResult> PreviewAllInternal(IReadOnlyDictionary<string, object> baseBag, out Dictionary<string, object> augmentedBag)
    {
        List<LoadedRule> rules;
        lock (_lock) rules = _loadedRules.ToList();

        var bag = new Dictionary<string, object>(baseBag, StringComparer.OrdinalIgnoreCase);
        var results = new List<RulePreviewResult>();

        var nonComposite = rules.Where(r => !ReferencesFinding(r.Rule.Condition)).ToList();
        var composite = rules.Where(r => ReferencesFinding(r.Rule.Condition)).ToList();

        foreach (var lr in nonComposite)
        {
            // Always evaluate (rather than short-circuiting on !lr.Enabled) so `err` is assigned
            // on every path - a disabled rule's condition is still evaluated for its
            // finding.<id>.fired key (kept false regardless below), just not surfaced as firing.
            bool conditionTrue = EvaluateWithSustain(lr, bag, out var err);
            bool fired = lr.Enabled && conditionTrue;
            bag[$"finding.{lr.Rule.Id}.fired"] = fired;
            results.Add(new RulePreviewResult { Rule = lr, WouldFire = fired, Error = err });
        }
        foreach (var lr in composite)
        {
            bool conditionTrue = EvaluateWithSustain(lr, bag, out var err);
            bool fired = lr.Enabled && conditionTrue;
            results.Add(new RulePreviewResult { Rule = lr, WouldFire = fired, Error = err });
        }

        augmentedBag = bag;
        return results;
    }

    public sealed class RuleEvaluationResult
    {
        public List<HealthIssue> Findings { get; } = new();

        /// <summary>#924: findings whose rule is currently suppressed (snoozed and not yet
        /// expired, or permanently ignored) - kept separate rather than dropped so the Health
        /// Check card's "N findings suppressed" panel can list/reveal them.</summary>
        public List<HealthIssue> Suppressed { get; } = new();

        /// <summary>#937: enabled rules the engine could not meaningfully evaluate this pass
        /// because their condition referenced a metric BuildMetricBag marked unavailable - kept
        /// out of Findings/Suppressed entirely (a false WouldFire here would just be an artifact
        /// of the missing metric, not a genuinely "checked and clean" result) and surfaced
        /// separately so the Health Check card can show it as visibly distinct from either.</summary>
        public List<CouldNotEvaluateInfo> CouldNotCheck { get; } = new();
    }

    private static readonly HashSet<string> EmptyMetricSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overload for callers with no #937 unavailable-metric tracking (e.g.
    /// RulesEditorViewModel's test-pack-against-a-snapshot flow, where a "couldn't check" list
    /// doesn't apply).</summary>
    public RuleEvaluationResult Evaluate(IReadOnlyDictionary<string, object> bag) => Evaluate(bag, EmptyMetricSet);

    /// <summary>SummaryViewModel's entry point: PreviewAllInternal, converted to HealthIssue and
    /// split into visible vs. suppressed (#924), plus #937's "could not check" list for any
    /// enabled rule whose condition touched a metric in `unavailableMetrics` this pass (see
    /// BuildMetricBag's out-parameter overload).</summary>
    public RuleEvaluationResult Evaluate(IReadOnlyDictionary<string, object> bag, IReadOnlySet<string> unavailableMetrics)
    {
        var preview = PreviewAllInternal(bag, out var augmentedBag);

        List<RuleSuppression> suppressions;
        lock (_lock) suppressions = _suppressions.ToList();
        DateTime now = DateTime.UtcNow;

        var result = new RuleEvaluationResult();
        foreach (var p in preview)
        {
            if (!p.Rule.Enabled) continue;
            var rule = p.Rule.Rule;

            // #937: a rule whose condition touches a metric this pass couldn't populate gets a
            // distinct "could not check" status - never silently folded into "evaluated, false".
            var unavailableRefs = AllReferencedMetrics(rule.Condition)
                .Where(unavailableMetrics.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unavailableRefs.Count > 0)
            {
                result.CouldNotCheck.Add(new CouldNotEvaluateInfo { RuleId = rule.Id, Title = rule.Title, UnavailableMetrics = unavailableRefs });
                continue;
            }

            if (!p.WouldFire) continue;

            // #929: capture exactly what the condition read against the same augmented bag it
            // actually fired against (so a composite rule's `finding.*.fired` reads show up too).
            var readings = new List<ConditionReading>();
            EvaluateConditionCapturing(rule.Condition, augmentedBag, readings, out _);

            var severity = p.Rule.EffectiveSeverity;
            var issue = new HealthIssue
            {
                Message = ResolveBody(rule, augmentedBag),
                IsCritical = severity == RuleSeverity.High,
                RuleId = rule.Id,
                Title = rule.Title,
                Severity = severity,
                Confidence = rule.Confidence,
                Category = rule.Category,
                DocsUrl = rule.DocsUrl,
                GroupKey = rule.GroupKey,
                // #991: resolved the same way Message is, against the same augmented bag - null
                // whenever this rule doesn't define one, which SummaryView/reports fall back on.
                PlainEnglishMessage = string.IsNullOrEmpty(rule.PlainEnglishBody) ? null : ResolveTemplate(rule.PlainEnglishBody, augmentedBag),
                ExplainerId = rule.ExplainerId,
                CounterEvidence = rule.CounterEvidence,
                ImpactText = TryResolveImpactText(rule.ImpactTemplate, augmentedBag),
                ConditionReadings = readings,
                // #967: straight passthrough - RemediationActionCatalog.Resolve does the actual
                // id -> concrete-action work, using this finding's own Evidence/RuleId for context.
                ActionIds = rule.ActionIds,
                // #930: for the built-in pack's simple metric-threshold rules, the evidence IS the
                // metric bag key/value pairs the condition read - legitimate evidence in its own
                // right, not a placeholder for a future richer source. DistinctBy(Label): a
                // range-check rule (e.g. "gte 90 AND lt 97") reads the same metric via two leaves
                // with different thresholds - ConditionReadings keeps both (the drill-down shows
                // each threshold separately), but Evidence only needs the value once per metric.
                Evidence = readings.Select(r => new EvidenceItem { Label = r.Metric, Value = r.ActualValueText }).DistinctBy(e => e.Label).ToList(),
            };

            bool suppressed = suppressions.Any(s => string.Equals(s.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase)
                && (s.ExpiresUtc is null || s.ExpiresUtc > now));
            (suppressed ? result.Suppressed : result.Findings).Add(issue);
        }
        return result;
    }

    private static string ResolveBody(Rule rule, IReadOnlyDictionary<string, object> bag)
    {
        string body = string.IsNullOrEmpty(rule.Body) ? rule.Title : rule.Body;
        return ResolveTemplate(body, bag);
    }

    /// <summary>#991: same `{metric.key}` placeholder substitution ResolveBody uses, factored out
    /// so Rule.PlainEnglishBody can be resolved against the exact same live bag without duplicating
    /// the regex - a placeholder whose key isn't in the bag is left as-is, same as ResolveBody.</summary>
    private static string ResolveTemplate(string template, IReadOnlyDictionary<string, object> bag) =>
        Regex.Replace(template, @"\{([a-zA-Z0-9_.]+)\}", m =>
            bag.TryGetValue(m.Groups[1].Value, out var v) ? FormatValue(v) : m.Value);

    /// <summary>#932: same `{metric.key}` placeholder syntax as ResolveBody, but strict - a
    /// template with any placeholder not present in the live bag resolves to null (no finding
    /// shows a half-filled-in or fabricated impact figure) rather than left with a literal
    /// `{...}` in it.</summary>
    private static string? TryResolveImpactText(string? template, IReadOnlyDictionary<string, object> bag)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        bool allResolved = true;
        string result = Regex.Replace(template, @"\{([a-zA-Z0-9_.]+)\}", m =>
        {
            if (bag.TryGetValue(m.Groups[1].Value, out var v)) return FormatValue(v);
            allResolved = false;
            return m.Value;
        });
        return allResolved ? result : null;
    }

    // ----- overrides (#923) ----------------------------------------------------------------

    public void SetOverride(string ruleId, bool? enabled, RuleSeverity? severityOverride)
    {
        lock (_lock)
        {
            if (!_overrides.TryGetValue(ruleId, out var ov)) { ov = new RuleOverride(); _overrides[ruleId] = ov; }
            if (enabled is not null) ov.Enabled = enabled;
            if (severityOverride is not null) ov.SeverityOverride = severityOverride;
            SaveOverridesLocked();
            ApplyOverridesLocked();
        }
        Reloaded?.Invoke();
    }

    private static Dictionary<string, RuleOverride> LoadOverrides()
    {
        try
        {
            if (File.Exists(OverridesSettingsPath))
            {
                var json = File.ReadAllText(OverridesSettingsPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, RuleOverride>>(json, JsonOpts);
                if (loaded is not null) return new Dictionary<string, RuleOverride>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt/unreadable - fall back to no overrides */ }
        return new Dictionary<string, RuleOverride>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveOverridesLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(OverridesSettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(OverridesSettingsPath, JsonSerializer.Serialize(_overrides, JsonOpts));
        }
        catch { /* best-effort - overrides just won't survive a restart */ }
    }

    // ----- suppressions (#924) ----------------------------------------------------------------

    public void Suppress(string ruleId, string reason, DateTime? expiresUtc)
    {
        lock (_lock)
        {
            _suppressions.RemoveAll(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
            _suppressions.Add(new RuleSuppression { RuleId = ruleId, Reason = reason, CreatedUtc = DateTime.UtcNow, ExpiresUtc = expiresUtc });
            SaveSuppressionsLocked();
        }
    }

    public void ClearSuppression(string ruleId)
    {
        lock (_lock)
        {
            if (_suppressions.RemoveAll(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveSuppressionsLocked();
        }
    }

    private static List<RuleSuppression> LoadSuppressions()
    {
        try
        {
            if (File.Exists(SuppressionsSettingsPath))
            {
                var json = File.ReadAllText(SuppressionsSettingsPath);
                var loaded = JsonSerializer.Deserialize<List<RuleSuppression>>(json, JsonOpts);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupt/unreadable - fall back to no suppressions */ }
        return new List<RuleSuppression>();
    }

    private void SaveSuppressionsLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(SuppressionsSettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SuppressionsSettingsPath, JsonSerializer.Serialize(_suppressions, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    // ----- user rule edits (#922) / import (#926) -------------------------------------------

    private static RulePackFile ReadPackFileOrEmpty(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<RulePackFile>(json, JsonOpts) ?? new RulePackFile { PackName = "My rules" };
            }
        }
        catch { /* corrupt - treat as empty rather than losing the ability to save a new edit */ }
        return new RulePackFile { PackName = "My rules" };
    }

    private static void WritePackFile(string path, RulePackFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
    }

    /// <summary>#922: writes (or updates) one rule into user-overrides.json and reloads. Used both
    /// by "save edit" and "create new rule".</summary>
    public void SaveUserRule(Rule rule)
    {
        lock (_lock)
        {
            var file = ReadPackFileOrEmpty(UserOverridesPath);
            int idx = file.Rules.FindIndex(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) file.Rules[idx] = rule; else file.Rules.Add(rule);
            WritePackFile(UserOverridesPath, file);
        }
        LoadPacks();
    }

    /// <summary>#926: commits a reviewed set of incoming rules into user-overrides.json, tagging
    /// each with the provenance banner fields.</summary>
    public void ImportRules(IEnumerable<Rule> rules, string sourceFileName)
    {
        lock (_lock)
        {
            var file = ReadPackFileOrEmpty(UserOverridesPath);
            foreach (var r in rules)
            {
                r.ImportedFromFile = true;
                r.ImportSourceFileName = sourceFileName;
                int idx = file.Rules.FindIndex(x => string.Equals(x.Id, r.Id, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) file.Rules[idx] = r; else file.Rules.Add(r);
            }
            WritePackFile(UserOverridesPath, file);
        }
        LoadPacks();
    }

    /// <summary>#926: the user's own pack (never the built-in one) - what Export writes to disk.</summary>
    public RulePackFile GetUserPack() => ReadPackFileOrEmpty(UserOverridesPath);

    public static RulePackFile ParsePackFile(string json) =>
        JsonSerializer.Deserialize<RulePackFile>(json, JsonOpts) ?? new RulePackFile();

    public void Dispose()
    {
        _watcher?.Dispose();
        // Same lock as OnWatcherEvent's swap - disposing concurrently with an in-flight watcher
        // callback would otherwise race that callback's own dispose/assign pair.
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    // ----- built-in pack seed (#916) ------------------------------------------------------------

    /// <summary>
    /// Translates a representative chunk of the old hardcoded RefreshHealthIssues if-chain into
    /// rule JSON: volume-full (warning/critical split), dirty-bit, drive health, CPU-hot
    /// (warning/critical split), dead-fan, page-file-full, thrashing, network-errors,
    /// failed-services, outdated-drivers, multi-AV, and reboot-pending - plus two new rules that
    /// exercise #920 (a genuine sustained-CPU dwell-time rule) and #927 (a composite rule that
    /// reads two other rules' fired state) that didn't exist in the old if-chain at all.
    /// </summary>
    private const string BuiltInPackJson = """
    {
      "PackName": "Built-in",
      "Rules": [
        {
          "Id": "builtin.disk.volume-full-warning",
          "Title": "Disk nearly full",
          "Body": "{disk.maxPercentUsedLabel} is {disk.maxPercentUsed}% full",
          "PlainEnglishBody": "Your {disk.maxPercentUsedLabel} drive is running out of free space - it's {disk.maxPercentUsed}% used up.",
          "Severity": "Medium",
          "Confidence": 90,
          "Category": "Storage",
          "GroupKey": "disk",
          "DocsUrl": "https://support.microsoft.com/windows/free-up-drive-space-in-windows-4d1899b3-c9d0-af2f-e2f1-a5e2be0a2fbc",
          "ImpactTemplate": "{disk.maxPercentUsed}% of {disk.maxPercentUsedLabel} used",
          "Condition": { "All": [
            { "Metric": "disk.maxPercentUsed", "Op": "gte", "Value": 90 },
            { "Metric": "disk.maxPercentUsed", "Op": "lt", "Value": 97 }
          ] }
        },
        {
          "Id": "builtin.disk.volume-full-critical",
          "Title": "Disk critically full",
          "Body": "{disk.maxPercentUsedLabel} is {disk.maxPercentUsed}% full",
          "PlainEnglishBody": "Your {disk.maxPercentUsedLabel} drive is almost completely full ({disk.maxPercentUsed}% used) - free up space soon or things like updates and app installs can start failing.",
          "Severity": "High",
          "Confidence": 95,
          "Category": "Storage",
          "GroupKey": "disk",
          "DocsUrl": "https://support.microsoft.com/windows/free-up-drive-space-in-windows-4d1899b3-c9d0-af2f-e2f1-a5e2be0a2fbc",
          "ExplainerId": "disk-full",
          "ImpactTemplate": "{disk.maxPercentUsed}% of {disk.maxPercentUsedLabel} used",
          "Condition": { "Metric": "disk.maxPercentUsed", "Op": "gte", "Value": 97 }
        },
        {
          "Id": "builtin.disk.dirty-bit",
          "Title": "Volume needs a chkdsk pass",
          "Body": "{disk.dirtyLabel} needs a chkdsk pass (dirty bit set)",
          "PlainEnglishBody": "Windows noticed {disk.dirtyLabel} wasn't shut down cleanly last time and wants to double-check it for errors before you fully trust it again.",
          "Severity": "High",
          "Confidence": 95,
          "Category": "Storage",
          "GroupKey": "disk",
          "DocsUrl": "https://learn.microsoft.com/windows-server/administration/windows-commands/chkdsk",
          "ExplainerId": "dirty-bit",
          "ActionIds": ["storage.chkdsk-scan"],
          "Condition": { "Metric": "disk.anyDirty", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.disk.health-warning",
          "Title": "Drive health warning",
          "Body": "Drive health warning: {disk.healthWarningLabel} ({disk.healthWarningText})",
          "Severity": "High",
          "Confidence": 85,
          "Category": "Storage",
          "GroupKey": "disk",
          "DocsUrl": "https://support.microsoft.com/windows/back-up-your-windows-pc-87a81f8a-78fa-456e-b521-ac0560e32338",
          "Condition": { "Metric": "disk.anyHealthWarning", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.cpu.hot-warning",
          "Title": "CPU running hot",
          "Body": "CPU running hot ({thermal.cpuPackageC}°C)",
          "Severity": "Medium",
          "Confidence": 80,
          "Category": "Thermals",
          "DocsUrl": "https://learn.microsoft.com/windows-hardware/drivers/whea/",
          "CounterEvidence": "Ambient room temperature or a recent dust buildup can also cause this, not just a software issue.",
          "ActionIds": ["power.lower-min-proc-state"],
          "Condition": { "All": [
            { "Metric": "thermal.cpuPackageC", "Op": "gte", "Value": 90 },
            { "Metric": "thermal.cpuPackageC", "Op": "lt", "Value": 100 }
          ] }
        },
        {
          "Id": "builtin.cpu.hot-critical",
          "Title": "CPU critically hot",
          "Body": "CPU running hot ({thermal.cpuPackageC}°C)",
          "PlainEnglishBody": "Your CPU is running very hot ({thermal.cpuPackageC}°C) - it may be slowing itself down to protect itself, and it's worth checking the fans and vents for dust.",
          "Severity": "High",
          "Confidence": 90,
          "Category": "Thermals",
          "DocsUrl": "https://learn.microsoft.com/windows-hardware/drivers/whea/",
          "ExplainerId": "cpu-hot",
          "CounterEvidence": "Ambient room temperature or a recent dust buildup can also cause this, not just a software issue.",
          "ActionIds": ["power.lower-min-proc-state"],
          "Condition": { "Metric": "thermal.cpuPackageC", "Op": "gte", "Value": 100 }
        },
        {
          "Id": "builtin.thermal.dead-fan",
          "Title": "Possible stopped fan",
          "Body": "Possible stopped fan detected",
          "Severity": "High",
          "Confidence": 70,
          "Category": "Thermals",
          "ExplainerId": "dead-fan",
          "Condition": { "Metric": "thermal.deadFanDetected", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.mem.pagefile-full",
          "Title": "Page file nearly full",
          "Body": "Page file is {mem.pageFilePercent}% full",
          "PlainEnglishBody": "Windows' overflow memory space on disk (the page file) is almost full - {mem.pageFilePercent}% used.",
          "Severity": "Medium",
          "Confidence": 75,
          "Category": "Memory",
          "DocsUrl": "https://support.microsoft.com/topic/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows-6a37e6c9-b0c4-e5d6-9805-4e83600a7078",
          "ExplainerId": "pagefile-full",
          "Condition": { "Metric": "mem.pageFilePercent", "Op": "gte", "Value": 90 }
        },
        {
          "Id": "builtin.mem.thrashing",
          "Title": "Possible memory thrashing",
          "Body": "Possible memory thrashing: {mem.hardFaultsPerSec} hard faults/sec with only {mem.availablePercent}% RAM available",
          "PlainEnglishBody": "Your PC's memory is overloaded and it's using the hard drive as backup memory ({mem.hardFaultsPerSec} hard faults/sec, only {mem.availablePercent}% RAM free) - that's a common cause of everything feeling sluggish.",
          "Severity": "High",
          "Confidence": 75,
          "Category": "Memory",
          "DocsUrl": "https://learn.microsoft.com/troubleshoot/windows-client/performance/general-troubleshooting-for-slow-performance-issue",
          "ExplainerId": "memory-thrashing",
          "CounterEvidence": "A lot of legitimately open browser tabs or a genuinely large workload can also drive this, not necessarily a leak.",
          "ImpactTemplate": "{mem.availablePercent}% RAM available",
          "Condition": { "All": [
            { "Metric": "mem.hardFaultsPerSec", "Op": "gte", "Value": 500 },
            { "Metric": "mem.availablePercent", "Op": "lt", "Value": 10 }
          ] }
        },
        {
          "Id": "builtin.cpu.sustained-high",
          "Title": "Sustained high CPU",
          "Body": "CPU has been above 90% for a while - check the Processes tab for what's using it",
          "PlainEnglishBody": "Your CPU has been almost completely busy for a while now, not just a brief spike - open the Processes tab to see what's using it.",
          "Severity": "Medium",
          "Confidence": 60,
          "Category": "CPU",
          "SustainedForSeconds": 30,
          "DocsUrl": "https://support.microsoft.com/windows/how-to-use-task-manager-6be7b063-a49b-6a3a-5062-a9296a9c6060",
          "ExplainerId": "sustained-cpu",
          "CounterEvidence": "A legitimate scheduled backup or antivirus scan can also cause sustained high CPU, not just a runaway process.",
          "Condition": { "Metric": "cpu.percent", "Op": "gt", "Value": 90 }
        },
        {
          "Id": "builtin.network.errors",
          "Title": "Network adapter errors",
          "Body": "Network adapter errors detected",
          "Severity": "Medium",
          "Confidence": 70,
          "Category": "Network",
          "DocsUrl": "https://support.microsoft.com/windows/fix-network-connection-issues-in-windows-166e3b3a-282a-4a3e-add4-2827ff2a3ec6",
          "ExplainerId": "network-errors",
          "CounterEvidence": "A flaky cable or an adapter that was recently unplugged/replugged can also cause a transient error count, not necessarily a failing NIC.",
          "ActionIds": ["network.reset-tcpip"],
          "Condition": { "Metric": "network.hasErrors", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.services.failed",
          "Title": "Services failed to start",
          "Body": "{services.failedCount} service(s) failed to start",
          "PlainEnglishBody": "{services.failedCount} background Windows service(s) that were supposed to start didn't - some system features may not work correctly until this is fixed.",
          "Severity": "Medium",
          "Confidence": 80,
          "Category": "Services",
          "DocsUrl": "https://learn.microsoft.com/windows-server/administration/windows-commands/sc-query",
          "ExplainerId": "services-failed",
          "ImpactTemplate": "{services.failedCount} service(s) not running",
          "ActionIds": ["services.restart-failed", "system.sfc-scannow", "system.dism-restorehealth"],
          "Condition": { "Metric": "services.failedCount", "Op": "gt", "Value": 0 }
        },
        {
          "Id": "builtin.system.outdated-drivers",
          "Title": "Drivers may need updating",
          "Body": "{system.outdatedDriverCount} driver(s) may need updating",
          "Severity": "Medium",
          "Confidence": 50,
          "Category": "System",
          "DocsUrl": "https://support.microsoft.com/windows/update-drivers-manually-in-windows-ec62f46c-ff14-c91d-eabb-a334cdfbc464",
          "Condition": { "Metric": "system.outdatedDriverCount", "Op": "gt", "Value": 0 }
        },
        {
          "Id": "builtin.system.multiple-av",
          "Title": "Multiple antivirus products active",
          "Body": "Multiple antivirus products look active",
          "Severity": "Medium",
          "Confidence": 60,
          "Category": "System",
          "DocsUrl": "https://learn.microsoft.com/microsoft-365/security/defender-endpoint/microsoft-defender-antivirus-compatibility",
          "Condition": { "Metric": "system.multipleAvActive", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.system.reboot-pending",
          "Title": "Restart pending",
          "Body": "A restart is pending to finish installing updates",
          "PlainEnglishBody": "Windows finished installing an update but needs you to restart the PC to actually apply it.",
          "Severity": "Medium",
          "Confidence": 90,
          "Category": "System",
          "GroupKey": "reboot",
          "DocsUrl": "https://learn.microsoft.com/windows/deployment/update/how-windows-update-works",
          "ExplainerId": "reboot-pending",
          "Condition": { "Metric": "system.rebootPending", "Op": "eq", "Value": true }
        },
        {
          "Id": "builtin.system.multiple-issues",
          "Title": "Multiple health issues active at once",
          "Body": "Disk is critically full and a restart is pending - free up space before rebooting so a pending update doesn't fail to apply",
          "Severity": "High",
          "Confidence": 55,
          "Category": "System",
          "GroupKey": "composite",
          "Condition": { "All": [
            { "Metric": "finding.builtin.disk.volume-full-critical.fired", "Op": "eq", "Value": true },
            { "Metric": "finding.builtin.system.reboot-pending.fired", "Op": "eq", "Value": true }
          ] }
        },
        {
          "Id": "builtin.thermal.cpu-hot-repeated-days",
          "Title": "CPU has run critically hot on multiple days recently",
          "Body": "CPU package temperature has exceeded 95C on 3 or more separate days in the last 30 days, per the background health history - worth checking cooling/dust if this keeps happening",
          "Severity": "Medium",
          "Confidence": 55,
          "Category": "Thermals",
          "DocsUrl": "https://learn.microsoft.com/windows-hardware/drivers/whea/",
          "CounterEvidence": "A demanding sustained workload (rendering, compiling, gaming) on those specific days can also explain this, not necessarily a cooling problem.",
          "Condition": { "Metric": "thermal.cpuPackageC", "Aggregate": "countAbove", "AggregateThreshold": 95, "OverSeconds": 2592000, "Op": "gte", "Value": 3 }
        }
      ]
    }
    """;
}
