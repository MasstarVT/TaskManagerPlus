# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8): a Summary dashboard, per-subsystem CPU/Memory/Storage/Network/
Energy & Thermals tabs (live charts, real sensors), Processes, Services,
Startup manager, System specs, and a live color-theming system. Navigation
is a TMOG-style top horizontal tab strip (`TabStripPlacement="Top"`), not a
left sidebar rail — this is a deliberate redesign away from the earlier
iStat-Menus-style rail, matching the visual/IA style of
[tmog.org](https://tmog.org). Theming supports six families (Dark/Light/
Green/Amber/Blue/Monochrome "phosphor" palettes) plus an adjustable
saturation slider, on top of the existing per-metric accent colors. CPU
topology (NUMA/P-core-E-core), a Windows-native memory breakdown, and real
temperature/fan/voltage/power sensors (via LibreHardwareMonitorLib) round
out the TMOG/HWiNFO-inspired depth — see the CPU/Memory/Energy & Thermals
sections below for the details and known limitations of each. A global
Start/Stop Logging control in the footer status bar writes every metric
to a CSV, one row per second, the same "log everything" approach HWiNFO's
own logging feature uses — see the Logging section below.

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

### Memory breakdown (Available/Committed/Cached)

Windows has no macOS-style "wired"/"compressed" memory concept, so the
Memory tab's breakdown uses Windows' own real terms instead of inventing
categories that don't exist: **Available** (physical free, from
`GlobalMemoryStatusEx.ullAvailPhys` — already read for `RamUsedBytes`, just
not previously surfaced), **Committed** (`Memory\Committed Bytes` /
`Memory\Commit Limit` PerfCounters — the same overall memory-pressure
figure Task Manager's own UI calls "Committed"), and **Cached**
(`Memory\Cache Bytes`). All three are plain instantaneous PerfCounter
gauges added to `HardwareMonitorService` alongside the existing ones, no
new dependency. `PerformanceViewModel.CommittedSeries`/`MemoryBytesYAxes`
follow the same glow+core `LineOf` chart pattern as Cpu/Ram/Disk, just on a
byte scale instead of 0–100%; `Committed` shares RAM's theme color rather
than getting its own `ThemeColors` field. `Common/Formatting.cs` (new)
holds the two byte-formatting helpers that were previously near-duplicated
in `PerformanceViewModel` (rate, `".../s"`) and `SystemSpecsViewModel`
(capacity, `"Unknown"` fallback) — both now call the shared
`FormatBytes`/`FormatByteRate`.

### Energy & Thermals (real sensors via LibreHardwareMonitorLib)

Temperature, fan speed, voltage, and wattage have no reliable Windows API -
WMI's `Win32_TemperatureProbe`/`MSAcpi_ThermalZoneTemperature` are
unimplemented by most OEM firmware, which is why every other tab avoids
them. This tab is the one place the app takes a third-party dependency for
data: **LibreHardwareMonitorLib** (NuGet, MPL-2.0 licensed - a separate
license family from the rest of this MIT-licensed project, worth knowing if
distribution terms ever come up). `Services/SensorMonitorService.cs` wraps
`LibreHardwareMonitor.Hardware.Computer` (Cpu/Gpu/Motherboard/Memory/Storage
enabled), opens it in a try/catch that never throws past the class
(`IsAvailable` goes `false` instead - driver load can fail under Smart App
Control the same way CLAUDE.md already documents for unsigned local
builds), and flattens `Sensors` from the whole hardware/sub-hardware tree
into `List<SensorReading>` each poll.

`EnergyThermalsViewModel` is the **one** tab-level ViewModel besides the
original five (Processes/Performance/Services/Startup/SystemSpecs) that
owns its own `DispatcherTimer` - unlike Cpu/Memory/Storage/Network, it's
not derived from `HardwareMonitorService`'s data, so it doesn't fit the
"share one sampler" pattern those four use. It polls at 1.5s (sensor
enumeration is heavier than a flat `PerformanceCounter` read, so it
shouldn't run faster than the other tabs' 1s), and clears+rebuilds its
`Temperatures`/`Fans`/`Voltages`/`Wattages` collections each tick rather
than merging in place - these are read-only display lists with no
selection/scroll state to preserve, the same simpler pattern
`SystemSpecsViewModel` already uses for its spec lists.

Sensor names are **not standardized across CPU vendors** (Intel: "CPU
Package"; AMD: "Core (Tctl/Tdie)"; varies further by model) - headline
readings (`CpuPackageTempC`, `TotalPackagePowerW`) go through
`FindByNameContains`, trying a few known name hints in order rather than
one brittle exact-string lookup. Expect sparse or missing fan/voltage data
on many systems (depends on motherboard Super I/O chip support) - the view
renders empty sections gracefully rather than assuming every sensor type
exists. Per-process power impact is **deliberately not shown** - there's
no public, stable Windows API for it, and Task Manager's own figure comes
from a private ETW heuristic; showing a fabricated per-process wattage
would misrepresent real telemetry.

### CPU topology (NUMA node + P-core/E-core)

`Services/CpuTopologyService.Query()` is a one-time (not per-tick) call to
the Win32 `GetLogicalProcessorInformationEx` API — there's no WMI
equivalent for NUMA membership or P-core/E-core (`EfficiencyClass`)
per logical processor. It reads the returned buffer's variable-length,
unioned native structs at fixed byte offsets (documented in the file)
rather than trying to `Marshal.PtrToStructure` an exact mirror of the
version-dependent layout — deliberately the highest-risk interop in the
app, so any failure degrades to `CpuTopologySnapshot.Flat` (every core on
node 0, no P/E distinction) instead of throwing. `PerformanceViewModel`
queries it once in its constructor and exposes `HasHybridTopology`/
`HasMultipleNumaNodes`; `CoreUsage.NumaNode`/`IsPCore` are set once when a
core tile is first created in `SyncCores`, not reassigned per tick.
`CpuViewModel.CoresByNumaNode` groups `Performance.Cores` by `NumaNode` for
the CPU tab's per-core `VfdMeter` grid, which shows "NUMA Node N" headers
only when `HasMultipleNumaNodes` and a P-core/E-core `SubText` tag only
when `HasHybridTopology` — both hidden on ordinary single-node,
non-hybrid CPUs, which is nearly every desktop the app will run on.

Getting `HardwareMonitorService`'s existing per-core `PerformanceCounter`
array (`CpuPerCorePercent[i]`) to line up 1:1 with this topology's logical
index required fixing two pre-existing bugs in how its `"Processor
Information"` counter instances were selected: the `_Total` filter didn't
catch per-NUMA-node aggregates like `"0,_Total"` (only the literal
`"_Total"`), and instance names were sorted as strings (`"0,10"` before
`"0,2"`) instead of numerically by `(node, core)`. Both are fixed in
`HardwareMonitorService`'s constructor now.

### Logging (HWiNFO-style CSV)

`Services/LoggingService.cs` is a thin wrapper around an open `StreamWriter`
(`Start`/`WriteRow`/`Stop`) - it just writes whatever rows it's given, no
polling of its own. `ViewModels/LoggingViewModel.cs` is the piece that
decides *what* goes in each row: it owns its own 1s `DispatcherTimer` but
never samples hardware itself - it reads already-polled state straight off
`PerformanceViewModel`/`EnergyThermalsViewModel` (both are already ticking
independently), so logging never adds a third redundant poller.

Clicking "Start Logging" (footer status bar, visible from every tab) opens
a `Microsoft.Win32.SaveFileDialog` defaulted to
`%AppData%\TaskManagerPlus\Logs\TaskManagerPlus-<timestamp>.csv`, then
**snapshots the column set** at that moment: CPU total/clock + one column
per current logical core, the memory breakdown, disk, network, and one
column per current Energy & Thermals sensor reading (temps/fans/voltages/
power - same `SensorReading.Identifier` used to look each one up on later
ticks). This snapshot is deliberate - the column set is fixed for the
lifetime of one log file (same as HWiNFO), so if a sensor list or core
count ever changed mid-session, later rows just leave that cell blank
rather than silently reshaping the CSV's columns partway through. Values
are culture-invariant (`CultureInfo.InvariantCulture`) so the CSV parses
the same regardless of the machine's regional settings.

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
