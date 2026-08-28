using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// suggestions.md #999: backs the Troubleshoot tab's "Network activity" sub-page - a read-only
/// disclosure listing every outbound network call this app can make, what triggers it, and where
/// it goes (NetworkActivityCatalogService's static table). The "Offline mode" toggle itself lives
/// on MainViewModel (mirroring AlwaysOnTop/MinimizeToTray - a plain UiPreferences-backed switch
/// reachable from anywhere via AncestorType=Window, same as the Settings drawer's copy of it) so
/// this ViewModel stays a plain read-only data source with no live dependencies, the same shape as
/// GlossaryViewModel/ChangeJournalViewModel.
/// </summary>
public sealed class NetworkActivityViewModel : ObservableObject
{
    public ObservableCollection<NetworkActivityEntry> Entries { get; } = new();

    public NetworkActivityViewModel()
    {
        foreach (var e in NetworkActivityCatalogService.BuildCatalog()) Entries.Add(e);
    }
}
