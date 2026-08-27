using System.Windows;
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

    /// <summary>Theme-family names, in the order shown in the Colors panel. Round 11, #83 adds
    /// "High Contrast" as a 7th family, following the exact same PaletteDefinition mechanism as
    /// the original six - no new architecture, just another table entry.</summary>
    public static readonly string[] ThemeModes = { "Dark", "Light", "Green", "Amber", "Blue", "Monochrome", "High Contrast" };

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

        // Round 11, #83: accessibility-focused variant - pure black background, near-white text,
        // and status colors deliberately chosen for a high contrast ratio against that background
        // (all comfortably past WCAG AA's 4.5:1 body-text threshold) rather than reusing any other
        // family's more muted tones. Saturation is still applied on top like every other family
        // (ApplyPalette doesn't special-case this one) - a user who wants both high contrast and,
        // say, ColorBlindSafeAlerts (#76) can combine the two, since that toggle overrides the
        // Success/Warning/Danger brushes independently of whichever family is active.
        ["High Contrast"] = new PaletteDefinition(
            Bg: C("#000000"), BgPanel: C("#0A0A0A"), BgElevated: C("#141414"), BgHover: C("#232323"),
            Border: C("#8A8A8A"), BorderSubtle: C("#4A4A4A"),
            TextPrimary: C("#FFFFFF"), TextSecondary: C("#E6E6E6"), TextTertiary: C("#C0C0C0"),
            Success: C("#4AFF7A"), Warning: C("#FFE24A"),
            Danger: C("#FF5C5C"), DangerHover: C("#FF8080"), DangerMuted: C("#4A1414")),
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

    // #76: color-blind-safe status colors (blue/yellow/orange instead of green/amber/red) - a
    // deuteranopia/protanopia-safe triple, applied on top of whichever theme family/saturation is
    // active, since red/green are exactly the colors this app's diagnostic UI leans on throughout.
    private static readonly Color CbSafeSuccess = Color.FromRgb(0x00, 0x72, 0xB2); // blue
    private static readonly Color CbSafeWarning = Color.FromRgb(0xF0, 0xE4, 0x42); // yellow
    private static readonly Color CbSafeDanger = Color.FromRgb(0xE6, 0x9F, 0x00);  // orange
    private static readonly Color CbSafeDangerHover = Color.FromRgb(0xFF, 0xB6, 0x33);
    private static readonly Color CbSafeDangerMuted = Color.FromArgb(0x46, 0xE6, 0x9F, 0x00);

    private bool _colorBlindSafeAlerts;
    public bool ColorBlindSafeAlerts
    {
        get => _colorBlindSafeAlerts;
        set
        {
            if (_colorBlindSafeAlerts == value) return;
            _colorBlindSafeAlerts = value;
            OnPropertyChanged();
            ApplyPalette(_themeMode, _saturation);
            if (!_isLoading) NotifyThemeModeChangedAndPersist();
        }
    }

    // Round 11, #78: dense/compact DataGrid row height - swaps two small resource values
    // (row height, cell padding) the same "mutate the resource dictionary" way ApplyPalette
    // already repaints colors, so every DataGrid across the app (Processes/Services/Startup/...)
    // picks it up live via the DynamicResource bindings Dark.xaml's DataGrid/DataGridCell styles
    // already use for their RowHeight/Padding setters.
    private bool _compactRows;
    public bool CompactRows
    {
        get => _compactRows;
        set
        {
            if (_compactRows == value) return;
            _compactRows = value;
            OnPropertyChanged();
            ApplyRowHeight();
            if (!_isLoading) NotifyAndPersist();
        }
    }

    // Round 11, #79: independent UI scale, separate from Windows' own display scaling. A literal
    // "font size" slider would need to be threaded through every explicit FontSize setter across
    // dozens of XAML files (most cards hardcode FontSize rather than inheriting it) - a much more
    // invasive change than this app's established "one resource-dictionary hook, everything
    // DynamicResource-bound repaints" pattern allows for text alone. Instead this drives a
    // LayoutTransform on the main window's tab content (MainWindow.xaml) that scales the whole UI
    // uniformly - text, tiles, charts, grids all get bigger/smaller together, which is the
    // practical goal ("make the whole app easier to read") even though it's a layout scale rather
    // than a font-metric change.
    private double _fontScale = 1.0;
    public double FontScale
    {
        get => _fontScale;
        set
        {
            value = Math.Clamp(value, 0.8, 1.5);
            if (Math.Abs(_fontScale - value) < 0.001) return;
            _fontScale = value;
            OnPropertyChanged();
            if (!_isLoading) NotifyAndPersist();
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

    // Round 11, #82: export/import just the accent/family/saturation subset of theme.json, as its
    // own small shareable file - distinct from theme.json itself (which this app already persists
    // automatically) and from a full settings/config export, since a user sharing "here's my color
    // scheme" with someone else has no reason to also hand over their alert thresholds or logging
    // preferences.
    public RelayCommand ExportPaletteCommand { get; }
    public RelayCommand ImportPaletteCommand { get; }

    private string _paletteStatusText = string.Empty;
    public string PaletteStatusText { get => _paletteStatusText; private set => SetProperty(ref _paletteStatusText, value); }

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
        ExportPaletteCommand = new RelayCommand(_ => ExportPalette());
        ImportPaletteCommand = new RelayCommand(_ => ImportPalette());

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
        _colorBlindSafeAlerts = saved.ColorBlindSafeAlerts;
        _compactRows = saved.CompactRows;
        _fontScale = Math.Clamp(saved.FontScale <= 0 ? 1.0 : saved.FontScale, 0.8, 1.5);
        _isLoading = false;

        ApplyPalette(_themeMode, _saturation);
        ApplyRowHeight();
    }

    /// <summary>Repaints the DataGrid row-height/cell-padding resources for the current
    /// CompactRows setting - the same "mutate the app resource dictionary" trick ApplyPalette uses
    /// for colors, just for two layout values instead.</summary>
    private void ApplyRowHeight()
    {
        _appResources["DataGridRowHeightValue"] = _compactRows ? 24.0 : 34.0;
        _appResources["DataGridCellPaddingValue"] = _compactRows ? new Thickness(10, 1, 10, 1) : new Thickness(10, 4, 10, 4);
    }

    /// <summary>Round 11, #82: writes just the palette subset of theme.json to a file the user
    /// picks - a small, shareable "here's my color scheme" file distinct from the app's full
    /// theme.json (which is persisted automatically and covers logging/remote-monitor prefs too
    /// in spirit, even though technically theme.json itself only ever held color/theme fields).</summary>
    private void ExportPalette()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export color palette",
            Filter = "Palette files (*.tmpalette.json)|*.tmpalette.json|All files (*.*)|*.*",
            DefaultExt = ".tmpalette.json",
            FileName = $"TaskManagerPlus-Palette-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.tmpalette.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var preset = new PalettePreset
            {
                Accent = ToHex(Accent), Cpu = ToHex(Cpu), Ram = ToHex(Ram), Disk = ToHex(Disk),
                NetworkReceive = ToHex(NetworkReceive), NetworkSend = ToHex(NetworkSend),
                ThemeMode = ThemeMode, Saturation = Saturation, ColorBlindSafeAlerts = ColorBlindSafeAlerts,
            };
            System.IO.File.WriteAllText(dialog.FileName,
                System.Text.Json.JsonSerializer.Serialize(preset, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            PaletteStatusText = $"Palette exported: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            PaletteStatusText = $"Couldn't export palette: {ex.Message}";
        }
    }

    private void ImportPalette()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import color palette",
            Filter = "Palette files (*.tmpalette.json;*.json)|*.tmpalette.json;*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = System.IO.File.ReadAllText(dialog.FileName);
            var preset = System.Text.Json.JsonSerializer.Deserialize<PalettePreset>(json);
            if (preset is null) { PaletteStatusText = "Couldn't read that palette file."; return; }

            _isLoading = true;
            Accent = ParseOrDefault(preset.Accent, Accent);
            Cpu = ParseOrDefault(preset.Cpu, Cpu);
            Ram = ParseOrDefault(preset.Ram, Ram);
            Disk = ParseOrDefault(preset.Disk, Disk);
            NetworkReceive = ParseOrDefault(preset.NetworkReceive, NetworkReceive);
            NetworkSend = ParseOrDefault(preset.NetworkSend, NetworkSend);
            _themeMode = Palettes.ContainsKey(preset.ThemeMode) ? preset.ThemeMode : _themeMode;
            _saturation = Math.Clamp(preset.Saturation, 0.0, 2.0);
            _colorBlindSafeAlerts = preset.ColorBlindSafeAlerts;
            OnPropertyChanged(nameof(ThemeMode));
            OnPropertyChanged(nameof(Saturation));
            OnPropertyChanged(nameof(ColorBlindSafeAlerts));
            _isLoading = false;

            ApplyPalette(_themeMode, _saturation);
            NotifyAndPersist();
            ThemeModeChanged?.Invoke();
            PaletteStatusText = $"Palette imported: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _isLoading = false;
            PaletteStatusText = $"Couldn't import palette: {ex.Message}";
        }
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
        _colorBlindSafeAlerts = d.ColorBlindSafeAlerts;
        _compactRows = d.CompactRows;
        _fontScale = d.FontScale;
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(Saturation));
        OnPropertyChanged(nameof(ColorBlindSafeAlerts));
        OnPropertyChanged(nameof(CompactRows));
        OnPropertyChanged(nameof(FontScale));
        _isLoading = false;

        ApplyPalette(_themeMode, _saturation);
        ApplyRowHeight();
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
            ColorBlindSafeAlerts = ColorBlindSafeAlerts,
            CompactRows = CompactRows,
            FontScale = FontScale,
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

        if (_colorBlindSafeAlerts)
        {
            // #76: deliberately NOT saturation-adjusted - these three are chosen specifically for
            // their distinguishability under deuteranopia/protanopia, and running them through the
            // same saturation slider as the rest of the palette could undermine that.
            _appResources["SuccessBrush"] = Frozen(CbSafeSuccess);
            _appResources["WarningBrush"] = Frozen(CbSafeWarning);
            _appResources["DangerBrush"] = Frozen(CbSafeDanger);
            _appResources["DangerHoverBrush"] = Frozen(CbSafeDangerHover);
            _appResources["DangerMutedBrush"] = Frozen(CbSafeDangerMuted);
        }
        else
        {
            _appResources["SuccessBrush"] = Frozen(Adj(p.Success));
            _appResources["WarningBrush"] = Frozen(Adj(p.Warning));
            _appResources["DangerBrush"] = Frozen(Adj(p.Danger));
            _appResources["DangerHoverBrush"] = Frozen(Adj(p.DangerHover));
            _appResources["DangerMutedBrush"] = Frozen(Adj(p.DangerMuted));
        }

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
