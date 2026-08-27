using TaskManagerPlus.Common;

namespace TaskManagerPlus.ViewModels;

/// <summary>Round 11, #69: one dashboard tile's live UI state - wraps a DashboardTileConfig's
/// Id/IsVisible/Order with the up/down move commands the "Customize tiles" panel and the tile's
/// own Visibility binding both need. Owned by SummaryViewModel; MoveUp/MoveDown are simple
/// delegates back into the owner rather than this class knowing about ObservableCollection
/// reordering itself.</summary>
public sealed class DashboardTileViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            Changed?.Invoke();
        }
    }

    /// <summary>Sort key only - not bound in the UI directly, just used to rebuild the owning
    /// ObservableCollection's order after a load/persist round-trip.</summary>
    public int Order { get; set; }

    /// <summary>Fired after IsVisible changes (not after construction) so the owner can persist -
    /// left unassigned by BuildTiles until after the initial value is set, avoiding a spurious
    /// save on load.</summary>
    public Action? Changed;

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public DashboardTileViewModel(string id, string displayName, bool isVisible, int order, Action moveUp, Action moveDown)
    {
        Id = id;
        DisplayName = displayName;
        _isVisible = isVisible;
        Order = order;
        MoveUpCommand = new RelayCommand(_ => moveUp());
        MoveDownCommand = new RelayCommand(_ => moveDown());
    }
}
