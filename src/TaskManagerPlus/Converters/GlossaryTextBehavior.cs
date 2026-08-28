using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.Converters;

/// <summary>
/// suggestions.md #990: an attached-property behavior (not a value converter, kept in Converters/
/// alongside them since that's where every other XAML display-formatting helper in this app
/// already lives) that renders a plain string into a TextBlock's Inlines, wrapping any recognized
/// glossary term (GlossaryService) in a dotted-underline Run with a ToolTip showing its
/// definition - "what does this mean?" everywhere jargon like "commit charge"/"WHEA"/"TDR"
/// actually appears, with no separate lookup step. Falls back to a single plain Run (identical to
/// a bare Text="..." binding) when the string matches no known term, so applying this to every
/// finding/description TextBlock costs nothing when there's nothing to highlight.
///
/// Usage: replace `Text="{Binding Message}"` with `conv:GlossaryText.Text="{Binding Message}"` on
/// a plain TextBlock (TextWrapping etc. still set directly on the element as usual).
/// </summary>
public static class GlossaryText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(GlossaryText), new PropertyMetadata(null, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    // Rebuilt once per glossary load (terms don't change at runtime) rather than re-matched
    // against every term individually per call - longest term first so e.g. a hypothetical
    // "CPU" + "CPU core" pair would prefer the longer match.
    private static Regex? _termRegex;
    private static IReadOnlyList<Models.GlossaryTerm>? _termsForRegex;

    private static Regex GetTermRegex()
    {
        var terms = GlossaryService.All;
        if (_termRegex is not null && ReferenceEquals(_termsForRegex, terms)) return _termRegex;

        var ordered = terms.OrderByDescending(t => t.Term.Length).Select(t => Regex.Escape(t.Term)).ToList();
        string pattern = ordered.Count == 0 ? "(?!)" /* never matches */ : $@"\b(?:{string.Join("|", ordered)})\b";
        _termRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _termsForRegex = terms;
        return _termRegex;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();

        string text = e.NewValue as string ?? string.Empty;
        if (text.Length == 0) return;

        Regex regex;
        try { regex = GetTermRegex(); }
        catch
        {
            // A malformed glossary (e.g. a term with regex-breaking content that Regex.Escape
            // somehow still failed on) just means no highlighting this call - the plain text
            // still renders correctly.
            tb.Inlines.Add(new Run(text));
            return;
        }

        int pos = 0;
        foreach (Match match in regex.Matches(text))
        {
            if (match.Index > pos) tb.Inlines.Add(new Run(text[pos..match.Index]));

            var termInfo = GlossaryService.Find(match.Value);
            if (termInfo is null)
            {
                // Shouldn't happen (the regex is built from the same term list), but degrade to
                // plain text rather than a tooltip-less styled run with nothing to show.
                tb.Inlines.Add(new Run(match.Value));
            }
            else
            {
                tb.Inlines.Add(BuildTermRun(match.Value, termInfo.Definition));
            }
            pos = match.Index + match.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }

    private static Run BuildTermRun(string display, string definition)
    {
        var dotted = new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            Pen = new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dot },
            PenThicknessUnit = TextDecorationUnit.FontRecommended,
        };
        var run = new Run(display)
        {
            TextDecorations = new TextDecorationCollection { dotted },
            ToolTip = definition,
            Cursor = System.Windows.Input.Cursors.Help,
        };
        return run;
    }
}
