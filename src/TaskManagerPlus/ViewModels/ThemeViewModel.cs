using System.Windows.Media;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Owns the user's color choices: applies the accent color to the WPF resource
/// dictionary live (so every DynamicResource-bound control repaints instantly)
/// and raises <see cref="ColorsChanged"/> so the Performance tab can restyle
/// its charts, which live outside WPF's resource system (SkiaSharp paints).
/// </summary>
public sealed class ThemeViewModel : ObservableObject
{
    public static readonly Color[] Presets =
    {
        Color.FromRgb(0x3F, 0xA7, 0xFF), // blue
        Color.FromRgb(0xB1, 0x8C, 0xFF), // purple
        Color.FromRgb(0x3D, 0xD6, 0x8C), // green
        Color.FromRgb(0xFF, 0xA5, 0x3F), // orange
        Color.FromRgb(0xFF, 0x6F, 0xA8), // pink
        Color.FromRgb(0xF0, 0x54, 0x6A), // red
        Color.FromRgb(0x33, 0xD6, 0xC0), // teal
        Color.FromRgb(0xF5, 0xD1, 0x42), // yellow
        Color.FromRgb(0x46, 0xD1, 0xFF), // cyan
        Color.FromRgb(0x9A, 0xA0, 0xA6), // gray
    };

    public event Action? ColorsChanged;

    private readonly System.Windows.ResourceDictionary _appResources = System.Windows.Application.Current.Resources;
    private bool _isLoading;

    private Color _accent;
    public Color Accent { get => _accent; set => SetColor(ref _accent, value, applyToWpfResources: true); }

    private Color _cpu;
    public Color Cpu { get => _cpu; set => SetColor(ref _cpu, value); }

    private Color _ram;
    public Color Ram { get => _ram; set => SetColor(ref _ram, value); }

    private Color _disk;
    public Color Disk { get => _disk; set => SetColor(ref _disk, value); }

    private Color _networkReceive;
    public Color NetworkReceive { get => _networkReceive; set => SetColor(ref _networkReceive, value); }

    private Color _networkSend;
    public Color NetworkSend { get => _networkSend; set => SetColor(ref _networkSend, value); }

    public IReadOnlyList<Color> PresetColors => Presets;

    public RelayCommand ResetCommand { get; }
    public RelayCommand SetAccentCommand { get; }
    public RelayCommand SetCpuCommand { get; }
    public RelayCommand SetRamCommand { get; }
    public RelayCommand SetDiskCommand { get; }
    public RelayCommand SetNetworkReceiveCommand { get; }
    public RelayCommand SetNetworkSendCommand { get; }

    public ThemeViewModel()
    {
        ResetCommand = new RelayCommand(_ => ResetToDefaults());
        SetAccentCommand = new RelayCommand(p => Accent = (Color)p!);
        SetCpuCommand = new RelayCommand(p => Cpu = (Color)p!);
        SetRamCommand = new RelayCommand(p => Ram = (Color)p!);
        SetDiskCommand = new RelayCommand(p => Disk = (Color)p!);
        SetNetworkReceiveCommand = new RelayCommand(p => NetworkReceive = (Color)p!);
        SetNetworkSendCommand = new RelayCommand(p => NetworkSend = (Color)p!);

        var saved = ThemeService.Load();
        _isLoading = true;
        Accent = ParseOrDefault(saved.Accent, Presets[0]);
        Cpu = ParseOrDefault(saved.Cpu, Presets[0]);
        Ram = ParseOrDefault(saved.Ram, Presets[1]);
        Disk = ParseOrDefault(saved.Disk, Presets[3]);
        NetworkReceive = ParseOrDefault(saved.NetworkReceive, Presets[2]);
        NetworkSend = ParseOrDefault(saved.NetworkSend, Presets[5]);
        _isLoading = false;

        ApplyAccentToResources(Accent);
    }

    private void ResetToDefaults()
    {
        var d = ThemeColors.Defaults;
        _isLoading = true;
        Accent = ParseOrDefault(d.Accent, Presets[0]);
        Cpu = ParseOrDefault(d.Cpu, Presets[0]);
        Ram = ParseOrDefault(d.Ram, Presets[1]);
        Disk = ParseOrDefault(d.Disk, Presets[3]);
        NetworkReceive = ParseOrDefault(d.NetworkReceive, Presets[2]);
        NetworkSend = ParseOrDefault(d.NetworkSend, Presets[5]);
        _isLoading = false;

        ApplyAccentToResources(Accent);
        NotifyAndPersist();
    }

    private void SetColor(ref Color field, Color value, bool applyToWpfResources = false)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(null); // cheap: just refresh all bindings on this small view model

        if (applyToWpfResources)
            ApplyAccentToResources(value);

        if (!_isLoading)
            NotifyAndPersist();
    }

    private void NotifyAndPersist()
    {
        ColorsChanged?.Invoke();
        ThemeService.Save(new ThemeColors
        {
            Accent = ToHex(Accent),
            Cpu = ToHex(Cpu),
            Ram = ToHex(Ram),
            Disk = ToHex(Disk),
            NetworkReceive = ToHex(NetworkReceive),
            NetworkSend = ToHex(NetworkSend),
        });
    }

    private void ApplyAccentToResources(Color accent)
    {
        var hover = Lighten(accent, 0.18);
        var muted = Color.FromArgb(0x46, accent.R, accent.G, accent.B);
        var foreground = RelativeLuminance(accent) > 0.55 ? Color.FromRgb(0x10, 0x14, 0x1A) : Color.FromRgb(0xF5, 0xF6, 0xF8);

        _appResources["AccentBrush"] = Frozen(accent);
        _appResources["AccentHoverBrush"] = Frozen(hover);
        _appResources["AccentMutedBrush"] = Frozen(muted);
        _appResources["AccentForegroundBrush"] = Frozen(foreground);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color c, double amount)
    {
        byte L(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * amount, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    private static double RelativeLuminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseOrDefault(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c) return c;
        }
        catch
        {
            // fall through
        }
        return fallback;
    }
}
