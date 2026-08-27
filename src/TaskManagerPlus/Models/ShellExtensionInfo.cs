namespace TaskManagerPlus.Models;

/// <summary>One registered Explorer shell extension (context-menu handler / icon overlay COM
/// add-in, #20) - a classic, often-overlooked cause of a slow Explorer right-click menu. See
/// Services/ShellExtensionService.</summary>
public sealed class ShellExtensionInfo
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Clsid { get; init; } = string.Empty;
    public string DllPath { get; init; } = string.Empty;

    /// <summary>True when this CLSID appears in the "Shell Extensions\Approved" list - Windows
    /// itself uses this list to allow context-menu handlers to load without a warning prompt;
    /// an unapproved entry isn't necessarily broken, just informational.</summary>
    public bool IsApproved { get; init; }

    public string ApprovedText => IsApproved ? "Approved" : "Not listed";
}
