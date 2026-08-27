using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskManagerPlus.Views;

/// <summary>
/// Custom lightweight "toast" popup for threshold alerts (#72) - a borderless, always-on-top
/// window rather than a true Windows Action Center toast, since Windows' native toast API needs a
/// package identity (AppUserModelID/MSIX) this app's classic .exe deployment doesn't have. Shows
/// itself bottom-right of the primary screen's work area and closes itself after a few seconds.
/// </summary>
public partial class ToastWindow : Window
{
    public ToastWindow(string title, string message, bool isCritical)
    {
        InitializeComponent();

        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        if (isCritical && Application.Current.Resources["DangerBrush"] is Brush danger)
            Dot.Background = danger;

        Loaded += (_, _) => PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;
    }

    /// <summary>Auto-dismisses after the given duration - called by ToastService right after Show().</summary>
    public void AutoClose(TimeSpan after)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { Close(); } catch { /* already closed by the user */ }
        };
        timer.Start();
    }
}
