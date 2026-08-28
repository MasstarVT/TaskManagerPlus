namespace TaskManagerPlus.Models;

/// <summary>#325: "changed since last run" - one row per key attribute whose value differs from the
/// previous snapshot for the same disk. Only attributes with an actual delta are produced - an
/// unchanged attribute is simply omitted rather than listed as "+0", since the point of this panel
/// is "what's new", not a full restatement of the current triage tiles.</summary>
public sealed record SmartHistoryChange(string Label, int Previous, int Current)
{
    public int Delta => Current - Previous;
    public string DeltaText => Delta > 0 ? $"+{Delta:N0}" : Delta.ToString("N0");
}
