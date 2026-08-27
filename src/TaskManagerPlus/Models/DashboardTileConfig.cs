namespace TaskManagerPlus.Models;

/// <summary>Round 11, #69: one Summary-tab dashboard tile's persisted visibility/order. A full
/// freeform drag-and-drop layout (dragging a tile anywhere on the page, spanning columns) is a
/// meaningfully larger WPF undertaking than the rest of this round's items - this is the
/// honestly-scoped version instead: each tile can be hidden, and reordered up/down within its
/// column, via simple controls, which covers the practical goal ("let me declutter/rearrange my
/// dashboard, and remember it") without gambling a whole feature on a drag-drop adorner layer this
/// app has no prior experience with.</summary>
public sealed class DashboardTileConfig
{
    public string Id { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public int Order { get; set; }
}
