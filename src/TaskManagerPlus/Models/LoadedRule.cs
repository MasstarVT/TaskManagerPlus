namespace TaskManagerPlus.Models;

/// <summary>Runtime wrapper around a <see cref="Rule"/> once RulesEngineService has loaded it off
/// disk - everything here is derived/session state, never itself serialized back into a pack file
/// (that's what SaveUserRule/ImportRules write plain Rule objects for).</summary>
public sealed class LoadedRule
{
    public required Rule Rule { get; init; }

    /// <summary>File name (not full path) this rule was loaded from - "built-in-pack.json",
    /// "user-overrides.json", or a custom pack file name.</summary>
    public required string SourceFile { get; init; }

    public bool IsBuiltIn { get; init; }
    public bool IsUserOverride { get; init; }

    /// <summary>#923: effective enabled state after rules-overrides.json is applied. Disabled
    /// rules stay loaded (still shown, greyed, in the editor) - they're just skipped when
    /// evaluating.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>#923: Rule.Severity with any override applied - what actually gets used for
    /// IsCritical/coloring when this rule fires.</summary>
    public RuleSeverity EffectiveSeverity { get; set; }
}
