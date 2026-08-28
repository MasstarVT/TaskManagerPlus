namespace TaskManagerPlus.Models;

/// <summary>#921: per-pack-file result of the last hot-reload pass, surfaced in the Settings
/// drawer's "Rule packs" panel. A malformed pack (bad JSON) disables itself entirely (IsValid =
/// false, RuleCount = 0) without taking any other pack - including the built-in one - down with it;
/// a pack that parses fine but has individual bad rules (unknown operator, duplicate id) stays
/// IsValid = true with those specific rules skipped and reported in Warnings.</summary>
public sealed class RuleValidationResult
{
    public string FileName { get; set; } = string.Empty;
    public bool IsValid { get; set; } = true;
    public int RuleCount { get; set; }
    public List<string> Warnings { get; } = new();
}
