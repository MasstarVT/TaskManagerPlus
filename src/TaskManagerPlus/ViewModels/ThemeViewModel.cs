using System.Windows.Media;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Owns the user's color choices: applies the accent color and the active
/// theme family/saturation to the WPF resource dictionary live (so every
/// DynamicResource-bound control repaints instantly) and raises
/// <see cref="ColorsChanged"/>/<see cref="ThemeModeChanged"/> so the
/// Performance charts can restyle themselves, since they live outside WPF's
/// resource system (SkiaSharp paints).
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

    /// <summary>Theme-family names, in the order shown in the Colors panel.</summary>
    public static readonly string[] ThemeModes = { "Dark", "Light", "Green", "Amber", "Blue", "Monochrome" };

    /// <summary>
    /// Base palettes for each theme family. Every color here is repainted into
    /// the app's resource dictionary by <see cref="ApplyPalette"/> - nothing in
    /// XAML hardcodes these values beyond the Dark entry (which mirrors
    /// Dark.xaml's original palette so the app looks unchanged by default).
    /// </summary>
    private static readonly Dictionary<string, PaletteDefinition> Palettes = new()
    {
        ["Dark"] = new PaletteDefinition(
            Bg: C("#17171A"), BgPanel: C("#1E1E22"), BgElevated: C("#26262B"), BgHover: C("#303038"),
            Border: C("#33333A"), BorderSubtle: C("#26262B"),
            TextPrimary: C("#F2F2F3"), TextSecondary: C("#9A9AA2"), TextTertiary: C("#6B6B72"),
            Success: C("#3DD68C"), Warning: C("#F5B942"),
            Danger: C("#F0546A"), DangerHover: C("#FF6E82"), DangerMuted: C("#3D2530")),

        ["Light"] = new PaletteDefinition(
            Bg: C("#F5F5F7"), BgPanel: C("#FFFFFF"), BgElevated: C("#ECECEF"), BgHover: C("#E2E2E7"),
            Border: C("#D6D6DC"), BorderSubtle: C("#E8E8EC"),
            TextPrimary: C("#1A1A1D"), TextSecondary: C("#5B5B63"), TextTertiary: C("#8A8A92"),
            Success: C("#1FA85C"), Warning: C("#B9790A"),
            Danger: C("#D93A52"), DangerHover: C("#C22D44"), DangerMuted: C("#FBE3E7")),

        ["Green"] = new PaletteDefinition(
            Bg: C("#050B06"), BgPanel: C("#0A140B"), BgElevated: C("#10200F"), BgHover: C("#173016"),
            Border: C("#1E3A1C"), BorderSubtle: C("#122414"),
            TextPrimary: C("#B8FFB0"), TextSecondary: C("#6FCB63"), TextTertiary: C("#3E8A3A"),
            Success: C("#7CFF6E"), Warning: C("#D9FF3D"),
            Danger: C("#FF5C4D"), DangerHover: C("#FF7A6C"), DangerMuted: C("#331A14")),

        ["Amber"] = new PaletteDefinition(
            Bg: C("#0B0704"), BgPanel: C("#150E08"), BgElevated: C("#211610"), BgHover: C("#2E1E14"),
            Border: C("#3C2A18"), BorderSubtle: C("#1C130C"),
            TextPrimary: C("#FFD9A0"), TextSecondary: C("#D89B4E"), TextTertiary: C("#8F6530"),
            Success: C("#8CFF6E"), Warning: C("#FFC93D"),
            Danger: C("#FF5C4D"), DangerHover: C("#FF7A6C"), DangerMuted: C("#331A14")),

        ["Blue"] = new PaletteDefinition(
            Bg: C("#0A0E17"), BgPanel: C("#101725"), BgElevated: C("#182233"), BgHover: C("#212E42"),
            Border: C("#2A3A54"), BorderSubtle: C("#162032"),
            TextPrimary: C("#D6E7FF"), TextSecondary: C("#8FA9CC"), TextTertiary: C("#5A7191"),
            Success: C("#3DD68C"), Warning: C("#F5B942"),
            Danger: C("#F0546A"), DangerHover: C("#FF6E82"), DangerMuted: C("#2A1E30")),

        ["Monochrome"] = new PaletteDefinition(
            Bg: C("#141414"), BgPanel: C("#1B1B1B"), BgElevated: C("#242424"), BgHover: C("#2E2E2E"),
            Border: C("#383838"), BorderSubtle: C("#242424"),
            TextPrimary: C("#F2F2F2"), TextSecondary: C("#9E9E9E"), TextTertiary: C("#6E6E6E"),
            Success: C("#C6C6C6"), Warning: C("#DCDCDC"),
            Danger: C("#EAEAEA"), DangerHover: C("#FFFFFF"), DangerMuted: C("#3A3A3A")),
    };

    public event Action? ColorsChanged;

    /// <summary>
    /// Raised when the theme family or saturation changes, in addition to
    /// <see cref="ColorsChanged"/> - lets listeners that only care about the
    /// SkiaSharp axis text/gridline colors (which don't ride WPF's resource
    /// system) avoid re-subscribing to two events for the same concern.
    /// </summary>
    public event Action? ThemeModeChanged;

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

    private string _themeMode = "Dark";
    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (_themeMode == value || !Palettes.ContainsKey(value)) return;
            _themeMode = value;
            OnPropertyChanged();
            ApplyPalette(_themeMode, _saturation);
            if (!_isLoading) NotifyThemeModeChangedAndPersist();
        }
    }

    private double _saturation = 1.0;
    public double Saturation
    {
        get => _saturation;
        set
        {
            value = Math.Clamp(value, 0.0, 2.0);
            if (Math.Abs(_saturation - value) < 0.001) return;
            _saturation = value;
            OnPropertyChanged();
            ApplyPalette(_themeMode, _saturation);
            if (!_isLoading) NotifyThemeModeChangedAndPersist();
        }
    }

    public IReadOnlyList<Color> PresetColors => Presets;
    public IReadOnlyList<string> ThemeModeNames => ThemeModes;

    public RelayCommand ResetCommand { get; }
    public RelayCommand SetAccentCommand { get; }
    public RelayCommand SetCpuCommand { get; }
    public RelayCommand SetRamCommand { get; }
    public RelayCommand SetDiskCommand { get; }
    public RelayCommand SetNetworkReceiveCommand { get; }
    public RelayCommand SetNetworkSendCommand { get; }
    public RelayCommand SetThemeModeCommand { get; }

    public ThemeViewModel()
    {
        ResetCommand = new RelayCommand(_ => ResetToDefaults());
        SetAccentCommand = new RelayCommand(p => Accent = (Color)p!);
        SetCpuCommand = new RelayCommand(p => Cpu = (Color)p!);
        SetRamCommand = new RelayCommand(p => Ram = (Color)p!);
        SetDiskCommand = new RelayCommand(p => Disk = (Color)p!);
        SetNetworkReceiveCommand = new RelayCommand(p => NetworkReceive = (Color)p!);
        SetNetworkSendCommand = new RelayCommand(p => NetworkSend = (Color)p!);
        SetThemeModeCommand = new RelayCommand(p => ThemeMode = (string)p!);

        var saved = ThemeService.Load();
        _isLoading = true;
        Accent = ParseOrDefault(saved.Accent, Presets[0]);
        Cpu = ParseOrDefault(saved.Cpu, Presets[0]);
        Ram = ParseOrDefault(saved.Ram, Presets[1]);
        Disk = ParseOrDefault(saved.Disk, Presets[3]);
        NetworkReceive = ParseOrDefault(saved.NetworkReceive, Presets[2]);
        NetworkSend = ParseOrDefault(saved.NetworkSend, Presets[5]);
        _themeMode = Palettes.ContainsKey(saved.ThemeMode) ? saved.ThemeMode : "Dark";
        _saturation = Math.Clamp(saved.Saturation, 0.0, 2.0);
        _isLoading = false;

        ApplyPalette(_themeMode, _saturation);
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
        _themeMode = d.ThemeMode;
        _saturation = d.Saturation;
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(Saturation));
        _isLoading = false;

        ApplyPalette(_themeMode, _saturation);
        NotifyAndPersist();
        ThemeModeChanged?.Invoke();
    }

    private void SetColor(ref Color field, Color value, bool applyToWpfResources = false)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(null); // cheap: just refresh all bindings on this small view model

        if (applyToWpfResources)
            ApplyAccentToResources(ColorMath.AdjustSaturation(value, _saturation));

        if (!_isLoading)
            NotifyAndPersist();
    }

    private void NotifyAndPersist()
    {
        ColorsChanged?.Invoke();
        Persist();
    }

    private void NotifyThemeModeChangedAndPersist()
    {
        ThemeModeChanged?.Invoke();
        Persist();
    }

    private void Persist()
    {
        ThemeService.Save(new ThemeColors
        {
            Accent = ToHex(Accent),
            Cpu = ToHex(Cpu),
            Ram = ToHex(Ram),
            Disk = ToHex(Disk),
            NetworkReceive = ToHex(NetworkReceive),
            NetworkSend = ToHex(NetworkSend),
            ThemeMode = ThemeMode,
            Saturation = Saturation,
        });
    }

    /// <summary>
    /// Repaints every base-palette brush in the app's resource dictionary for
    /// the given theme family and saturation, then reapplies the (also
    /// saturation-adjusted) accent on top so it stays visually consistent.
    /// </summary>
    private void ApplyPalette(string themeMode, double saturation)
    {
        if (!Palettes.TryGetValue(themeMode, out var p))
            p = Palettes["Dark"];

        Color Adj(Color c) => ColorMath.AdjustSaturation(c, saturation);

        _appResources["BgBrush"] = Frozen(Adj(p.Bg));
        _appResources["BgPanelBrush"] = Frozen(Adj(p.BgPanel));
        _appResources["BgElevatedBrush"] = Frozen(Adj(p.BgElevated));
        _appResources["BgHoverBrush"] = Frozen(Adj(p.BgHover));
        _appResources["BorderBrush2"] = Frozen(Adj(p.Border));
        _appResources["BorderSubtleBrush"] = Frozen(Adj(p.BorderSubtle));

        _appResources["TextPrimaryBrush"] = Frozen(Adj(p.TextPrimary));
        _appResources["TextSecondaryBrush"] = Frozen(Adj(p.TextSecondary));
        _appResources["TextTertiaryBrush"] = Frozen(Adj(p.TextTertiary));

        _appResources["SuccessBrush"] = Frozen(Adj(p.Success));
        _appResources["WarningBrush"] = Frozen(Adj(p.Warning));
        _appResources["DangerBrush"] = Frozen(Adj(p.Danger));
        _appResources["DangerHoverBrush"] = Frozen(Adj(p.DangerHover));
        _appResources["DangerMutedBrush"] = Frozen(Adj(p.DangerMuted));

        // Keep the user's chosen accent visually consistent with the new family/saturation.
        if (!_isLoading)
            ApplyAccentToResources(Adj(_accent));
    }

    private void ApplyAccentToResources(Color accent)
    {
        var hover = ColorMath.Lighten(accent, 0.18);
        var muted = Color.FromArgb(0x46, accent.R, accent.G, accent.B);
        var foreground = ColorMath.RelativeLuminance(accent) > 0.55 ? Color.FromRgb(0x10, 0x14, 0x1A) : Color.FromRgb(0xF5, 0xF6, 0xF8);

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

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

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

    private sealed record PaletteDefinition(
        Color Bg, Color BgPanel, Color BgElevated, Color BgHover, Color Border, Color BorderSubtle,
        Color TextPrimary, Color TextSecondary, Color TextTertiary,
        Color Success, Color Warning, Color Danger, Color DangerHover, Color DangerMuted);
}
