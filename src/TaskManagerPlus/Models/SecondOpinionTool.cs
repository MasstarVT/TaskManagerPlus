namespace TaskManagerPlus.Models;

/// <summary>One #900 "second opinion scanner" entry - a link-out only (this app never bundles,
/// downloads, or runs any of these itself) to a reputable vendor tool's official page, with a
/// one-line "good for" description. See SecurityViewModel.SecondOpinionTools for the static list.</summary>
public sealed class SecondOpinionTool
{
    public string Name { get; init; } = string.Empty;
    public string GoodFor { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}
