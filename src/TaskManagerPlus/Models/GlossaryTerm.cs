namespace TaskManagerPlus.Models;

/// <summary>suggestions.md #990: one glossary entry - a technical term this app's own findings/UI
/// text actually uses, plus a two-sentence plain-English definition. Loaded once by
/// GlossaryService from a bundled JSON seed (no network fetch - see GlossaryService's remarks).</summary>
public sealed class GlossaryTerm
{
    public string Term { get; init; } = string.Empty;
    public string Definition { get; init; } = string.Empty;
}
