# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8): a Summary dashboard, per-subsystem CPU/Memory/Storage/Network tabs
(live charts), Processes, Services, Startup manager, System specs, and a
live color-theming system. Navigation is a TMOG-style top horizontal tab
strip (`TabStripPlacement="Top"`), not a left sidebar rail — this is a
deliberate redesign away from the earlier iStat-Menus-style rail, matching
the visual/IA style of [tmog.org](https://tmog.org). Theming supports six
families (Dark/Light/Green/Amber/Blue/Monochrome "phosphor" palettes) plus
an adjustable saturation slider, on top of the existing per-metric accent
colors. Further TMOG/HWiNFO-inspired depth (CPU topology, memory breakdown,
real sensors) is being layered in a staged plan — see the CPU/Memory/
Energy & Thermals sections below for what's landed vs. still planned.

## Commands

```bash
# Run (Debug) — requires admin; you'll get a UAC prompt
dotnet run --project src/TaskManagerPlus

# Build
dotnet build
dotnet build -c Release

# Release exe output
src/TaskManagerPlus/bin/Release/net8.0-windows/TaskManagerPlus.exe
```

There is no test project and no lint/format config in this repo — don't
invent test or lint commands.

Alternative: open `TaskManagerPlus.sln` in Visual Studio/Rider and Run.
VS's debugger runs elevated automatically if VS itself is elevated;
otherwise the UAC prompt appears the same as `dotnet run`.

### Signing local builds (optional, for Smart App Control / "Unknown Publisher")

```powershell
.\scripts\New-DevCertificate.ps1   # one-time: creates + trusts a self-signed cert
dotnet build -c Release
.\scripts\Sign-Release.ps1
```

## Architecture

Standard MVVM, no framework dependency (no CommunityToolkit.Mvvm, no DI
container — everything is `new`'d directly). Layers:

- **Models/** — plain data classes bound to the UI (`ProcessRow`,
  `ServiceRow`, `StartupItem`, `SystemSpecs`, `ThemeColors`, ...).
- **Services/** — all system interaction (WMI, performance counters,
  `ServiceController`, registry). No UI dependencies; safe to reason about
  in isolation. Each is typically static or has no ties to a ViewModel.
- **ViewModels/** — one per tab (`ProcessesViewModel`, `PerformanceViewModel`,
  `ServicesViewModel`, `StartupViewModel`, `SystemSpecsViewModel`,
  `ThemeViewModel`), each owns its own `DispatcherTimer` for polling and
  disposes it in `Dispose()`. `SummaryViewModel`, `CpuViewModel`,
  `MemoryViewModel`, `StorageViewModel`, and `NetworkViewModel` are the
  exception — each is a thin composition over the single shared
  `PerformanceViewModel` instance (passed in via constructor), not a new
  polling source. CPU/Memory/Storage/Network are split into separate
  top-level tabs (matching TMOG's per-subsystem IA) but all still come from
  one `HardwareMonitorService.Sample()` call per tick; giving each its own
  timer would mean redundant `PerformanceCounter` instantiation for
  identical data, so keep new tabs like this thin instead of adding a
  sampler per tab. `MainViewModel` composes all of them plus settings-drawer
  state (`IsSettingsOpen`) and elevation status (`IsElevated`, checked once
  via `WindowsPrincipal`).
- **Views/** — XAML + minimal code-behind per tab, hosted in `MainWindow.xaml`'s
  `TabControl` (see "UI shell" below). `CpuView`/`MemoryView`/`StorageView`/
  `NetworkView` replace the old single `PerformanceView` (deleted). `MeterTile`
  and `VfdMeter` are small reusable UserControls (colored dot/title, big
  value, colored bar/segmented LED bar) for gauge-style tiles — share the
  same `Title`/`ValueText`/`SubText`/`Percent`/`AccentBrush` DP surface, so
  either is close to a drop-in for the other. `VfdMeter` is the TMOG-style
  "dense glowing digital readout" variant (monospace value, phosphor
  drop-shadow via `Glow`, segmented bar); prefer it for new tiles, reuse
  `MeterTile` only where you want the plainer continuous-bar look.
- **Common/** — `ObservableObject` (minimal `INotifyPropertyChanged` base)
  and `RelayCommand` (`ICommand` implementation) — the entire "MVVM
  framework" for this project, intentionally hand-rolled rather than
  pulling in a library.
- **Converters/** — value converters for display formatting (bytes,
  percentages, status colors, bool→text, color↔brush/hex).
- **Themes/Dark.xaml** — the (only) app theme resource dictionary.

Data flow per tab: a `DispatcherTimer` on the ViewModel ticks (usually every
1s) → calls into a `Services/*` class (often via `Task.Run` to keep WMI/
perf-counter calls off the UI thread) → merges results into an
`ObservableCollection` in place (update-existing/remove-stale/add-new, see
`ProcessesViewModel.MergeInto`) rather than clearing and rebuilding it, so
`DataGrid` selection and scroll position survive each refresh.

Cross-tab coupling is deliberately thin: `MainViewModel` wires
`ThemeViewModel.ColorsChanged` to `PerformanceViewModel.ApplyColors` (per-metric
accent colors) and `ThemeViewModel.ThemeModeChanged` to
`PerformanceViewModel.ApplyAxisTheme` (chart axis text/gridlines + the
Network chart's legend/tooltip, which are SkiaSharp paints living outside
WPF's resource system and can't repaint via `DynamicResource` alone) so
charts stay in sync with both the user's chosen colors and the active theme
family; otherwise tabs don't know about each other.

### UI shell (top tab strip, icons, footer)

`MainWindow.xaml`'s `TabControl` is re-templated (in `Themes/Dark.xaml`) as a
TMOG-style top icon+label tab strip — `TabStripPlacement="Top"` gets the
horizontal `TabPanel` layout for free, and the strip's `ScrollViewer` scrolls
horizontally so all 10 tabs stay reachable at narrower window widths instead
of wrapping/clipping. No separate nav ViewModel/converters exist; page
switching is still plain `TabItem` selection.

- **Per-tab icons**: each `TabItem.Tag` in `MainWindow.xaml` holds a small
  hand-drawn `Viewbox`/`Canvas` glyph (`Line`/`Ellipse`/`Rectangle`/`Path`),
  styled via the `NavIconStroke`/`NavIconFill` styles declared in
  `MainWindow.xaml`'s `Window.Resources`. Those styles bind `Stroke`/`Fill` to
  `{RelativeSource AncestorType=TabItem}.Foreground`, so icons recolor
  automatically with the existing selected/hover triggers in the `TabItem`
  template — add new tabs the same way rather than hardcoding icon colors.
- **Strip footer**: the "Colors" button lives in `TabControl.Tag`, which the
  `TabControl` template docks to the trailing (right) edge of the strip. This
  keeps `Dark.xaml` generic (it doesn't know about `ToggleSettingsCommand`)
  while letting `MainWindow.xaml` supply real app bindings. Add further
  footer entries the same way (a `StackPanel` inside `TabControl.Tag`)
  rather than editing the template.
- **Footer status bar**: a slim bar under the tab body (`MainWindow.xaml`,
  bottom `Grid.Row`) shows live process count / uptime, pulled straight from
  `Processes`/`Performance` — no new state.

### Theming (families + saturation, on top of per-metric accent colors)

`ThemeViewModel` now owns two layers: the original per-metric accent colors
(`Accent`/`Cpu`/`Ram`/`Disk`/`NetworkReceive`/`NetworkSend`, unchanged) and a
new theme-family + saturation layer (`ThemeMode`: Dark/Light/Green/Amber/
Blue/Monochrome, `Saturation`: 0–2). `ApplyPalette(mode, saturation)` looks
up a `PaletteDefinition` from its internal `Palettes` table, runs every
color through `ColorMath.AdjustSaturation` (an HSL round-trip in
`Common/ColorMath.cs`), and overwrites the base palette brush keys
(`BgBrush`, `BorderBrush2`, `TextPrimaryBrush`, etc.) directly in
`Application.Current.Resources` — the same "mutate the resource dictionary
entry" trick `ApplyAccentToResources` already used for just the accent
brushes. This only works because every consumer (`Dark.xaml` and every
`Views/*.xaml`) references these brushes via `DynamicResource`, not
`StaticResource` — **if you add a new view, use `DynamicResource` for any of
the base palette brush keys, or it won't re-theme.** The underlying `Color`
resources at the top of `Dark.xaml` (`BgColor`, etc.) are otherwise unused
now — brushes are repainted directly, not derived from them at runtime.

### Chart styling (glow + gradient)

`PerformanceViewModel.LineOf` builds each metric (CPU/RAM/Disk/Network
receive/send) as a **pair** of `LineSeries<double>` sharing the same
`ObservableCollection<double>`: a thick, translucent "glow" stroke
(`IsHoverable=false`, `IsVisibleAtLegend=false`) drawn first, then a crisp
2px "core" stroke on top with a top-to-bottom `LinearGradientPaint` area
fill. `CpuSeries`/`RamSeries`/`DiskSeries` are therefore 2-element
`ISeries[]` arrays and `NetworkSeries` is 4 elements
(recv-glow, recv-core, send-glow, send-core) — `Recolor`/`ApplyColors` update
both series of a pair together. `CpuView.xaml`/`MemoryView.xaml`/
`StorageView.xaml`/`NetworkView.xaml`/`SummaryView.xaml` just bind to the
`ISeries[]` as before and don't need to know about the pair.

### Notable implementation details

- **CPU clock speed**: not directly exposed by Windows. Computed the same
  way the real Task Manager does — read the CPU's rated base clock once via
  WMI (`Win32_Processor.MaxClockSpeed`), then each tick read the
  `% Processor Performance` counter and multiply (reflects turbo/throttling).
- **Startup enable/disable**: doesn't touch the registry Run value or move
  the shortcut. Flips the binary flag under
  `...\Explorer\StartupApproved\Run` (or `\StartupApproved\StartupFolder`)
  that Explorer itself checks — kept consistent with Explorer/Task Manager.
- **Theme persistence**: `ThemeService` loads/saves `ThemeColors` as JSON to
  `%AppData%\TaskManagerPlus\theme.json`, failing silently (falls back to
  `ThemeColors.Defaults`) on a missing/corrupt file so a bad settings file
  never blocks startup.
- **Elevation**: the whole app runs elevated (`app.manifest` →
  `requireAdministrator`) rather than elevating per-action, so ending other
  users' processes and controlling services just work without extra
  prompts.

## Local dev environment notes

- **Smart App Control** (Windows Security → App & browser control), if On,
  blocks locally-built unsigned exes from launching at all (instant crash,
  no window — `FileLoadException` / exit code `0xE0434352`). Signing with
  `scripts/New-DevCertificate.ps1` + `Sign-Release.ps1` fixes the "Unknown
  Publisher" label but does **not** satisfy Smart App Control, which blocks
  on reputation, not signature validity — turning SAC off (Windows Security,
  one-way without a full OS reset) is the only real fix for running unsigned
  local builds.
- A running Debug build locks its exe; build/run a Release build in
  parallel instead of waiting for the Debug instance to exit. Elevated app
  processes can't be killed from a non-elevated shell.
