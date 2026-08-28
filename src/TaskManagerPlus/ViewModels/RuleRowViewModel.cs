using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One row of the Settings drawer's "Rules engine" list (#922) - wraps a LoadedRule with
/// the two directly-editable-inline bits (#923's Enabled checkbox and severity-override dropdown)
/// plus the #920 live "would currently fire" flag RulesEditorViewModel's timer refreshes.</summary>
public sealed class RuleRowViewModel : ObservableObject
{
    private readonly RulesEngineService _engine;

    public LoadedRule Loaded { get; private set; }
    public Rule Rule => Loaded.Rule;

    public string Id => Rule.Id;
    public string Title => Rule.Title;
    public string Body => Rule.Body;
    public string Category => Rule.Category;
    public string? DocsUrl => Rule.DocsUrl;
    public int Confidence => Rule.Confidence;
    public int? SustainedForSeconds => Rule.SustainedForSeconds;
    public string ConditionSummary => RulesEngineService.Summarize(Rule.Condition);
    public bool IsBuiltIn => Loaded.IsBuiltIn;
    public string SourceFile => Loaded.SourceFile;

    /// <summary>#926: provenance badge - true only for a rule that arrived via Import.</summary>
    public bool IsImported => Rule.ImportedFromFile;
    public string? ImportSourceFileName => Rule.ImportSourceFileName;

    public bool Enabled
    {
        get => Loaded.Enabled;
        set
        {
            if (Loaded.Enabled == value) return;
            _engine.SetOverride(Id, value, null);
            OnPropertyChanged();
        }
    }

    public RuleSeverity EffectiveSeverity
    {
        get => Loaded.EffectiveSeverity;
        set
        {
            if (Loaded.EffectiveSeverity == value) return;
            _engine.SetOverride(Id, null, value);
            OnPropertyChanged();
        }
    }

    private bool _wouldFire;
    public bool WouldFire { get => _wouldFire; set => SetProperty(ref _wouldFire, value); }

    public RuleRowViewModel(LoadedRule loaded, RulesEngineService engine)
    {
        Loaded = loaded;
        _engine = engine;
    }
}
