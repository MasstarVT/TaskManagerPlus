using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// suggestions.md #990: backs the Troubleshoot tab's "Glossary" sub-page - a searchable, browsable
/// list of every term GlossaryService knows, the standalone counterpart to the inline
/// dotted-underline/tooltip rendering (Converters/GlossaryTextBehavior.cs) used on finding text
/// elsewhere. No live ViewModel dependencies (GlossaryService's data never changes at runtime), so
/// a plain parameterless constructor is enough - the same shape ChangeJournalViewModel already
/// establishes for a sibling Troubleshoot sub-page with no live-state needs.
/// </summary>
public sealed class GlossaryViewModel : ObservableObject
{
    private readonly List<GlossaryTerm> _all;

    public ObservableCollection<GlossaryTerm> Terms { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) Refresh(); }
    }

    public GlossaryViewModel()
    {
        _all = GlossaryService.All.ToList();
        Refresh();
    }

    private void Refresh()
    {
        Terms.Clear();
        var q = SearchText.Trim();
        var matches = q.Length == 0
            ? _all
            : _all.Where(t => t.Term.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Definition.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var t in matches) Terms.Add(t);
    }
}
