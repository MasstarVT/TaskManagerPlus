# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8). Thirteen top-level tabs (Summary, CPU, Memory, Storage, Network, GPU,
Energy & Thermals, Responsiveness, Processes, Services, Startup, System Specs,
Stability), live color-theming (six palette families + saturation + high-contrast +
color-blind-safe alerts), a CSV/HTML/Markdown logging & reporting system, a
system tray icon with a global hotkey, and an optional LAN-visible remote
monitor endpoint. Navigation is a TMOG-style top horizontal tab strip
(`TabStripPlacement="Top"`), matching the visual/IA style of
[tmog.org](https://tmog.org), not a left sidebar rail.

The project has gone through many incremental rounds of feature additions
(see `suggestions.md` for the backlog and git history for the detailed
per-round rationale) — this file documents the resulting architecture and
the conventions those rounds converged on, not a round-by-round changelog.

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
  `ServiceController`, registry, native interop, shelled-out Windows
  tools). No UI dependencies; safe to reason about in isolation. Each is
  typically static or has no ties to a ViewModel.
- **ViewModels/** — one per tab. Most tabs (`SummaryViewModel`,
  `CpuViewModel`, `MemoryViewModel`, `StorageViewModel`, `NetworkViewModel`)
  are thin compositions over one shared `PerformanceViewModel` instance
  (passed in via constructor) rather than new polling sources — CPU/Memory/
  Storage/Network are split into separate top-level tabs (matching TMOG's
  per-subsystem IA) but all come from one `HardwareMonitorService.Sample()`
  call per tick, so giving each its own timer would mean redundant
  `PerformanceCounter` instantiation for identical data. `CpuViewModel` also
  owns one small timer of its own (thermal-throttle/power-limit flags need
  both `Performance` and `EnergyThermals` data) and takes `ProcessesViewModel`
  (core-affinity heatmap) and `EnergyThermalsViewModel` references.
  `ProcessesViewModel`, `ServicesViewModel`, `EnergyThermalsViewModel`, and
  `GpuViewModel` each own their own `DispatcherTimer` and poll independently
  — `EnergyThermalsViewModel` and `GpuViewModel` don't fit the shared-sampler
  pattern because sensor/GPU-engine enumeration is a genuinely separate,
  heavier data source than the fixed `HardwareMonitorService` counter array.
  `StartupViewModel`, `SystemSpecsViewModel`, and `StabilityViewModel` are
  on-demand instead (an initial load plus a manual Refresh command, no
  timer) since their underlying queries (registry/WMI inventory sweeps,
  event-log scans) aren't cheap enough to repeat on a tick.
  `LoggingViewModel` owns a timer but samples nothing itself — it just reads
  already-polled state off `PerformanceViewModel`/`EnergyThermalsViewModel`.
  `ResponsivenessViewModel` (the Responsiveness tab, lag/stutter/freeze
  diagnostics) is the one ViewModel that deliberately mixes both cadences at
  once rather than picking one: a cheap always-on `_lightTimer` (2s) for
  syscall/registry/perf-counter reads that are fine on a tick, plus several
  independent on-demand Start/Stop sessions (DPC/ISR capture, present
  monitor, VBlank jitter, input-latency probe, flight recorder) for anything
  ETW-backed or otherwise too heavy to run unconditionally — see its own
  file-header remarks for the full list of sub-services it composes.
  `MainViewModel` composes all of them plus settings-drawer state, tray/
  hotkey wiring, and elevation status (checked once via `WindowsPrincipal`).
- **Views/** — XAML + minimal code-behind per tab, hosted in `MainWindow.xaml`'s
  `TabControl`. `MeterTile` and `VfdMeter` are small reusable UserControls
  (colored dot/title, big value, colored bar/segmented LED bar) sharing the
  same `Title`/`ValueText`/`SubText`/`Percent`/`AccentBrush` DP surface, so
  either is close to a drop-in for the other, plus a right-click "Copy value"
  context menu. `VfdMeter` is the TMOG-style "dense glowing digital readout"
  variant (monospace value, phosphor drop-shadow, segmented bar); prefer it
  for new tiles.
- **Common/** — `ObservableObject` (minimal `INotifyPropertyChanged` base),
  `RelayCommand` (`ICommand` implementation), `ColorMath` (HSL saturation
  adjustment), `Formatting` (shared byte/rate formatting helpers) — the
  entire "MVVM framework" for this project, intentionally hand-rolled.
- **Converters/** — value converters for display formatting (bytes,
  percentages, status colors, bool→text, color↔brush/hex, percent→width, ...).
- **Themes/Dark.xaml** — the (only) app theme resource dictionary; also
  defines the re-templated `TabControl` (top icon+label strip) and shared
  `DataGrid`/`TabItem` styles.

Data flow per polling tab: a `DispatcherTimer` on the ViewModel ticks →
calls into a `Services/*` class (often via `Task.Run` to keep WMI/perf-
counter calls off the UI thread) → merges results into an
`ObservableCollection` in place (update-existing/remove-stale/add-new, see
`ProcessesViewModel.MergeInto`) rather than clearing and rebuilding it, so
`DataGrid` selection and scroll position survive each refresh. Read-only
display lists with no selection state (e.g. Energy & Thermals' sensor
lists) instead clear+rebuild each tick — simpler, and there's nothing to
preserve.

Cross-tab coupling is deliberately thin: `MainViewModel` wires
`ThemeViewModel.ColorsChanged`/`ThemeModeChanged` to `PerformanceViewModel`
so charts stay in sync with accent colors and theme family (SkiaSharp
paints live outside WPF's resource system and can't repaint via
`DynamicResource` alone). Settings-drawer / cross-view-model bindings that
need to reach a sibling ViewModel from a control whose own `DataContext` is
something else use `{Binding DataContext.X, RelativeSource={RelativeSource
AncestorType=Window}}` (or `UserControl`) rather than new plumbing.

### Configurable poll intervals

`PollIntervalSettingsService` (`poll-intervals.json`) backs a
`PollIntervalSeconds` property on the four ViewModels that actually own a
`DispatcherTimer` in the polling sense: `ProcessesViewModel`, the shared
`PerformanceViewModel` (one knob for CPU/Memory/Storage/Network too),
`ServicesViewModel`, `EnergyThermalsViewModel`. On-demand ViewModels
(Startup/SystemSpecs/Stability) have no interval to configure.

### UI shell (top tab strip, icons, footer, tray)

`MainWindow.xaml`'s `TabControl` is re-templated (in `Themes/Dark.xaml`) as
a TMOG-style top icon+label tab strip — `TabStripPlacement="Top"` gets the
horizontal layout for free, and the strip scrolls horizontally so all tabs
stay reachable at narrower widths. Each `TabItem.Tag` holds a small
hand-drawn `Viewbox`/`Canvas` glyph styled via `NavIconStroke`/
`NavIconFill` (bound to the ancestor `TabItem.Foreground`, so icons recolor
automatically with the theme). The "Colors" button and other footer
controls live in `TabControl.Tag`, which the template docks to the trailing
edge of the strip — keeps `Dark.xaml` generic while `MainWindow.xaml`
supplies real bindings. A slim footer status bar under the tab body shows
live process count/uptime and the Start/Stop Logging control. A
`NotifyIcon`-based system tray icon (`System.Windows.Forms`, pulled in via
`<UseWindowsForms>true</UseWindowsForms>` alongside `<UseWPF>true</UseWPF>`
in the csproj — the two frameworks' implicit usings collide on `Color`/
`Brush`/etc., resolved with `<Using Remove>` entries) supports
minimize-to-tray and a global Ctrl+Alt+T hotkey (`GlobalHotkeyService`,
`RegisterHotKey` via a `HwndSource` message hook). Ctrl+1..9 jump to a tab
by matching `TabItem.Header` text against `MainViewModel.TabShortcutOrder`,
not a hardcoded index, so it survives tab reordering.

### Theming (families + saturation, on top of per-metric accent colors)

`ThemeViewModel` owns two layers: per-metric accent colors (`Accent`/`Cpu`/
`Ram`/`Disk`/`NetworkReceive`/`NetworkSend`) and a theme-family +
saturation layer (`ThemeMode`: Dark/Light/Green/Amber/Blue/Monochrome/High
Contrast, `Saturation`: 0–2, plus an independent `ColorBlindSafeAlerts`
toggle that overrides just the status colors). `ApplyPalette(mode,
saturation)` runs every color through `ColorMath.AdjustSaturation` and
overwrites the base palette brush keys directly in
`Application.Current.Resources` — the same "mutate the resource dictionary
entry" trick used for accent brushes and for `CompactRows`'s row-height
resources. **This only works because every consumer references these
brushes via `DynamicResource`, not `StaticResource` — if you add a new
view, use `DynamicResource` for any base palette brush key, or it won't
re-theme.** `ThemeViewModel.FontScale` doesn't touch font sizes directly
(most views hardcode `FontSize`); instead it drives a `ScaleTransform` via
`LayoutTransform` on the whole `TabControl`.

### Chart styling (glow + gradient)

`PerformanceViewModel.LineOf` and friends build each metric as a **pair**
of `LineSeries<double>` sharing one `ObservableCollection<double>`: a
thick, translucent "glow" stroke drawn first, then a crisp 2px "core"
stroke with a top-to-bottom gradient fill on top. Every history chart in
the app (CPU/RAM/Disk/Network, Committed memory, CPU/motherboard/fan
temperature, ...) follows this same pattern; `Recolor`/`ApplyColors`
update both series of a pair together. Fan RPM-vs-temperature uses a
`ScatterSeries` instead (no glow/core pairing — a point cloud, not a line).
Reliability History (Stability tab) uses `ColumnSeries` — the only bar
chart in the app, since a discrete daily count reads better as bars.

## Cross-cutting conventions

These recur across almost every feature added after the original five
tabs — worth knowing before adding something new rather than re-deriving
per file:

- **Prefer a known Windows tool/API over raw interop or an undocumented
  struct layout.** `schtasks.exe`, `sc.exe`, `vssadmin.exe`, `fsutil.exe`,
  `defrag.exe`, `tracert.exe`, `powercfg.exe`, `netsh` are all shelled out
  to and their text output parsed, rather than reimplementing what they do
  via COM/registry/native structs. Raw P/Invoke (`CpuTopologyService`,
  `NetworkConnectionsService`'s `GetExtendedTcpTable`, the process-
  environment PEB walk, the system-wide handle-table walk) is reserved for
  cases with no tool or WMI class available at all, and is always wrapped
  to degrade gracefully rather than throw or hang (e.g. `NtQueryObject`
  calls run on an abandoned background thread with a strict timeout, since
  it's known to occasionally hang forever on certain handle types).
- **Degrade to Unknown/0/hidden — never fabricate.** A denied registry key,
  an absent WMI namespace, or an unsupported sensor means the feature shows
  "Unknown" or hides its section/card entirely (the same pattern
  `SensorMonitorService`, `ReadTpmStatus`, the Battery section, and the
  Storage Spaces card all use), not a guessed or zeroed-out value presented
  as real.
- **"Quick flag, not a verdict."** Several heuristics are explicitly
  documented (in code comments and in the UI) as informational only, not
  authoritative: process signature checks only see embedded Authenticode
  signatures (not catalog signing), high-privilege/duplicate-instance/
  memory-leak/thermal-throttle/power-limit flags are pattern-matches on
  otherwise-ambiguous data, AV/mitigation-status reads use undocumented
  bitmask/registry conventions, and outdated-driver/BIOS-age checks are
  "worth a manual check," not a confirmed update available.
- **Settings persistence**: every persisted setting is a small JSON file
  under `AppPaths.SettingsDirectory` (`%AppData%\TaskManagerPlus` normally,
  or a `Settings` folder next to the exe in portable mode — see
  `Services/AppPaths.cs`, initialized once from `App.xaml.cs` before any
  other service runs). Each settings file fails silently to its type's
  defaults on a missing/corrupt file, same as `ThemeService`/`theme.json`.
- **On-demand vs. polled**: anything that takes more than a trivial
  registry/perf-counter read (event-log scans, recursive file-system
  walks, registry-tree sweeps, network calls) is gated behind an explicit
  button, never added to a per-tick timer.
- **ETW without a tracing library**: several Responsiveness-tab services
  (`DpcLatencyService`, `PresentMonitorService`, `HardFaultEtwService`,
  `WprCaptureService`'s circular-capture mode) need real-time kernel/ETW
  data but deliberately avoid the `Microsoft.Diagnostics.Tracing.TraceEvent`
  NuGet package, per the "prefer a known tool" rule taken one step further —
  they shell out to `logman`/`wpr` to capture a short `.etl`, convert it with
  `tracerpt -of XML`, and parse the result *leniently* (event/field names
  matched by substring, not an exact schema, since the classic MOF-based
  event layout tracerpt renders isn't a stable public contract). A build
  that doesn't match any expected field just parses zero events — an empty
  grid with an explanatory message, never a crash or fabricated numbers.
  Start/Stop-gated (never a timer), and every one of these checks tool
  presence (`ToolsAvailable`/`IsAvailable`) before attempting a capture.

## Notable implementation details

- **CPU clock speed**: not directly exposed by Windows. Computed the same
  way the real Task Manager does — read the CPU's rated base clock once via
  WMI (`Win32_Processor.MaxClockSpeed`), then each tick read the
  `% Processor Performance` counter and multiply (reflects turbo/throttling).
- **Startup enable/disable**: doesn't touch the registry Run value or move
  the shortcut. Flips the binary flag under
  `...\Explorer\StartupApproved\Run` (or `\StartupApproved\StartupFolder`)
  that Explorer itself checks — kept consistent with Explorer/Task Manager.
- **Sensors (temperature/fan/voltage/power)**: no reliable Windows API
  exists for these, so `SensorMonitorService` is the one place the app
  takes a third-party dependency (LibreHardwareMonitorLib, MPL-2.0 —
  a different license family from the rest of this MIT-licensed project).
  Sensor names aren't standardized across vendors, so headline readings are
  found via a name-hint search (`FindByNameContains`), not an exact lookup.
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
