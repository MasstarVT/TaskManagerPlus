using TaskManagerPlus.Views;

namespace TaskManagerPlus.Services;

/// <summary>Shows the custom toast popup (#72) - see ToastWindow's remarks for why this is a
/// hand-rolled window rather than a native Windows toast.</summary>
public static class ToastService
{
    private static readonly List<ToastWindow> Open = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    public static void Show(string title, string message, bool isCritical = false)
    {
        var toast = new ToastWindow(title, message, isCritical);
        // Stack additional toasts above whichever ones are already showing so they don't overlap.
        toast.Loaded += (_, _) => toast.Top -= Open.Count * (toast.Height + 10);
        toast.Closed += (_, _) => Open.Remove(toast);
        Open.Add(toast);

        toast.Show();
        toast.AutoClose(Lifetime);
    }
}
