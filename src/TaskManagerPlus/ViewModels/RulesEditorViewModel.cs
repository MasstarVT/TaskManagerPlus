using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #921-926: backs the Settings drawer's "Rules engine" section - the pack validation report
/// (#921), the rule list with inline enable/disable + severity override (#923) and a live
/// would-fire indicator (#922), an edit form that writes to the user's own pack rather than the
/// built-in one (#922), suppression is handled on the Summary Health Check card itself (#924, see
/// SummaryViewModel) since that's where findings actually render, and a metric-bag capture / "test
/// pack against a saved snapshot" flow plus rule-pack import/export with a review step (#925, #926).
///
/// Shares one RulesEngineService instance with SummaryViewModel (constructed once by
/// MainViewModel) rather than owning a second one, so an edit made here is immediately reflected in
/// the live Health Check card and vice versa.
/// </summary>
public sealed class RulesEditorViewModel : ObservableObject, IDisposable
{
    private readonly RulesEngineService _engine;
    private readonly PerformanceViewModel _performance;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly SystemSpecsViewModel _systemSpecs;
    private readonly ServicesViewModel _services;
    private readonly ProcessesViewModel _processes;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<RuleRowViewModel> Rows { get; } = new();
    public ObservableCollection<RuleValidationResult> PackValidation { get; } = new();
    public ObservableCollection<Rule> PendingImportRules { get; } = new();
    public ObservableCollection<RuleTestPreviewRow> TestResults { get; } = new();

    public static Array SeverityValues { get; } = Enum.GetValues(typeof(RuleSeverity));

    private RuleRowViewModel? _selectedRow;
    public RuleRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (SetProperty(ref _selectedRow, value) && !_rebuildingRows) LoadEditBuffer(); }
    }

    // Set while RebuildRows swaps in fresh row instances: any engine reload (hot reload, an
    // override checkbox, a suppression) raises Reloaded, and re-selecting the same rule must not
    // overwrite unsaved edits in the form.
    private bool _rebuildingRows;

    private string _editTitle = string.Empty;
    public string EditTitle { get => _editTitle; set => SetProperty(ref _editTitle, value); }

    private string _editBody = string.Empty;
    public string EditBody { get => _editBody; set => SetProperty(ref _editBody, value); }

    private string _editCategory = string.Empty;
    public string EditCategory { get => _editCategory; set => SetProperty(ref _editCategory, value); }

    private string _editDocsUrl = string.Empty;
    public string EditDocsUrl { get => _editDocsUrl; set => SetProperty(ref _editDocsUrl, value); }

    private string _editGroupKey = string.Empty;
    public string EditGroupKey { get => _editGroupKey; set => SetProperty(ref _editGroupKey, value); }

    private RuleSeverity _editSeverity;
    public RuleSeverity EditSeverity { get => _editSeverity; set => SetProperty(ref _editSeverity, value); }

    private int _editConfidence = 70;
    public int EditConfidence { get => _editConfidence; set => SetProperty(ref _editConfidence, value); }

    private string _editSustainedText = string.Empty;
    public string EditSustainedText { get => _editSustainedText; set => SetProperty(ref _editSustainedText, value); }

    private string _editConditionJson = "{}";
    public string EditConditionJson { get => _editConditionJson; set => SetProperty(ref _editConditionJson, value); }

    private string _editConditionPreview = string.Empty;
    public string EditConditionPreview { get => _editConditionPreview; private set => SetProperty(ref _editConditionPreview, value); }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private bool _isImportReviewOpen;
    public bool IsImportReviewOpen { get => _isImportReviewOpen; private set => SetProperty(ref _isImportReviewOpen, value); }

    private string _importSourceFileName = string.Empty;
    public string ImportSourceFileName { get => _importSourceFileName; private set => SetProperty(ref _importSourceFileName, value); }

    public RelayCommand SaveEditCommand { get; }
    public RelayCommand PreviewConditionCommand { get; }
    public RelayCommand NewRuleCommand { get; }
    public RelayCommand ExportUserPackCommand { get; }
    public RelayCommand ImportPackCommand { get; }
    public RelayCommand ConfirmImportCommand { get; }
    public RelayCommand CancelImportCommand { get; }
    public RelayCommand CaptureBagCommand { get; }
    public RelayCommand TestPackCommand { get; }

    public RulesEditorViewModel(RulesEngineService engine, PerformanceViewModel performance,
        EnergyThermalsViewModel energyThermals, SystemSpecsViewModel systemSpecs,
        ServicesViewModel services, ProcessesViewModel processes)
    {
        _engine = engine;
        _performance = performance;
        _energyThermals = energyThermals;
        _systemSpecs = systemSpecs;
        _services = services;
        _processes = processes;

        SaveEditCommand = new RelayCommand(_ => SaveEdit(), _ => SelectedRow is not null);
        PreviewConditionCommand = new RelayCommand(_ => PreviewCondition());
        NewRuleCommand = new RelayCommand(_ => NewRule());
        ExportUserPackCommand = new RelayCommand(_ => ExportUserPack());
        ImportPackCommand = new RelayCommand(_ => ImportPack());
        ConfirmImportCommand = new RelayCommand(_ => ConfirmImport(), _ => PendingImportRules.Count > 0);
        CancelImportCommand = new RelayCommand(_ => { PendingImportRules.Clear(); IsImportReviewOpen = false; });
        CaptureBagCommand = new RelayCommand(_ => CaptureBag());
        TestPackCommand = new RelayCommand(_ => TestPack());

        _engine.Reloaded += OnEngineReloaded;
        RebuildRows();

        // #922: a lightweight timer, matching the Health Check card's own 2s cadence - just reads
        // already-live ViewModel state through BuildMetricBag, no I/O of its own.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshLivePreview();
        _timer.Start();
        RefreshLivePreview();
    }

    private void OnEngineReloaded()
    {
        // Always post through the dispatcher, even when already on the UI thread - SetOverride
        // (an Enabled checkbox or severity ComboBox edit) raises Reloaded synchronously, and
        // rebuilding Rows (which the ComboBox/CheckBox that triggered this is itself bound into)
        // from inside that same binding-update call stack would be reentrant. Posting lets the
        // triggering binding update finish first.
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() => { RebuildRows(); RefreshLivePreview(); });
    }

    private void RebuildRows()
    {
        string? selectedId = SelectedRow?.Id;
        _rebuildingRows = true;
        try
        {
            Rows.Clear();
            foreach (var lr in _engine.Rules.OrderBy(r => r.Rule.Category, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Rule.Title, StringComparer.OrdinalIgnoreCase))
                Rows.Add(new RuleRowViewModel(lr, _engine));

            PackValidation.Clear();
            foreach (var v in _engine.ValidationResults) PackValidation.Add(v);

            SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Id == selectedId);
        }
        finally
        {
            _rebuildingRows = false;
        }

        // The selected rule itself went away - clear the form rather than leave a stale buffer.
        if (selectedId is not null && SelectedRow is null) LoadEditBuffer();
    }

    private void RefreshLivePreview()
    {
        var bag = RulesEngineService.BuildMetricBag(_performance, _energyThermals, _systemSpecs, _services, _processes);
        var preview = _engine.PreviewAll(bag);
        foreach (var row in Rows)
        {
            var match = preview.FirstOrDefault(p => p.Rule.Rule.Id == row.Id);
            row.WouldFire = match?.WouldFire ?? false;
        }
    }

    private void LoadEditBuffer()
    {
        if (SelectedRow is null)
        {
            EditTitle = EditBody = EditCategory = EditDocsUrl = EditGroupKey = EditSustainedText = string.Empty;
            EditConditionJson = "{}";
            EditConditionPreview = string.Empty;
            return;
        }

        var r = SelectedRow.Rule;
        EditTitle = r.Title;
        EditBody = r.Body;
        EditCategory = r.Category;
        EditDocsUrl = r.DocsUrl ?? string.Empty;
        EditGroupKey = r.GroupKey ?? string.Empty;
        EditSeverity = r.Severity;
        EditConfidence = r.Confidence;
        EditSustainedText = r.SustainedForSeconds?.ToString() ?? string.Empty;
        EditConditionJson = JsonSerializer.Serialize(r.Condition, RulesEngineService.JsonOpts);
        EditConditionPreview = RulesEngineService.Summarize(r.Condition);
        StatusText = string.Empty;
    }

    private void PreviewCondition()
    {
        try
        {
            var cond = JsonSerializer.Deserialize<RuleCondition>(EditConditionJson, RulesEngineService.JsonOpts) ?? new RuleCondition();
            EditConditionPreview = RulesEngineService.Summarize(cond);
        }
        catch (Exception ex)
        {
            EditConditionPreview = $"Invalid condition JSON: {ex.Message}";
        }
    }

    private void SaveEdit()
    {
        if (SelectedRow is null) return;
        try
        {
            var condition = JsonSerializer.Deserialize<RuleCondition>(EditConditionJson, RulesEngineService.JsonOpts) ?? new RuleCondition();
            // Start from a full copy of the rule being edited, so every field this form doesn't
            // show (ActionIds, AlertChannel, escalation, PlainEnglishBody, ExplainerId,
            // CounterEvidence, ImpactTemplate, import provenance) survives the save - the saved
            // copy replaces the original by Id.
            var rule = JsonSerializer.Deserialize<Rule>(JsonSerializer.Serialize(SelectedRow.Rule, RulesEngineService.JsonOpts), RulesEngineService.JsonOpts)
                ?? throw new InvalidOperationException("couldn't copy the rule being edited");
            rule.Id = SelectedRow.Id;
            rule.Title = EditTitle;
            rule.Body = EditBody;
            rule.Category = EditCategory;
            rule.DocsUrl = string.IsNullOrWhiteSpace(EditDocsUrl) ? null : EditDocsUrl;
            rule.Severity = EditSeverity;
            rule.Confidence = Math.Clamp(EditConfidence, 0, 100);
            rule.GroupKey = string.IsNullOrWhiteSpace(EditGroupKey) ? null : EditGroupKey;
            rule.SustainedForSeconds = int.TryParse(EditSustainedText, out var s) && s > 0 ? s : null;
            rule.Condition = condition;
            _engine.SaveUserRule(rule);
            StatusText = "Saved to your rule overrides (user-overrides.json) - the built-in pack file was not touched.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save: {ex.Message}";
        }
    }

    private void NewRule()
    {
        var rule = new Rule
        {
            Id = $"user.{Guid.NewGuid():N}"[..16],
            Title = "New rule",
            Body = "New rule fired",
            Category = "Custom",
            Severity = RuleSeverity.Medium,
            Confidence = 70,
            Condition = new RuleCondition { Metric = "cpu.percent", Op = "gt", Value = 90 },
        };
        try
        {
            _engine.SaveUserRule(rule);
            StatusText = "New rule created - edit it below.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't create the rule: {ex.Message}";
        }
    }

    private void ExportUserPack()
    {
        RulePackFile pack;
        try { pack = _engine.GetUserPack(); }
        catch (Exception ex)
        {
            StatusText = $"Couldn't export: {ex.Message}";
            return;
        }
        var dialog = new SaveFileDialog
        {
            RestoreDirectory = true,
            Title = "Export rule pack",
            Filter = "Rule pack files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "TaskManagerPlus-RulePack.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(pack, RulesEngineService.JsonOpts));
            StatusText = $"Exported {pack.Rules.Count} rule(s) to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't export: {ex.Message}";
        }
    }

    private void ImportPack()
    {
        var dialog = new OpenFileDialog
        {
            RestoreDirectory = true,
            Title = "Import rule pack",
            Filter = "Rule pack files (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var pack = RulesEngineService.ParsePackFile(json);
            PendingImportRules.Clear();
            foreach (var r in pack.Rules) PendingImportRules.Add(r);
            ImportSourceFileName = Path.GetFileName(dialog.FileName);
            IsImportReviewOpen = PendingImportRules.Count > 0;
            StatusText = PendingImportRules.Count == 0
                ? "That file had no rules to import."
                : $"Review the {PendingImportRules.Count} rule(s) below, then confirm to import.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't read that file: {ex.Message}";
        }
    }

    /// <summary>#926: commits the reviewed import - every rule gets a persistent "imported from
    /// &lt;file&gt;, not verified by this app" provenance badge (Rule.ImportedFromFile/
    /// ImportSourceFileName), rendered in the rule list.</summary>
    private void ConfirmImport()
    {
        try
        {
            _engine.ImportRules(PendingImportRules.ToList(), ImportSourceFileName);
        }
        catch (Exception ex)
        {
            // Pending rules stay listed so the import can be retried.
            StatusText = $"Couldn't import: {ex.Message}";
            return;
        }
        PendingImportRules.Clear();
        IsImportReviewOpen = false;
        StatusText = $"Imported from {ImportSourceFileName} - not verified by this app.";
    }

    private void CaptureBag()
    {
        var bag = RulesEngineService.BuildMetricBag(_performance, _energyThermals, _systemSpecs, _services, _processes);
        var dir = AppPaths.GetPath("RuleTestFixtures");
        try { Directory.CreateDirectory(dir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            RestoreDirectory = true,
            Title = "Capture current metrics",
            Filter = "Metric snapshot (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"metrics-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json",
            InitialDirectory = dir,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(bag, RulesEngineService.JsonOpts));
            StatusText = $"Captured current metrics to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't capture: {ex.Message}";
        }
    }

    /// <summary>#925: evaluates every loaded rule against a previously-saved metric-bag snapshot
    /// instead of live data.</summary>
    private void TestPack()
    {
        var dir = AppPaths.GetPath("RuleTestFixtures");
        var dialog = new OpenFileDialog
        {
            RestoreDirectory = true,
            Title = "Test rules against a saved snapshot",
            Filter = "Metric snapshot (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(dir) ? dir : null,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var raw = JsonSerializer.Deserialize<Dictionary<string, object>>(json, RulesEngineService.JsonOpts) ?? new();
            // A hand-edited snapshot can hold explicit nulls or blank keys - drop them so they read
            // as an absent metric rather than reaching the condition evaluator.
            var bag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null and not JsonElement { ValueKind: JsonValueKind.Null })
                    bag[kv.Key] = kv.Value;
            var preview = _engine.PreviewAllAgainstSnapshot(bag);

            TestResults.Clear();
            foreach (var p in preview.OrderByDescending(p => p.WouldFire).ThenBy(p => p.Rule.Rule.Title, StringComparer.OrdinalIgnoreCase))
            {
                TestResults.Add(new RuleTestPreviewRow
                {
                    RuleId = p.Rule.Rule.Id,
                    Title = p.Rule.Rule.Title,
                    WouldFire = p.WouldFire,
                    Error = p.Error,
                });
            }
            StatusText = $"Tested against {Path.GetFileName(dialog.FileName)}: {preview.Count(p => p.WouldFire)} of {preview.Count} rule(s) would fire.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't test: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _engine.Reloaded -= OnEngineReloaded;
    }
}
