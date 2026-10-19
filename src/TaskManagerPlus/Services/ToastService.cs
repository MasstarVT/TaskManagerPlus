using TaskManagerPlus.Views;

namespace TaskManagerPlus.Services;

/// <summary>Shows the custom toast popup (#72) - see ToastWindow's remarks for why this is a
/// hand-rolled window rather than a native Windows toast.</summary>
public static class ToastService
{
    private static readonly List<(ToastWindow Toast, int Slot)> Open = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    public static void Show(string title, string message, bool isCritical = false)
    {
        var toast = new ToastWindow(title, message, isCritical);

        // Stack above the toasts already showing: take the lowest slot none of them holds. Counting
        // open toasts instead put a lone toast one slot up (the count included itself) and, once an
        // earlier toast closed, put the next one exactly on top of a toast still showing.
        int slot = 0;
        while (Open.Any(o => o.Slot == slot)) slot++;
        var entry = (toast, slot);

        // ToastWindow positions itself bottom-right in its own Loaded handler, registered first.
        toast.Loaded += (_, _) => toast.Top -= slot * (toast.Height + 10);
        toast.Closed += (_, _) => Open.Remove(entry);
        Open.Add(entry);

        toast.Show();
        toast.AutoClose(Lifetime);
    }
}
