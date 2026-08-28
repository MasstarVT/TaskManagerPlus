using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #938/#948: one row in the Timeline panel's lane list - a lane's display name, its events
/// (already time-ordered by TimelineViewModel.RebuildLaneEvents), and a persisted visibility
/// checkbox. A thin ObservableObject wrapper around Models.TimelineLane (a bare enum) because
/// IsVisible needs to raise PropertyChanged for its checkbox binding and tell TimelineViewModel to
/// persist + re-filter the detail table/correlation findings whenever it's toggled.
/// </summary>
public sealed class TimelineLaneRow : ObservableObject
{
    public TimelineLane Lane { get; }
    public string DisplayName { get; }

    public ObservableCollection<TimelineEvent> Events { get; } = new();

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value)) VisibilityChanged?.Invoke(); }
    }

    public bool HasEvents => Events.Count > 0;

    public event Action? VisibilityChanged;

    public TimelineLaneRow(TimelineLane lane, string displayName, bool isVisible)
    {
        Lane = lane;
        DisplayName = displayName;
        _isVisible = isVisible;
        Events.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEvents));
    }
}
