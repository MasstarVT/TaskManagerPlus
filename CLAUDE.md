# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8): a Summary dashboard, per-subsystem CPU/Memory/Storage/Network/GPU/
Energy & Thermals tabs (live charts, real sensors), Processes, Services,
Startup manager, System specs, a Stability tab (event-log-based crash/
TDR/minidump diagnostics), and a live color-theming system. Navigation
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
  sampler per tab. `CpuViewModel` and `StorageViewModel` also take a shared
  `EnergyThermalsViewModel` reference now (Round 4) — `Cpu` for its
  composite thermal-throttle flag (needs both `Performance` and
  `EnergyThermals` data, so it owns one small timer of its own to recompute
  it; see the CPU section below), `Storage` purely to re-filter
  `EnergyThermals.Temperatures` down to Storage hardware, no new polling.
  `StabilityViewModel` follows `SystemSpecsViewModel`'s on-demand pattern
  instead (an initial load plus a manual Refresh command, no timer) since an
  event-log query isn't cheap enough to repeat on a tick. `GpuViewModel`
  (Round 10, the GPU tab) is the newest exception to the "thin wrapper"
  shape — like `EnergyThermalsViewModel`, it owns its own `GpuMonitorService`
  and `DispatcherTimer` rather than riding `PerformanceViewModel`'s shared
  sampler, since "GPU Engine"/"GPU Adapter Memory" perf-counter instance
  enumeration is a genuinely separate, heavier data source than a fixed
  counter array. `MainViewModel` composes all of them plus settings-drawer
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
horizontally so all 12 tabs (11 plus Round 10's new GPU tab) stay reachable
at narrower window widths instead of wrapping/clipping. No separate nav
ViewModel/converters exist; page switching is still plain `TabItem`
selection.

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

### Process diagnostics (handles, signature, command line, high-privilege, recently-started)

`ProcessMonitorService` samples a few more fields per process than raw
CPU/memory, aimed specifically at "what's wrong with this PC" triage:
**handle count** (`Process.HandleCount`, cheap, read every tick like thread
count), **command line** (not exposed by `Process` at all — fetched via WMI
`Win32_Process.CommandLine` and cached per-pid, since a running process's
command line never changes after launch — same caching shape as the
existing owner-name cache), and **signature status** ("Signed"/"Unsigned"/
"Unknown", via `X509Certificate.CreateFromSignedFile`, cached per **file
path** rather than per-pid since many processes share one executable). The
signature check is a real, documented limitation: it only sees an embedded
Authenticode signature, not a catalog signature, so a handful of legitimate
Windows system binaries that rely on catalog signing will show as
"Unsigned" — a full WinVerifyTrust check would need native interop this
app doesn't otherwise take on, so this is a "quick visual flag, not a
security verdict" tradeoff, the same kind CpuTopologyService documents for
its native interop. **High-privilege** (`IsHighPrivilege`) just flags the
three well-known service accounts (SYSTEM/LOCAL SERVICE/NETWORK SERVICE)
rather than attempting real token/group inspection, and tints those rows in
`ProcessesView`'s `DataGrid.RowStyle`. **Recently started** is a
`ProcessesViewModel.RecentlyStartedOnly` toggle layered into the existing
`ProcessesView.Filter` predicate (5-minute window); since `MergeInto` only
implicitly re-filters on add/remove, `RefreshAsync` explicitly calls
`ProcessesView.Refresh()` each tick while the toggle is on so rows still
age back out of view even when nothing else in the process list changed.

### Storage bottleneck diagnostics (queue length + latency)

`HardwareMonitorService` adds three more `PhysicalDisk` counters alongside
the existing `% Disk Time`/read/write-bytes ones: `Avg. Disk Queue Length`
(requests waiting, not just active — a classic "is the disk really the
bottleneck" signal beyond raw throughput) and `Avg. Disk sec/Read`/
`Avg. Disk sec/Write` (per-I/O latency, converted from seconds to ms since
that's the unit anyone diagnosing "is my disk slow" actually thinks in).
All three are instantaneous gauges like the Memory tab's counters, so they
get primed the same way. `PerformanceViewModel` derives a `...GaugePercent`
for each (`DiskQueueLengthGaugePercent`, `DiskReadLatencyGaugePercent`,
`DiskWriteLatencyGaugePercent`) — these have no natural 0–100 range the way
a percentage does, so the percent is a rough "how concerning is this
reading" fill for the `VfdMeter` segmented bar (queue length ≥8 or latency
≥50ms = full bar), not an exact value; the numeric readout next to it is
still the real number. Shown as a "Bottleneck diagnostics" `VfdMeter` row
on the Storage tab, following the same pattern as the Memory tab's
Available/Committed/Cached breakdown row.

### Disk health + per-volume free space (System Specs tab)

`SystemSpecsService.ReadDisks` now also reports a health badge per
physical disk: primarily `Win32_DiskDrive.Status` ("OK" vs. anything else),
cross-checked against the SMART failure-prediction flag from the legacy
`root\wmi` `MSStorageDriver_FailurePredictStatus` class where available.
That class is keyed by an `InstanceName` that's a normalized (lowercased,
spaces→underscores) **prefix** of `Win32_DiskDrive.PNPDeviceID`, not an
exact match, so matching is a prefix scan (`ReadFailurePredictStatus`
returns a list, not a dictionary) — and the whole lookup is wrapped to
degrade to "Unknown"/`Win32_DiskDrive.Status` alone when it fails, since
NVMe and some AHCI/RAID drivers don't implement it at all (the same
graceful-degradation shape `SensorMonitorService` uses for its LibreHardwareMonitor
dependency). `SystemSpecsView`'s `SpecRowTemplate` renders this as a small
colored badge next to the disk model name (green/OK vs. red/warning),
reusing the existing green/amber/red visual language rather than adding a
new one.

A separate **Volumes** card lists per-drive-letter free space via
`DriveInfo.GetDrives()` (not WMI — it already handles removable/unready
drives cleanly through `IsReady`), with a thin progress bar per volume
color-coded by the existing `PercentToBrushConverter` (turns red at ≥85%
used) — a nearly-full system volume is an easy-to-miss, common slowdown/
crash cause that the old Storage tab (which only ever showed *aggregate*
disk activity, not free space) had no way to surface.

### Copy value to clipboard (MeterTile / VfdMeter)

Both shared tile controls (`Views/MeterTile.xaml(.cs)`,
`Views/VfdMeter.xaml(.cs)`) now carry a right-click "Copy value" context
menu that puts `"Title: ValueText (SubText)"` (VfdMeter also folds in
`Unit`) on the clipboard — since the two controls are the shared building
block for nearly every gauge across Summary/CPU/Memory/Storage/Network/
Energy & Thermals, this one addition covers pasting any reading into a
forum post or support ticket app-wide, with no per-tab wiring needed.

### Memory diagnostics (page file, top consumers)

`HardwareMonitorService` adds a page file gauge alongside the existing
Committed/Cache counters: total size comes from WMI
`Win32_PageFileUsage.AllocatedBaseSize` (summed across all configured page
files, read once — it only changes on a resize) and live usage from
`Paging File\% Usage\_Total` (a live PerformanceCounter, multiplied back
into bytes), the same "WMI for the rarely-changing total, PerformanceCounter
for the live rate/percent" split the CPU clock speed calculation already
uses. The Memory tab's "Page file" tile deliberately binds its
`AccentBrush` to `PercentToBrushConverter` instead of the fixed RAM accent
color the other three breakdown tiles use — a nearly-full page file is a
real, distinct memory-pressure warning worth calling out visually. "Top
memory consumers" re-presents the Processes tab's already-polling
`ObservableCollection` — `MemoryViewModel` now takes `ProcessesViewModel` in
its constructor (mirroring `SummaryViewModel`'s existing "Top CPU
processes" card) and exposes a second `ICollectionView` over the same
`Processes` collection, live-sorted by `MemoryBytes` instead of
`CpuPercent`. No new process sampling.

### Network diagnostics (adapter errors, gateway/DNS reachability)

`HardwareMonitorService.ReadNetworkErrorCounters` sums each active
adapter's `IPInterfaceStatistics` CRC/framing-error and dropped-packet
counts (`IncomingPacketsWithErrors`, `IncomingPacketsDiscarded`, and their
Outgoing counterparts) the same way `ReadTotalNetworkBytes` already sums
throughput — these are cumulative counters since the adapter/driver last
loaded, not per-second rates, so no delta math is needed; any nonzero total
already flags a problem. The Network tab's "Adapter errors" card tints
itself the warning color when `PerformanceViewModel.HasNetworkErrors` is
true, otherwise renders as a normal card.

Gateway/DNS reachability is the one deliberate exception to "Network tab
is a thin PerformanceViewModel wrapper": answering it needs actual ICMP
ping I/O (up to ~1.2s per check), unlike every other figure on the tab
which is a local counter read, so it doesn't belong on the 1s shared
sampler tick. `Services/NetworkDiagnosticsService.cs` pings the first
active adapter's default gateway and a fixed public DNS resolver
(1.1.1.1); `NetworkViewModel` now owns its own slow (15s) `DispatcherTimer`
for this alone plus a manual "Check now" button — the same kind of
narrow, documented exception `EnergyThermalsViewModel` already carries for
LibreHardwareMonitorLib. `NetworkViewModel` is `IDisposable` now (stops
that timer); `MainViewModel.Dispose()` calls it. A `false` result means
"didn't respond to ping", not definitively "unreachable" — ICMP being
blocked by a firewall is a known, real limitation, not a bug.

### Services diagnostics ("failed to start" — a false-positive lesson)

The obvious first implementation of "flag services that should be running
but aren't" — `StartMode == Automatic && State != Running` — was tried
against a real machine and rejected: delayed-auto-start and "Automatic
(Trigger Start)" services (`WbioSrvc`, `MapsBroker`, most vendor updater
services, ...) are legitimately stopped most of the time by design, and
dominated the result with false positives. `Win32_Service.ExitCode` from
each service's last start attempt is the actually-reliable signal instead
— a delayed/trigger-start service that simply hasn't started yet reports
`ExitCode 0`, identical to a normal clean stop, so a *nonzero* exit code
means an automatic service genuinely tried to start and failed.
`ServiceControlService.ReadServiceExitCodes` reads this via WMI alongside
the existing PID lookup; `ServiceRow.HasFailedToStart` combines it with
`StartType == Automatic`. The Services tab tints a failed row and adds a
"Failed to start only" filter checkbox, following the same
`DataGrid.RowStyle` + `ICollectionView.Filter` patterns already used
elsewhere (Processes tab's high-privilege tint and "Recently started"
toggle).

### System Specs: security posture, install age, outdated drivers

Three more `SystemSpecsService` reads, all optional/nullable by design
since each data source can legitimately be unavailable:

- **Secure Boot**: read straight from the registry
  (`HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`)
  rather than calling the `Confirm-SecureBootUEFI`-equivalent native API,
  which needs elevation this app's own process doesn't always satisfy for
  that specific check.
- **TPM**: `Win32_Tpm` in the `root\cimv2\security\microsofttpm` WMI
  namespace (present/enabled/owned/activated → a single `TpmReady` bool,
  plus `SpecVersion`). This one genuinely does need this app's existing
  elevation, and can still be denied by a stricter local policy even
  elevated — wrapped to return "Unknown" rather than a false "absent".
- **VBS (Core Isolation / Memory Integrity)**: `Win32_DeviceGuard` in
  `root\Microsoft\Windows\DeviceGuard`
  (`VirtualizationBasedSecurityStatus == 2` means running;
  `SecurityServicesRunning` is decoded against the documented enum —
  Credential Guard, HVCI, System Guard Secure Launch, SMM Firmware
  Measurement).

**OS install age**: `ReadOperatingSystem` already parsed
`Win32_OperatingSystem.InstallDate` to a `DateTime` before formatting it
down to a display string — `OsInstallAgeDays` just keeps that `DateTime`
around one extra step to compute `(DateTime.Now - installDate).TotalDays`
before it's discarded, appended to the existing OS details line as
"(412 days ago)".

**Outdated third-party drivers** (`ReadOutdatedDrivers`, `Win32_PnPSignedDriver`)
went through two rounds of false-positive filtering discovered by querying
a real machine, both documented in the method's own comment: (1) most
in-box/class drivers report a `DriverVersion` tied to the current OS build
but a `DriverDate` frozen at the classic Windows placeholder date
(2006-06-21) even when perfectly current, so `DeviceClass` is restricted
to an allowlist of categories where a stale *third-party* driver is a
plausible troubleshooting lead (`Display`, `Net`, `HDC`, `SCSIAdapter`,
`Media`, `Monitor`, `USB`, `Bluetooth`, `Image`, `Printer`); (2) that alone
still let some "Generic ..."/"Standard ..." manufacturer entries with that
same 2006 date through, so `Manufacturer` excludes anything containing
"Microsoft", "Generic", or "Standard", on top of an outright exclusion of
`DriverDate.Year <= 2006`. What's left is flagged only past a 2-year-old
bar and capped at 20 rows, oldest first — deliberately conservative to
keep the list short and trustworthy. The System tab collapses this card
entirely when the list is empty (the common, expected case), unlike
Memory/Graphics/Storage which show a "none detected" line — an empty
outdated-driver list isn't noteworthy the way an empty disk list would be.

### Battery health (Energy & Thermals)

`SensorMonitorService` now also enables `IsBatteryEnabled` on the
LibreHardwareMonitorLib `Computer` object — on any desktop this simply
reports no Battery hardware, so the whole "Battery" section on the Energy
& Thermals tab collapses itself via a `Battery.Count == 0` trigger, the
same pattern the System tab uses for its outdated-drivers card.
`EnergyThermalsViewModel.Battery` buckets by `HardwareType.Battery`
instead of a single `SensorType` the way Temperatures/Fans/Voltages/
Wattages do, because battery sensors mix several types (`Level` for
charge %, `Level` again for "Degradation Level" — LibreHardwareMonitorLib's
own full-vs-design-capacity wear calculation and the closest thing to a
real battery-health figure this app can show without a laptop-vendor API,
`Voltage`, `Power` for charge/discharge rate) — a new
`Converters/SensorTypeToUnitConverter.cs` maps each tile's `SensorType` to
its display unit at bind time instead. Also not zero-filtered like the
other four sections: 0% charge or 0 W (idle, on AC) are normal battery
readings, unlike a temperature/voltage/wattage sensor reading exactly 0
(which usually means "unsupported" — see the zero-filtering comment in
`EnergyThermalsViewModel.RefreshAsync`).

### Round 3: CPU/memory/network deep diagnostics, process ancestry, System Specs, Health Check

A third batch of `suggestions.md` items, all following the established
patterns rather than introducing new ones — see each area below.

**CPU (interrupt/DPC time, context switches, queue length, clock vs. rated
spec)**: four more `PerformanceCounter`s in `HardwareMonitorService`
(`"Processor"\% Interrupt|DPC Time` on `"_Total"`, `"System"\Context
Switches/sec`, `"System"\Processor Queue Length`), flowing through
`HardwareSnapshot` to `PerformanceViewModel` the same way every other CPU
figure does. `CpuQueueLengthGaugePercent` is a rough "how concerning" fill
(past 2× logical processors) for the `VfdMeter` bar, the same non-exact-value
convention the Storage tab's disk-latency gauges already established.
Session min/max/avg clock speed is a running accumulator in
`PerformanceViewModel.RefreshAsync` (no history buffer, since only the
summary values are shown), and `CpuVsBasePercent` makes the base-vs-current
comparison explicit as a percent delta rather than requiring the user to do
the math between two separate numbers on the CPU tab.

**Memory (page faults, standby list, kernel pool)**: five more instantaneous/
rate counters (`Memory\Page Faults/sec`, `Memory\Pages/sec` as the hard-fault
proxy, the three `Standby Cache *` counters summed into one reclaimable-
memory figure, `Pool Nonpaged|Paged Bytes`), shown as a second "Diagnostics"
`VfdMeter` row below the Memory tab's existing Breakdown row. Soft faults are
derived (`total − hard`, clamped at 0) rather than counted directly — there's
no separate "soft faults" PerfCounter, only a total and a hard-fault proxy.

**Per-process disk I/O and parent process (Processes)**: `GetProcessIoCounters`
(kernel32) is the same interop-risk tier as `CpuTopologyService`'s native
calls — wrapped so an access-denied/exited process just reports a 0 rate
rather than failing the whole sample. The read+write byte total piggybacks
on the same per-pid `CpuSample` bookkeeping the CPU% calculation already
does (same elapsed-time window), rather than a second tracking dictionary.
Parent process ID comes from `Win32_Process.ParentProcessId` via WMI, cached
per-pid like command line and owner already are (it never changes after
launch); the parent's *name* can't be cached the same way since it needs to
be resolved from whichever processes exist in the current batch, so
`ProcessMonitorService.Sample()` does a second pass over the freshly-built
rows to look it up, falling back to `"(exited)"` when the parent is gone -
a lighter-weight alternative to a full indented process-tree view that
still answers "does this process have a parent I didn't expect."

**Network (link speed, TCP retransmits, DNS resolution latency, VPN
detection)**: TCP retransmit rate (`TCPv4\Segments Retransmitted/sec`) is
the one addition here that belongs on the shared 1s sampler like the
existing adapter error counters — `HardwareMonitorService` wraps its
creation in try/catch since the `"TCPv4"` category can legitimately be
missing on an unusual network stack, degrading to "always reports 0" rather
than failing construction. Link speed and VPN detection are both pure
`NetworkInterface` enumeration (no I/O), so they piggyback on
`NetworkViewModel`'s existing slow (15s) connectivity timer instead of
getting a new one; DNS resolution latency extends the same timer's
`CheckAsync` with an actual `Dns.GetHostEntryAsync` call (timed with a
`Stopwatch`) against Windows' own NCSI connectivity-check hostname
(`www.msftconnecttest.com`) — deliberately distinct from the existing ICMP
ping to a resolver IP, which never exercises real name resolution. VPN
detection and the "Gigabit adapter negotiated down" flag are both
heuristics (name/description substring matches, and a speed-vs.-description
comparison respectively) in the same "quick flag, not a verdict" spirit as
the process signature check.

**Dead fan detector and session temperature baseline (Energy & Thermals)**:
both computed in `EnergyThermalsViewModel.RefreshAsync` from data
`SensorMonitorService` already provides each tick, no new sampling. A "dead
fan" is a 0 RPM reading paired with any temperature reading past a "clearly
under load" threshold (55°C) — 0 RPM alone is normal for an idle
semi-passive fan, so it's the *combination* that's the real signal, the
same reasoning the zero-filtering comment for Temperatures/Voltages/
Wattages already documents for a different false-positive. Session min/max
per sensor lives on `SensorReading` itself (`SessionMin`/`SessionMax`,
`init`-only like the rest of the model) rather than a side dictionary the
view has to look up — `EnergyThermalsViewModel` keeps the running
min/max in a private dictionary keyed by `Identifier` and stamps a fresh
copy of each Temperature reading with it every tick.

**Volume dirty bit, Windows Update history, AV detection (System Specs)**:
the dirty bit (`FSCTL_IS_VOLUME_DIRTY` via `DeviceIoControl` on a raw
`\\.\C:`-style volume handle) is the same interop-risk tier as the other
native calls in this app, wrapped to degrade to `null` ("Unknown") rather
than a false "clean" — it can fail even elevated on some volume types
(removable/network drives). Windows Update history
(`Win32_QuickFixEngineering`) follows `ReadOutdatedDrivers`'s exact
try/catch-degrades-to-empty shape, including one wrinkle specific to this
WMI class: `InstalledOn` comes back as a plain culture-formatted date
string, not the usual CIM_DATETIME format the rest of this service parses
with `ManagementDateTimeConverter`, so it needs a direct `DateTime.TryParse`
instead. AV detection reads the undocumented (but widely reverse-engineered)
`productState` bitmask from the `root\SecurityCenter2` namespace — presented
as a best-effort "looks enabled" heuristic in both the code comment and the
UI, the same "quick visual flag, not a security verdict" tradeoff the
process signature check already established, since the bitmask encoding
isn't officially documented by Microsoft.

**Health Check summary card (Summary tab)**: the one feature in this round
that's a pure aggregator — `SummaryViewModel` gained a lightweight 2s
`DispatcherTimer` (previously it owned no timer at all, being a thin
composition over other view-models) that recomputes a rule-based
`ObservableCollection<HealthIssue>` purely by reading state already live on
`Performance`/`EnergyThermals`/`SystemSpecs`/`Services`/`Network` — no new
polling or I/O of its own. This required reordering `MainViewModel`'s
constructor (`Cpu`/`Memory`/`Storage`/`Network`/`Logging` now built before
`Summary`, previously the reverse) so those view-model references exist
before `SummaryViewModel`'s constructor needs them. Each rule is
independent and best-effort: a missing/unavailable data source (e.g. no
sensors, no AV product registered) just means that rule contributes nothing
to the list, never an error state.

### Round 4: Stability tab, thermal-throttle diagnostics, memory leaks, network/process depth

A fourth batch of `suggestions.md` items. The one new top-level tab in this
round (Stability) plus a spread of smaller additions across existing tabs,
all following established patterns rather than introducing new ones.

**Stability tab (event log, minidumps, TDR, unexpected shutdown)**:
`Services/EventLogService.cs` queries the System and Application event logs
via `System.Diagnostics.Eventing.Reader.EventLogReader` (built into the
.NET 8 Windows targeting pack — no extra NuGet package needed), filtered to
Critical/Error (Level 1/2) entries from the last 30 days, capped at 60 per
log. `StabilityViewModel` follows `SystemSpecsViewModel`'s on-demand shape
(initial load + manual Refresh command, no timer) since an event-log query
walks potentially thousands of records and isn't cheap enough to repeat on
a tick. "Last shutdown was unexpected" is detected by finding a
Kernel-Power 41 or legacy EventLog 6008 entry timestamped within 5 minutes
of `DateTime.Now - Environment.TickCount64` (the approximate last boot
time) — a real, if approximate, correlation rather than a guaranteed match.
TDR (GPU driver timeout/reset) events are just a count of event ID 4101 in
the same query. Minidump bugcheck codes are **not** parsed from the raw
`.dmp` binary format (a much larger undertaking, on par with a mini
MINIDUMP-stream reader) — instead, each `%SystemRoot%\Minidump\*.dmp`
file's timestamp is correlated with the nearest Kernel-Power 41 event
(within 10 minutes) to recover the same bugcheck code from that event's
insertion strings, reusing data already read for the shutdown banner. That
insertion-string layout is undocumented and not a versioned contract, so
`ExtractBugcheckCode` degrades to "Unknown" on any parse failure rather
than showing a wrong value.

**CPU thermal-throttle flag**: `CpuViewModel` now takes a shared
`EnergyThermalsViewModel` reference and owns one small 2s `DispatcherTimer`
of its own (unlike every other CPU/Memory/Storage/Network tab, which is a
pure thin wrapper with no timer) — flagging "is the CPU throttling right
now" needs both `Performance.CpuVsBasePercent`/`CpuCurrentPercent` and
`EnergyThermals.CpuPackageTempC`, two view-models that tick on different
intervals (1s vs. 1.5s), so a periodic recompute is simpler and more
robust than wiring cross-object `PropertyChanged` chains between them. This
is a heuristic ("hot AND meaningfully below base clock AND under load"),
not a verified throttle reason — LibreHardwareMonitorLib exposes no
"limit reason" API on most consumer hardware (that's the vendor-proprietary
MSR data HWiNFO reads directly), so a CPU idle-clocked for power-saving
reasons, or throttling for a non-thermal reason, won't necessarily be
flagged correctly. Same tradeoff family as the process signature check and
the outdated-driver date filtering.

**Historical CPU temperature chart + GPU hotspot differential (Energy &
Thermals)**: the temperature chart reuses `PerformanceViewModel.LineOf`'s
glow+core `LineSeries<double>` pattern verbatim (a second history buffer,
`CpuTempHistory`, alongside the existing `PowerHistory`). Throttle
"annotations" are a plain timestamped `ObservableCollection<string>` log
(at most one entry per 30 seconds, capped to the 10 most recent) rather
than an in-chart marker series — deliberately the lower-risk choice, since
this app's LiveChartsCore version's scatter/marker API wasn't something to
gamble a whole feature's compile success on when a readable list conveys
the same "exactly when did this happen" information. GPU hotspot-vs-edge
differential reuses the existing `FindByNameContains` name-hint lookup,
restricted to `HardwareType.GpuAmd`/`GpuNvidia` entries specifically (LHM
0.9.6 has no separate Intel iGPU `HardwareType`) — necessary because sensor
names like "Core" collide with per-core CPU temperature readings otherwise.

**Memory leak heuristic + per-process GPU usage + on-demand module list
(Processes)**: `ProcessMonitorService` gained two more per-pid rolling
buffers alongside the existing CPU/IO sample dictionary — a ~120-sample
(~2 minute) working-set history for the leak flag (`IsLeakSuspect`: true
only when every consecutive sample is non-decreasing across the *entire*
window AND total growth exceeds 50 MB; any single dip disqualifies it,
since a real unbounded leak never gives memory back, unlike GC/cache
churn), and a 10-sample (~10s) CPU% window feeding `CpuPercent10sAvg` for
the Summary tab's new "Top CPU (10s avg)" card. Per-process GPU usage
(`ReadGpuUsageByPid`) reads the same `"GPU Engine"` perf-counter category
Task Manager's own GPU column is built on — instance names look like
`pid_1234_..._engtype_3D`, parsed by regex and summed per pid across every
engine instance a process owns. Unlike the static per-core CPU counters in
`HardwareMonitorService`, GPU engine instances churn constantly as
processes start/stop using the GPU, so `ProcessMonitorService` now
implements `IDisposable` to manage its counter dictionary (created lazily,
pruned when an instance disappears, a newly-seen instance skipped for one
tick per the usual "prime before trusting a rate counter" rule). The
loaded-modules list is a plain on-demand `Process.Modules` read behind a
"View modules" button + `ViewModulesCommand`, not sampled per-tick — same
"expensive, so make it explicit" tradeoff as the event log queries above.

**Active connections, Wi-Fi signal, public IP lookup (Network)**:
`Services/NetworkConnectionsService.cs` lists TCP connections with owning
PID via the native `GetExtendedTcpTable` call (`iphlpapi.dll`) — the same
API `netstat -b` itself is built on, since no managed .NET API exposes a
connection's owning process. Same interop-risk tier as `CpuTopologyService`'s
native calls: wrapped to return an empty list on any failure. Wi-Fi
SSID/signal/channel comes from parsing `netsh wlan show interfaces` text
output rather than the native WLAN API — a deliberately lower-effort
technique with a real, documented limitation: the field labels ("SSID",
"Signal", "Channel", "Radio type") are English-locale text netsh prints, so
this silently returns null (and the Network tab hides the Wi-Fi card, the
same "hidden when not applicable" pattern the Battery section already
uses) on a non-English Windows install. Public IP + ISP lookup
(`PublicIpLookupService`, via ipinfo.io's free JSON endpoint) is the one
feature in this round that makes a real outbound network call, so unlike
everything else on this tab, it deliberately does **not** ride the
existing 15s connectivity timer — it only ever runs from an explicit
"Look up public IP" button click.

**RAM slot population + rated-vs-running speed, memory-compression note
(Memory/System Specs)**: `Win32_PhysicalMemory.ConfiguredClockSpeed`
(the speed Windows actually detected a module running at) alongside the
already-read `Speed` field (the module's rated/SPD speed) is a real,
accurate way to detect "XMP/DOCP not enabled" — when configured speed is
lower than rated speed, the System tab's memory-module row now says
`"2133 MHz running (rated 3200)"` instead of just the rated number.
`Win32_PhysicalMemoryArray.MemoryDevices` (total physical slots) is
compared against the populated-module count for a "3 of 4 slots
populated" line. Windows has no separate "compressed memory" stat the way
macOS does — the Memory tab's existing Committed % figure (from Round 2's
`Memory\% Committed Bytes In Use`) already **is** Windows' equivalent
memory-pressure indicator, so this item became a one-line explanatory note
next to that tile rather than a new figure.

**Service dependency graph (Services)**: `ServiceController.ServicesDependedOn`/
`DependentServices` are already-available .NET APIs (unlike almost
everything else in `ServiceControlService`, no WMI/registry needed) — read
fresh every 2s tick per service, the same "no per-row caching" tradeoff the
existing `Description` registry read already makes, since dependencies
can't change without a reboot/reinstall anyway. Shown as a two-column
"Depends on" / "Other services depend on this" panel below the grid for
whichever service is currently selected, via a new
`Converters/StringListJoinConverter.cs` ("None" for an empty list).

### Round 5: hang duration, core parking, SSD wear, scheduled tasks, alerts/report, log rotation

A fifth batch of `suggestions.md` items, 20 in total, again following established patterns rather
than introducing new architecture — see each area below.

**Not-responding duration + row tint (Processes)**: `ProcessMonitorService` already flagged
"Not responding" via `Process.Responding`; this round adds a per-pid `_notRespondingSince`
dictionary so the Status column can show "Not responding (12s)" instead of a flat label, and the
Processes grid's `RowStyle` gains a `DangerMutedBrush` tint on those rows - "make it more
prominent with duration" was the original ask, and a plain text flag alone is easy to miss in a
long scrolling grid.

**Per-core parking status (CPU)**: `HardwareMonitorService` adds a second `PerformanceCounter[]`
array parallel to the existing per-core `% Processor Time` one, reading the "Processor
Information\Parking Status" instance (0 = unparked, nonzero = parked) - same instance names, same
numeric node/core sort, wrapped in its own try/catch since older Windows versions don't expose
this counter at all (degrades to "no cores parked" rather than failing construction).
`CoreUsage.IsParked` dims a per-core `VfdMeter` tile to 0.5 opacity and tags it "Parked" (taking
priority over the P-core/E-core tag), and a new "Parked cores" diagnostics tile on the CPU tab
gives the aggregate count - a common, otherwise invisible reason "only half my CPU seems busy"
under light load.

**SSD wear indicator (System Specs)**: `MSFT_StorageReliabilityCounter.Wear` in
`root\Microsoft\Windows\Storage` - the same figure PowerShell's `Get-StorageReliabilityCounter`
reports - rather than parsing raw SMART attribute bytes, which would be a much larger and riskier
undertaking. There's no direct WMI association between `Win32_DiskDrive` (used for the rest of
this app's disk info) and `MSFT_PhysicalDisk`/`MSFT_StorageReliabilityCounter`, so
`SystemSpecsService.ReadDiskWearByIndex` pairs them by their shared small numeric index
(`Win32_DiskDrive.Index` and `MSFT_StorageReliabilityCounter.DeviceId`) - a best-effort match that
holds on an ordinary single-controller desktop/laptop but isn't guaranteed under RAID/Storage
Spaces, so the whole feature degrades to "not shown" on any failure rather than risk showing the
wrong disk's wear. Rendered as an appended `" · N% life used"` on the existing Storage card's
secondary line rather than a new column.

**Fan curve, motherboard/VRM temperature, power-ceiling detector (Energy & Thermals / CPU)**: the
fan curve is a `ScatterSeries<ObservablePoint>` (LiveChartsCore.SkiaSharpView 2.0.5 - the version
already in use here - does expose a scatter series, unlike the uncertainty Round 4 flagged around
in-chart throttle markers) plotting one (CPU package temp, primary-fan RPM) sample per tick, capped
to a rolling 120-sample window; a fan that isn't ramping with load shows up as a flat/scattered
cloud instead of a rising trend. Motherboard/VRM temperature reuses the CPU temperature chart's
exact glow+core `LineOf` pattern on a second history buffer, restricted to `HardwareType.Motherboard`
sensors specifically (the same "restrict to one hardware tree" trick the GPU hotspot lookup already
uses, so a same-named sensor on a different component can't collide). The power-ceiling detector
lives on `CpuViewModel` next to the existing thermal-throttle flag: `EnergyThermalsViewModel` now
tracks `PowerSessionMaxW` (a running session-high), and `IsPowerLimited` fires when the CPU is
pinned within 3% of that high, below base clock, under load, but **not** also reading hot - the
"power ceiling, not thermal ceiling" signature, pointing at a PSU/motherboard limit or a vendor
PL1/PL2 cap instead of the cooler. Same heuristic tier as the existing throttle flag - no access to
the vendor-proprietary limit-reason MSR data HWiNFO reads directly.

**Scheduled Tasks viewer + measured logon delay (Startup tab)**: `Services/ScheduledTaskService.cs`
shells out to `schtasks.exe` (`/query /fo csv /v` for the list, `/change /tn ... /enable|disable`
to toggle, `/query /tn ... /xml` for a single task's trigger XML) rather than taking a Task
Scheduler COM (`ITaskService`) dependency - this app has no COM interop anywhere else, and
schtasks' CSV/XML output is a stable, documented contract, the same "known Windows tool, not raw
interop" tradeoff `ServiceControlService`'s recovery-actions reader and `netsh wlan` parsing
already take. A hand-rolled quoted-CSV line parser handles the `/fo csv` output (no CSV library
dependency for one simple, fixed escaping rule). The logon-trigger delay - #17's actual *measured*
value, as opposed to Task Manager's own estimated "startup impact" rating - isn't in the CSV output
at all, only in the per-task XML export as an undocumented but stable `<Delay>PT30S</Delay>`
duration, so it's read on demand per selected task (a "Check logon delay" button) rather than for
every row up front. The whole section is loaded on demand too (a "Load scheduled tasks" button) -
enumerating every registered task can take a couple of seconds on a system with hundreds of them.
`StartupView.xaml` needed converting from a bare `Grid` to a `ScrollViewer`-wrapped `StackPanel`
with explicit `DataGrid` heights to fit the new section without truncating the tab.

**Service recovery/failure-action viewer (Services)**: `ServiceControlService.ReadFailureActionsText`
shells to `sc.exe qfailure "<name>"` and returns its (lightly cleaned-up) text output rather than
decoding the underlying `SERVICE_FAILURE_ACTIONS` registry binary value directly - that layout is
undocumented, and sc.exe already does the decoding reliably at the command line, the same
"known tool, not raw struct interop" tradeoff as the Scheduled Tasks reader above. On-demand only
(a "Recovery actions" button next to Start/Stop/Restart), shown in a new third column of the
existing dependency-graph panel below the grid.

**Recently installed software, USB device list, page file location (System Specs)**: recently
installed software reads the per-app Uninstall registry keys (both the native and Wow6432Node
views) the same way Windows' own "Installed apps" settings page does, filtering `InstallDate`
(a plain `yyyyMMdd` string, not a real date type) to the last 6 months and excluding
`SystemComponent=1` entries and Microsoft-published noise; there's no equivalent Windows log of
*uninstalls*, so this is deliberately install-only, not a full add/remove timeline. USB devices are
`Win32_PnPEntity` rows filtered to `PNPDeviceID LIKE 'USB%'`, sorted so any device with a nonzero
`ConfigManagerErrorCode` (Device Manager's own "problem code") surfaces first - per-device power
draw is deliberately not shown, the same "no reliable public API for it" reasoning Round 3's
per-process power figure was skipped for. Page file location resolves the page file's drive letter
(`Win32_PageFileUsage.Name`) through the documented `MSFT_Volume` → `MSFT_Partition` → `MSFT_Disk`
→ `MSFT_PhysicalDisk.MediaType` associator chain in `root\Microsoft\Windows\Storage` to get an
actual SSD/HDD answer, shown as a one-line note under the Storage card (warning-colored when the
page file lands on an HDD) rather than a new card.

**Health Check additions - Defender scan heuristic, anomaly highlighting (Summary)**: both are pure
aggregator rules added to `SummaryViewModel.RefreshHealthIssues`, no new sampling. The antivirus
heuristic just looks up `MsMpEng` in the Processes tab's already-polling collection and flags
sustained CPU use past 20% - Windows exposes no "scan in progress" API, so this is the same
"quick visual flag, not a verdict" tier as the process signature check. Anomaly highlighting
computes a mean/stddev over `PerformanceViewModel`'s existing 60-sample CPU/RAM/Disk history
buffers (already kept for the charts, no new buffer) and flags the current reading only when it's
*both* a meaningful raw jump (≥20 points) *and* a real statistical outlier (≥3 standard deviations
past a floor) - the double condition keeps a merely-noisy-but-normal baseline from producing false
positives the way a bare z-score threshold would on a near-flat history.

**Configurable threshold alerts + one-click diagnostic report (Summary)**: alert thresholds
(CPU%/Memory%/CPU temp, each with its own enable checkbox) persist to
`%AppData%\TaskManagerPlus\alerts.json` the same way `ThemeService` persists colors, and are
checked on the Health Check card's existing 2s timer - edge-triggered per metric (a `_xAlerted`
bool that resets when the value drops back under threshold) so one sustained excursion produces
one toast, not one every tick. The toast itself (`Views/ToastWindow.xaml` + `Services/ToastService.cs`)
is a hand-rolled borderless always-on-top `Window` positioned bottom-right of the work area and
auto-closed after 8 seconds, not a native Windows toast - a real Action Center toast needs an
AppUserModelID/MSIX package identity this app's classic .exe deployment doesn't have. The
diagnostic report bundles system specs, the Health Check list, recent Stability-tab events, a
sensor snapshot, and top CPU/memory processes into one Markdown file via a `SaveFileDialog` -
`MainViewModel`'s existing construction order (Stability built before Summary) meant
`SummaryViewModel`'s constructor could take a `StabilityViewModel` reference the same way it
already takes five other view-models.

**Log rotation + event markers (Logging)**: `LoggingService` now tracks the header list and base
path it was started with so it can transparently roll over to a `-partN` file (same header
rewritten) once the active file crosses 100 MB, firing a `Rotated` event so `LoggingViewModel` can
refresh the footer's "Stop Logging (filename)" display - an unattended "log everything forever"
session no longer silently fills the disk. Event markers are a plain, always-present trailing
"Marker" column (blank on every row except one where the footer's new "Add marker" button was
used) rather than a separate line in the CSV - keeps the column count fixed for the file's
lifetime the same way the sensor-column snapshot already does, while still letting a user tag
"this is when it happened" while reproducing an issue.

**Color-blind-safe alert palette (Theming)**: a `ColorBlindSafeAlerts` toggle in the Settings
drawer, orthogonal to the existing theme-family/saturation system - when on, `ThemeViewModel.ApplyPalette`
overwrites just the `SuccessBrush`/`WarningBrush`/`DangerBrush` (+hover/muted variants) with a
fixed deuteranopia/protanopia-safe blue/yellow/orange triple instead of the active palette's own
green/amber/red, deliberately skipping the saturation adjustment those three otherwise get so the
slider can't undermine the color choice. Persisted in `ThemeColors.ColorBlindSafeAlerts` (defaults
false, so existing `theme.json` files load unaffected).

**Command-line JSON snapshot (`--dump-json`)**: `App.xaml.cs` now overrides `OnStartup` (App.xaml's
`StartupUri` was removed so this path can skip showing a window entirely rather than flashing one
open and closing it) and checks `e.Args` for `--dump-json <path>` before creating `MainWindow`.
`Services/CliDumpService.cs` constructs fresh, short-lived `HardwareMonitorService`/
`SystemSpecsService`/`SensorMonitorService` instances, takes one sample of each, and writes a
plain JSON object via `System.Text.Json` - useful for a scripted remote-diagnostics caller that
wants one machine-readable reading without driving the full GUI. A known, documented limitation:
rate-based counters (CPU%, disk/network throughput) read 0 on a single sample taken immediately
after construction, since a real reading needs one full tick's elapsed time a one-shot dump can't
wait around for. The app's elevation requirement still applies to this path - launching with the
flag still triggers the same UAC prompt as a normal launch.

### Round 6: reliability history, per-tab depth across CPU/Storage/Network/Startup, snapshot
diffing, log tooling, and cross-cutting UX (mini dashboard, search, remote monitoring)

The sixth and, as of this round, final batch of `suggestions.md`'s original backlog - all 20
remaining items, closing out every category the file originally listed. Same "established pattern,
graceful degradation" ethos as every prior round; the more interesting tradeoffs are below.

**Reliability History (Stability)**: `EventLogService.Query` now also buckets its already-read
30-day event list into one Critical/Error count per calendar day (`BuildDailyCounts`), zero-filled
for quiet days rather than only showing spikes - no second event-log query, just a different view
of the same `events` list `RecentEvents`/`WasLastShutdownUnexpected`/etc. already derive from.
Rendered as a `ColumnSeries<double>` bar chart, the one new LiveChartsCore series type this app
uses (every prior chart is `LineSeries`/`ScatterSeries`) - a discrete daily count reads better as
bars than a connected line.

**CPU throttle-reason breakdown + C-state residency**: `CpuViewModel.ThrottleReason` is a pure
readout, not a new signal - it collapses the existing `IsThrottling`/`IsPowerLimited` heuristics
(Rounds 4/5) into one "Thermal"/"Power"/"None" string, since LibreHardwareMonitorLib still exposes
no CPU limit-reason API to build a real third category from. C-state residency
(`% Idle|C1|C2|C3 Time` on `"Processor Information"\_Total`) is a genuinely new `HardwareMonitorService`
counter group, each tier constructed independently via a new `TryCreateCounter` helper since not
every CPU/Windows generation reports all three tiers - `HardwareSnapshot.CStatesAvailable` lets the
CPU tab hide the whole section rather than showing an all-zero row when none are exposed.

**Storage Spaces/RAID rollup + HDD fragmentation (Storage)**: `StorageSpacesService` queries
`MSFT_VirtualDisk`/`MSFT_StoragePoolToVirtualDisk` in the same `root\Microsoft\Windows\Storage`
namespace the SSD-wear and page-file-location features already use - empty (card hidden) on the
large majority of systems that never configure Storage Spaces, the same "hidden when not
applicable" pattern the Battery section established. `DiskFragmentationService` reuses the
MSFT_Volume→MSFT_Partition→MSFT_Disk→MSFT_PhysicalDisk media-type associator chain
`SystemSpecsService.ReadPageFileLocation` introduced, generalized to any drive letter, to show the
on-demand "Analyze" button only for actual HDD volumes (SSDs never appear in the list at all -
fragmentation isn't a meaningful concept there). Analysis shells to `defrag.exe /A /V` (analyze
only, never moves data) and regex-extracts the "Total/File fragmentation: N%" lines from its
verbose report - the same "known Windows tool, not raw NTFS-bitmap interop" tradeoff
ScheduledTaskService/ServiceControlService's recovery-actions reader already take.

**Per-process network usage proxy (Network)**: `NetworkConnectionsService.SummarizeByProcess`
groups the same TCP connection list `NetworkConnectionsService.Sample()` already reads for the
"Active connections" grid by owning process, sorted by connection count. This is deliberately
*not* presented as bandwidth - true per-process byte attribution needs either the undocumented NSI
API Task Manager's own network column is built on, or enabling per-connection ETW stats
system-wide, both a materially higher-risk interop tier than anything else in this app. A
connection count is an honest, useful proxy instead (the process holding the most simultaneous
connections is very often the one saturating the link) and is documented as such in both the code
and the UI, rather than a byte-rate figure this app can't actually measure.

**Battery drain rate (Energy & Thermals)**: pulled out of the generic Battery sensor tile list
into its own headline `BatteryDrainRateW`/`BatteryIsCharging` readout, the same "find the one
figure that matters" treatment `CpuPackageTempC`/`TotalPackagePowerW` already get.
LibreHardwareMonitorLib doesn't standardize battery Power-sensor naming any more than it
standardizes CPU sensor naming, so this tries name hints ("discharge rate", "charge rate") first
and falls back to the sign of any nonzero Power reading (negative conventionally means charging)
when no name match is found.

**Boot time breakdown + measured startup delay + boot history trend (Startup)**: `BootPerformanceService`
reads the Microsoft-Windows-Diagnostics-Performance/Operational log's event ID 100 ("Windows has
started up") - but deliberately does *not* hardcode field names like "BootTime"/"MainPathBootTime",
since that event's exact schema is not a documented, versioned Microsoft contract this app could
rely on matching correctly across Windows builds. Instead it adaptively scans the event's rendered
XML for any `<Data Name="...">` field whose name mentions both "Boot" and "Time" and whose value is
a plausible millisecond duration, showing whatever it finds as-is (split into readable words) with
the largest value standing in for "total boot time" - the same "adaptive, degrade gracefully rather
than guess a wrong exact contract" tradeoff `EventLogService`'s bugcheck-code extraction already
established for a different event. Boot history is a small self-recorded JSON log
(`%AppData%\TaskManagerPlus\boot-history.json`, same persistence shape as `ThemeService`) appended
to once per session, de-duplicated by boot timestamp. Startup delay is a genuinely different,
independent measurement from the boot-breakdown event: `StartupDelayService` matches each startup
item's executable name against `Process.GetProcesses()` and computes `Process.StartTime` minus the
approximate boot time (`Environment.TickCount64`, same approximation `EventLogService` uses) - only
works for an item whose process is still running, and takes the smallest plausible delay among
same-named matches so a later user-relaunch of the same app doesn't get mistaken for its
startup-triggered launch.

**BIOS age hint (System Specs)**: `Win32_BIOS.ReleaseDate` age in days, appended to the existing
BIOS version line and warning-colored past 3 years. Explicitly framed as "worth a manual check on
the OEM support page," not a verified "update available" flag - Windows has no cross-vendor BIOS
update-check API (that's OEM-tool territory, e.g. Dell Command | Update), the same honesty
tradeoff `ReadOutdatedDrivers`' date-based filtering already takes for third-party drivers.

**Baseline snapshot / "what changed" diff (Summary)**: one mechanism serves both original
suggestions, since a saved baseline snapshot *is* the comparison point for a later diff.
`SnapshotService.Capture()` reads its own independent data (Uninstall registry keys,
`ServiceController.GetServices()`, a fresh `StartupManagerService` instance) rather than reusing
the Processes/Services/Startup tabs' live collections, so a capture isn't affected by whatever
filter/sort state those tabs currently have applied. Saved as plain JSON via `SaveFileDialog`/
`OpenFileDialog`; `Diff()` is a simple case-insensitive set difference per category (added/removed),
shown as a green/red line list on the Summary tab.

**Log file viewer/replay + auto-start rolling buffer + HTML report (Logging)**: `LogReplayService`
parses a previously-written CSV by header *name* (Timestamp/CPU Total (%)/RAM (%)/Disk Active (%))
rather than fixed column indices, since this app's own log column set has changed release to
release (Rounds 2-5 each added columns) and an older log file won't have every column a current
one does - reuses the same quoted-CSV line parser `ScheduledTaskService` already has for schtasks'
output, since this app's own `LoggingService.Escape` uses the identical quoting rule.
`LoggingViewModel` was refactored so `BuildHeaders`/`BuildRow` take an explicit column-snapshot
parameter instead of reading `_coreCountAtStart`/`_sensorColumnsAtStart` fields directly - needed
so the new auto-start rolling buffer (`AutoStartRollingBufferEnabled`, persisted like every other
settings toggle) can maintain its *own*, independent column snapshot and fixed-size in-memory
`Queue<string>` (last N minutes, one row/sec) without interfering with a concurrent manual logging
session; `LoggingViewModel.Tick` runs one or the other, never both, since manual logging always
takes precedence. The rolling buffer flushes to one fixed file every 10 ticks (not every tick) - a
crash mid-session still leaves a file at most ~10 seconds stale, without rewriting a small CSV to
disk every single second for a background, always-on feature. The HTML report
(`SummaryViewModel.GenerateHtmlReportCommand`) reuses the existing Markdown report's exact data
sources but renders self-contained HTML with hand-built inline-SVG `<polyline>` sparklines for the
CPU/RAM/Disk history buffers already kept for the live charts - deliberately no charting library
dependency for three 60-point lines, and no external references at all so the file stays a single
shareable artifact.

**Mini dashboard, cross-tab search, remote monitoring (UX)**: the two "detach a few tiles"
suggestions (pin-to-top overlay, second-monitor mini dashboard) collapsed into one feature -
`MiniDashboardWindow`, a small borderless always-on-top window (same `WindowStyle="None"`/
`AllowsTransparency`/`Topmost` shape `ToastWindow` already established) that binds directly to the
existing `MainViewModel` instance, so it's a second *view* over already-ticking data, never a
second poller; `MainViewModel.ToggleMiniDashboardCommand` opens/closes a single tracked instance.
`GlobalSearchViewModel` is a pure live filter over Processes/Services/Startup/System-Specs'
already-loaded collections (no new sampling), shown as a popup under a search box in the window
header via the `{Binding DataContext.X, RelativeSource={RelativeSource AncestorType=Window}}`
indirection the Settings drawer's cross-view-model bindings already use (SettingsPanel's own
DataContext is `ThemeViewModel`, not `MainViewModel`, so reaching sibling view-models needs the
same trick). `RemoteMonitorService` wraps `HttpListener` (no new package - it's in the BCL) serving
one self-refreshing HTML page plus a `/metrics.json` endpoint, built from a `Func<RemoteMetricsSnapshot>`
callback that reads already-polled `Performance`/`EnergyThermals` state - deliberately off by
default and opt-in via the Settings drawer, with an explicit "unauthenticated, LAN-visible" warning
in the UI, since `HttpListener` has no built-in auth and this app takes no dependency that would
add one; the snapshot it serves is a small, fixed, read-only subset (no process list, no file
paths, no control actions) by design. Binds `http://+:port/` (all local interfaces) - this app's
existing elevation requirement is what makes that binding succeed without a separate `netsh http
add urlacl` reservation.

### Round 7: process tree/control depth (affinity, suspend, environment, handle types) and
service accountability (logon accounts, drivers, config drift, start duration)

A seventh batch of `suggestions.md` items - 17 in total, closing out the Processes and Services
categories. This round leans harder on raw native interop than any prior one (five new P/Invoke
surfaces in one round), so each new `Services/*` file follows the same "wrap everything, degrade to
0/Unknown/empty on failure" discipline `CpuTopologyService`/`NetworkConnectionsService` already
established, rather than introducing a new risk tolerance.

**Process tree + job-object/process-group heuristic**: `ProcessesViewModel.ShowTree` toggles a
`TreeView` (bound to a fresh `ObservableCollection<ProcessTreeNode>`, `Models/ProcessTreeNode.cs`)
alongside the existing flat `DataGrid`, rebuilt each tick from the already-sampled `Processes`
collection (`BuildProcessTree`) rather than a second sampling source - a process whose parent isn't
currently running becomes a root node, the same "orphan" treatment Task Manager's own Details-tab
tree uses. `TreeView` has no built-in two-way `SelectedItem` binding, so `ProcessesView.xaml.cs`
pushes the selection into the view model manually on `SelectedItemChanged`. Job-object/process-group
detection (there is no per-process Windows API for job-object membership) is a heuristic proxy
instead, computed in `ProcessMonitorService.ComputeSpawnGroups`: processes sharing the same parent
pid and executable name, whose start times cluster within a 3-second window, are flagged with a
shared `ProcessRow.SpawnGroupSize` - a "quick flag, not a verdict" in the same family as the CPU
throttle heuristic, shown as a small "Group of N" badge in both the flat grid and the tree.

**Process control actions (trim, suspend/resume, affinity, priority, GDI/USER handles)**: all five
live in a new `Services/ProcessControlService.cs`, a sibling to `ProcessMonitorService` for
one-shot commands rather than per-tick sampling. Trim working set is `EmptyWorkingSet` (psapi.dll).
Suspend/Resume use `NtSuspendProcess`/`NtResumeProcess` (ntdll.dll) - undocumented but stable, the
same two calls Task Manager's own "Suspend process" Details-tab action and Sysinternals tooling are
built on; there is no documented Win32 equivalent for suspending every thread in a process
atomically. Priority class and CPU affinity turned out to need **no** raw interop at all -
`Process.PriorityClass`/`Process.ProcessorAffinity` are plain managed wrappers already in the BCL -
so `ProcessControlService.SetPriority`/`GetAffinity`/`SetAffinity` are thin wrappers with try/catch
around them. GDI/USER handle counts (matching Task Manager's optional Details columns) come from
`GetGuiResources` (user32.dll), sampled every tick per process alongside the existing handle count -
denied for a handful of protected processes even while elevated, degrading to 0 rather than
throwing. The CPU affinity editor (`ProcessesViewModel.AffinityCores`, one `CpuAffinityOption` per
logical processor) loads automatically on selection (a plain read), but only *applies* on an
explicit "Apply affinity" button click - the same "view now, commit explicitly" split the
modules/environment viewers below use for anything heavier than a read. "Suspended" also gets a
best-effort `ProcessControlService.IsSuspended` heuristic (every thread parked in a `Wait`/
`Suspended` state) feeding a dimmed row + "Suspended" status text, since Windows has no single
"process state" flag to query directly.

**Environment variables viewer**: `Services/ProcessEnvironmentService.cs` is the highest-risk
interop in this round - .NET's `Process` class only exposes environment variables for a child
process this app itself launched, not an arbitrary existing pid, so the only way to read one is a
PEB memory walk: open the process, `NtQueryInformationProcess` for its PEB address, read the
`ProcessParameters` pointer out of the PEB, then the `Environment` pointer out of that, and parse
the double-null-terminated block - exactly what Process Explorer's own Environment tab does under
the hood. Deliberately narrow: the offsets used (PEB.ProcessParameters at 0x20,
RTL_USER_PROCESS_PARAMETERS.Environment at 0x80) are only valid for a same-bitness 64-bit target
read from this (64-bit) app - a 32-bit/WOW64 target has a separate PEB at different offsets
(`ProcessWow64Information`, not implemented here) and degrades to an explanatory placeholder line
rather than risk reading garbage across that boundary.

**Open-handle-by-type breakdown**: `Services/HandleInspectionService.cs` answers "what kinds of
handles does this process hold" (File, Key, Event, Section, Mutant, ...) by walking the
system-wide handle table (`NtQuerySystemInformation`, `SystemHandleInformation`), filtering to the
target pid, then duplicating each handle into this process and asking `NtQueryObject` for its type
name. `NtQueryObject` is well known to occasionally hang forever on certain handle types (a named
pipe with no listener is the classic case), so every single per-handle query runs on its own
short-lived, abandoned-not-joined background `Thread` with a strict 120ms timeout - the same
defensive technique Process Explorer/System Informer use for this exact hazard - rather than risk
blocking the UI. Both the per-handle count (400) and overall wall time (6s) are capped, since this
is an on-demand, button-triggered inspector for one process, not a promise of full coverage on a
system service holding tens of thousands of handles.

**"What has this file open"**: rather than a second raw handle-table walk (fragile, and exactly the
hazard the paragraph above works around), `Services/FileLockLookupService.cs` uses the Restart
Manager API (`RmStartSession`/`RmRegisterResources`/`RmGetList`/`RmEndSession`, rstrtmgr.dll) - the
same documented, purpose-built Windows facility behind Explorer's own "this file is open in another
program" dialog and every installer's "close these programs first" prompt. A real, supported API
for exactly this question, not a repurposed diagnostic trick - and correspondingly the lowest-risk
new interop in this round. An empty result means "Restart Manager found nothing," not a guarantee
the file is free (a memory-mapped section with no remaining file handle wouldn't be reported).

**Duplicate-instance detector**: `ProcessMonitorService.ComputeDuplicateInstances` groups running
processes by resolved `FilePath` and flags a count past a deliberately generous threshold (20) as
`ProcessRow.IsDuplicateInstanceOutlier`. The threshold is tuned generous specifically because a
legitimate multi-process Chromium-family browser (Chrome/Edge/many Electron apps) routinely runs a
few dozen renderer/GPU/utility processes sharing one exe path - a real, documented false-positive
source that a tighter bar would trip on every single machine running one; a genuine runaway-launcher
crash loop tends to blow well past even this generous bar.

**Reverse svchost lookup**: `ServiceControlService.ReadServicesByPid` groups the same
`Win32_Service.ProcessId` column `ReadServicePids` already reads the other direction (by pid instead
of by name), exposed via a "Hosted services" button on the Processes tab (enabled only when the
selected row is actually named svchost) rather than a Services-tab feature, since the question
("what's inside *this* process") starts from a process row.

**Service logon accounts + config drift**: `ServiceControlService` reads `Win32_Service.StartName`
(`ReadServiceAccounts`) alongside the existing PID/exit-code queries, feeding `ServiceRow.LogOnAs` +
`IsNonStandardAccount` (flagged whenever it's neither empty, a standard built-in account, nor an
`NT SERVICE\...` virtual per-service account) - tinted in the grid the same way `IsHighPrivilege`
already tints a Processes row. Config drift reuses Round 6's `SnapshotService`/`SystemSnapshot`
baseline mechanism rather than inventing a second file format: `SystemSnapshot.ServiceConfigs`
(a new, purely additive field - an older snapshot JSON just loads with an empty list, the same
graceful-degradation shape `ThemeService` already relies on for a missing `theme.json` field) stores
each service's `StartType` + `LogOnAs` at capture time. "Capture config baseline" /
"Check config drift" on the Services toolbar call the same `SnapshotService.Save`/`Load` the Summary
tab's existing baseline/diff feature uses, just comparing `StartType`/`LogOnAs` per service instead
of the add/removed name-list diff Summary already shows - `ServiceRow.HasConfigDrift` tints a row
and its tooltip lists exactly what changed.

**Driver sub-view**: `ServiceControlService.SampleDrivers` is `ServiceController.GetDevices()` -
already available in .NET, no WMI needed - covering the kernel/file-system driver "services" the
ordinary Services tab never surfaces. A "Show drivers" toggle swaps the grid to a separate
`ServicesViewModel.Drivers` collection, sampled only while the toggle is on (an otherwise-idle
enumeration a user who never opens it shouldn't pay for); drivers carry a much narrower set of
meaningful columns (no dependencies, rarely a logon account) so `ServiceRow.IsDriver` rows just
leave those fields at their defaults rather than querying data that's rarely populated for a driver.

**Service start-duration history**: extends `EventLogService` (rather than a new class) with
`ReadServiceStartDurations`, mining the System log's own Service Control Manager event 7036
("service entered the running/stopped state") - the only service-lifecycle event Windows logs by
default. There's no explicit "start requested at" timestamp in default logging (that needs Verbose
SCM ETW tracing, a materially heavier ask), so this approximates a start duration as the time
between a service's most recent "stopped" 7036 entry and the following "running" 7036 entry for
that service - a real, if approximate, measurement, discarding any stopped-to-running gap wider
than 3 minutes as "sat stopped for a while, then happened to start" rather than reporting a wildly
inflated duration. On-demand only ("Load start-time history" button), the same shape
`StabilityViewModel`'s own on-demand event-log query already uses, since a 30-day scan isn't cheap
enough to repeat on a timer tick.

### Round 8: startup inventory/impact depth, CPU identification (SMT, cache, CPUID-via-.NET,
turbo histogram), and per-process/kernel memory diagnostics

An eighth batch of `suggestions.md` items - 19 in total, closing out the Startup, CPU, and Memory
categories. No new top-level tabs or ViewModels this round - everything extends `StartupViewModel`/
`CpuViewModel`/`MemoryViewModel`/`StabilityViewModel`/`SummaryViewModel` and their existing
services, following each category's established patterns.

**Signature check extracted into a shared service**: `Services/SignatureCheckService.cs` pulls
`ProcessMonitorService`'s Round 2 per-file-path signature cache out into a static, thread-safe
(`ConcurrentDictionary`) helper - `ProcessMonitorService` now calls it instead of keeping its own
private cache, and `StartupViewModel` reuses the exact same check/cache for the Startup tab's new
signed/unsigned badge rather than duplicating the `X509Certificate.CreateFromSignedFile` logic (and
its documented "no catalog signature" limitation) a second time.

**Startup item file size/last-modified + signature badge**: `StartupManagerService.Sample()` now
also resolves each item's target executable (`ExtractPath`, a small path-parsing helper extracted
out to be shared - `StartupDelayService` reduces its result further to a bare exe name, and
`StartupViewModel`'s signature check uses it directly) and reads `FileInfo` for size/last-write
time, degrading to null ("Unknown") for an unresolvable command or missing file. The signature
badge is computed in a background `Task.Run` alongside the existing measured-delay scan in
`StartupViewModel.Refresh`, applied back via `Dispatcher.Invoke` the same way.

**Browser extension inventory**: `Services/BrowserExtensionService.cs` reads Chrome/Edge's shared
`User Data\<Profile>\Extensions\<id>\<version>\manifest.json` layout (every profile folder, not
just Default, deduplicated by extension id) and Firefox's `<profile>\extensions.json` (`"addons"`
array, filtered to active, non-built-in entries). A manifest name like `"__MSG_extName__"` points at
a `_locales` message file this app doesn't resolve, so that case falls back to the raw extension id
rather than showing the unresolved placeholder. Loaded on demand (a "Load browser extensions"
button) - walking every profile's Extensions folder and parsing a manifest per extension is more
I/O than this tab's live-polled sections do, the same "expensive, so make it explicit" tradeoff
Round 5's Scheduled Tasks section already established.

**Registered shell extensions**: `Services/ShellExtensionService.cs` reads the CLSIDs registered
under `ShellIconOverlayIdentifiers` and the three `shellex\ContextMenuHandlers` locations
(`*`, `AllFilesystemObjects`, `Directory\Background`), resolves each CLSID's friendly name and
`InprocServer32` DLL path from `HKEY_CLASSES_ROOT\CLSID\{guid}`, and cross-references the
`Shell Extensions\Approved` list Windows itself uses to allow a handler to load without a warning
prompt. Also on-demand (a "Load shell extensions" button), same tradeoff as browser extensions
above - this walks several registry trees per call.

**Startup impact score**: `StartupDelayService.ComputeDelays` (Round 6's measured-logon-delay scan)
now also returns a combined Low/Medium/High `StartupMeasurement.ImpactText` per item, blending the
measured delay with a quick CPU/memory footprint sample of the matched running process - two
`TotalProcessorTime` reads separated by one **shared** 250ms wait (taken once per scan, not once
per item, so a long startup list doesn't stack into a multi-second delay) the same "two samples,
one elapsed window" technique `ProcessMonitorService`'s own per-tick CPU% calculation already uses.
A weighted point score (delay/CPU%/memory MB, each contributing 0-2 points) buckets into
Low/Medium/High rather than claiming false precision - explicitly documented as a brief snapshot of
whatever the process happens to be doing right now, not a true measurement of its actual startup-
time footprint (which would need continuous tracking from the moment it launched).

**Scheduled-task logon-trigger run mode**: `ScheduledTaskService.ReadLogonDelay` became
`ReadLogonTriggerInfo`, reading both the existing `<Delay>` duration and a new `<LogonType>` value
from the same per-task XML export in one call rather than two - `InteractiveToken` means "only
while signed in", every other value (`Password`/`S4U`/`ServiceAccount`/`Group`) means "whether or
not logged on", a distinct and easy-to-miss startup-impact category since it can run work even at
the lock screen. Surfaced as a new "Runs" column next to "Logon delay", populated by the same
"Check logon delay" button click.

**SMT/Hyper-Threading sibling pairing**: `CpuTopologyService` already parses one
`RelationProcessorCore` entry per *physical* core out of `GetLogicalProcessorInformationEx`'s
buffer (Round 1) - this round just keeps more of what's already being decoded instead of discarding
it: each logical core's `CoreTopologyInfo.PhysicalCoreGroup` is now the index of the physical-core
entry it came from, and `CpuTopologySnapshot.HasSmt` is true when any entry's `GroupMask` has more
than one bit set (an SMT pair sharing one physical core). `PerformanceViewModel.SyncCores` looks up
each core's sibling index from this grouping (only on an actual core-count change, not per tick) and
folds it directly into `CoreUsage.Label` ("CPU 0 ↔4") rather than a separate tile field, since the
tile's `SubText` slot is already spoken for by the Parked/P-core/E-core tags.

**Turbo-boost histogram**: `PerformanceViewModel` accumulates a six-bucket, session-long histogram
(Below base / At base / Light-Turbo / Turbo / High turbo / Max turbo) from the same
`CpuVsBasePercent` figure already computed each tick (Round 3) - bucketed by percent above/below
base clock rather than raw GHz, so it stays meaningful across different CPU models without a
per-model frequency table. Rendered as a small labeled-bar list (a `PercentToWidthConverter` inside
a fixed-width container) on the CPU tab rather than a `ColumnSeries` chart - simpler than wiring a
new chart series for six static rows that only ever grow monotonically over a session.

**Core-affinity heatmap**: `Services/CoreAffinityService.cs` walks the threads of the current top
few CPU-consuming processes and calls `GetThreadIdealProcessorEx` (kernel32) on each - the closest
proxy available to "which cores is this process's work landing on" without taking on ETW
context-switch tracing (a materially higher risk tier this app doesn't otherwise reach for
anywhere). Framed explicitly in the UI and code as the scheduler's *preferred* core per thread, not
a live trace of the core it's actually running on this instant - the same "quick flag, not a
verdict" tier as the CPU throttle heuristic. `CpuViewModel` now also takes a `ProcessesViewModel`
reference (constructed before `Cpu` in `MainViewModel`, same reordering trick prior rounds have used
for a new cross-VM dependency) and refreshes the heatmap on its existing 2s throttle timer, guarded
against overlap since the native per-thread scan can occasionally run long on a busy system.

**CPU identification card (microcode, mitigations, instruction sets, cache)**: all four queried
once via `Services/CpuFeatureService.cs` in `CpuViewModel`'s constructor (static data, same
treatment `CpuTopologyService`'s own one-time query gets). Microcode revision reads the same
`HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\"Update Revision"` registry value tools like
CPU-Z read (an 8-byte value, revision in the last 4 bytes little-endian) - not a documented,
versioned Microsoft contract, so a surprise length/format degrades to "Unknown" rather than a wrong
number, the same caveat `EventLogService`'s bugcheck-code extraction already carries for a
different undocumented layout. Spectre/Meltdown mitigation status is informational only: Windows
offers no simple "are mitigations active right now" API, so this reports whether an administrator
has manually overridden the default via `FeatureSettingsOverride`/`FeatureSettingsOverrideMask`
(their absence is the common, healthy case) without attempting to decode the exact undocumented bit
meaning - a "quick flag, not a verdict" pointing at Microsoft's own KB4072698 for specifics.
Instruction-set support (SSE4.2/AVX/AVX2/AVX-512/FMA) is the one readout in this round that's
genuinely accurate rather than best-effort: it reads `System.Runtime.Intrinsics.X86`'s own
`IsSupported` flags, which are backed by .NET 8's own CPUID-driven, JIT-verified runtime feature
detection - a correct answer for "can code running on this machine actually use this instruction
set" without this app taking on raw CPUID execution (self-modifying executable memory) or a native
helper process, both a materially higher risk tier than the interop this app already carries
elsewhere. (AVX-512 in particular can still read `false` on hardware that has the silicon but isn't
exposed by this OS/runtime combination - documented in the UI, not just here.) Cache sizes prefer
`Win32_CacheMemory` (the safer WMI-only path vs. CPUID leaf parsing) but fall back to
`Win32_Processor.L2CacheSize`/`L3CacheSize` for L2/L3 when `Win32_CacheMemory` reports nothing - a
known, common gap on modern systems; there's no L1 equivalent on `Win32_Processor`, so L1 stays
"Unknown" in that fallback case.

**Extra per-process memory columns**: `ProcessRow` gains `PrivateBytes`/`VirtualBytes`
(`Process.PrivateMemorySize64`/`VirtualMemorySize64`, no new interop) shown alongside the existing
working-set column on the Processes grid. Modern Windows doesn't expose a *fourth* truly distinct
"commit charge" figure for a process beyond private bytes (Task Manager's own "Commit size" column
reads the same underlying number `PrivateMemorySize64` does), so virtual size fills the third slot
instead of a redundant duplicate - documented as a deliberate, honest scoping decision rather than
silently showing two columns with identical values.

**Top kernel-pool consumers**: `ProcessRow` also gains `NonpagedPoolBytes`/`PagedPoolBytes`
(`Process.NonpagedSystemMemorySize64`/`PagedSystemMemorySize64`, again already exposed by .NET with
no extra interop). `MemoryViewModel.TopPoolProcesses` re-sorts the same already-polling Processes
collection `TopMemoryProcesses` already does (live-sorted by nonpaged pool descending, paged pool
shown alongside each row) - no new process sampling, the same "second `ICollectionView` over one
shared collection" pattern `TopMemoryProcesses` established in Round 2.

**Memory-in-use-by-category stacked bar**: built entirely from figures the Memory tab already
reads - no new signal. `PerformanceViewModel.MemoryInUsePercent`/`MemoryStandbyPercent`/
`MemoryFreePercent` split matches Windows' own Resource Monitor breakdown: since
`GlobalMemoryStatusEx`'s "available" figure already folds the standby (reclaimable cache) list in,
"in use" is Total minus Available (excludes standby), Standby is its own slice, and Free is
whatever of Available isn't standby - the three sum back to the total. Rendered as three
`Grid.ColumnDefinition`s whose `Width`s come from a new `PercentToStarWidthConverter` (percent ->
`GridLength(percent, Star)`) rather than a bespoke stacked-bar control, since three star-weighted
columns already lay out proportionally for free.

**Low-memory resource-exhaustion event detector**: extends `EventLogService` with
`ReadLowMemoryEvents`, a second targeted `EventLogQuery` against the
`Microsoft-Windows-Resource-Exhaustion-Detector` provider - these events log at Warning level, not
Critical/Error, so they fall outside the `Level=1|2` filter the tab's main 30-day scan already uses
and need their own query, the same "one provider, one separate query" shape
`ReadServiceStartDurations` (Round 7) already established for a different provider. Surfaced as a
fourth tile alongside "Since last crash"/"TDR events"/"Minidumps" on the Stability tab.

**Swap-thrash Health Check rule**: a pure aggregator rule in `SummaryViewModel.RefreshHealthIssues`
(no new sampling) - sustained heavy paging (`Performance.HardFaultsPerSec >= 500`) together with
very little free RAM (`RamAvailablePercent < 10`) is a much stronger "the system is actually
thrashing" signal than either figure alone, since a hard-fault burst during a big file load or
briefly-low available RAM after opening several apps are both routine and not worth a critical flag
on their own - the same "combine two figures for a real signal" reasoning the Round 4 dead-fan
detector and Round 3's link-speed heuristics already established for different false-positive
sources.

**RAM module JEDEC manufacturer lookup**: `Services/JedecManufacturerLookup.cs` is a small,
explicitly non-exhaustive table (about a dozen entries) mapping a handful of common raw SPD/JEDEC
manufacturer codes to a friendly brand name, used only when `Win32_PhysicalMemory.Manufacturer`
comes back as a short hex code rather than an already-readable string (the common case on modern
firmware, which is left untouched) - an unmatched code passes through unchanged rather than being
forced into a wrong guess, the same honestly-scoped-informational-only framing the BIOS-age hint
(Round 6) and outdated-driver filtering (Round 3) already established for "worth knowing, not a
verified fact" data.

### Round 9: Storage volume diagnostics (BitLocker/SMART/scanner/throughput/VSS/TRIM) and
Network deep-dive tools (history/proxy/driver/traceroute/jitter/captive-portal/metered)

A ninth batch of `suggestions.md` items - 16 in total, closing out the Storage and Network
categories. No new top-level tabs or ViewModels this round - everything extends `StorageViewModel`/
`NetworkViewModel`/`SystemSpecsViewModel`/`EnergyThermalsViewModel` and their existing services,
following each category's established patterns, most notably the "known Windows tool, not raw
interop" tradeoff (`vssadmin.exe`, `fsutil.exe`, `tracert.exe` all shelled out to, joining
`defrag.exe`/`schtasks.exe`/`sc.exe` from earlier rounds) and the "degrade to Unknown/hidden rather
than fabricate" honesty rule that runs through this whole app.

**Four per-volume facts bundled into one service**: `Services/VolumeDiagnosticsService.cs` answers
BitLocker status, Recycle Bin size, VSS shadow-copy usage, and TRIM status - one file because each
is a small, independent, gracefully-degrading read, the same "bundled because they answer one
question together" shape `SystemSpecsService.ReadSecurityInfo` already uses for TPM/Secure
Boot/VBS. BitLocker reads `Win32_EncryptableVolume` in
`root\CIMV2\Security\MicrosoftVolumeEncryption` (`GetConversionStatus`/`GetProtectionStatus`),
wrapped to degrade to "Unknown" rather than a false "Off" - both the namespace and its methods can
be denied even to this app's elevated process on non-Enterprise/Pro editions or under a stricter
policy, the same tier of failure `ReadTpmStatus` already documents for a neighboring security
namespace. Recycle Bin size is the native `SHQueryRecycleBinW` call (no managed .NET API exists for
it - the same interop-risk tier as `CpuTopologyService`'s native calls). TRIM status shells to
`fsutil behavior query DisableDeleteNotify <drive>` and is only read (and only shown) for volumes
`DiskFragmentationService.GetMediaType` reports as SSD - the mirror image of how HDD fragmentation
is hidden for SSDs. Shadow copy usage shells to `vssadmin list shadowstorage` once for the whole
system (not once per volume, since the command already reports every volume in one pass) and parses
its "For volume" / "Used Shadow Copy Storage space" text blocks. All four are read eagerly inside
`SystemSpecsService.ReadVolumes` (already running off the UI thread via `SystemSpecsViewModel`'s
existing `Task.Run`) and rendered on the System tab's existing Volumes card - a BitLocker badge
next to the drive letter (mirroring the dirty-bit badge), and a second line joining whichever of
Recycle Bin/shadow-copy/TRIM text actually has something to show (`VolumeRow.ExtraFactsText`, a
plain pre-joined string property rather than a WPF `MultiBinding`/converter for three optional
strings).

**On-demand full SMART attribute table**: `SystemSpecsService.ReadSmartDetails(diskIndex)` extends
Round 5's `ReadDiskWearByIndex` (which only ever surfaced `MSFT_StorageReliabilityCounter.Wear`) to
enumerate every non-null property that WMI instance actually carries - Temperature,
ReadErrorsTotal/Uncorrected, PowerOnHours, StartStopCycleCount, and others that vary by
drive/driver, so rather than hardcode an exact field list this adaptively reads whatever properties
are present and splits each PascalCase name into words for display, the same "adaptive, don't
assume a fixed schema" tradeoff `BootPerformanceService`'s event-field scan already established for
a similarly loosely-documented data source. Shown on the Storage tab as a disk picker (populated
from a new lightweight `SystemSpecsService.ListDisksForSmart()` query, kept separate from the much
heavier full `Query()` so the picker doesn't wait on the whole System tab's inventory read) plus a
"Read SMART details" button and a table - on-demand only, the same "expensive, so make it explicit"
tradeoff the modules list and event-log queries already take.

**Largest files/folders scanner**: `Services/LargestItemsService.cs` is a depth-capped
(6 directory levels for enumeration; folder totals themselves are summed slightly deeper, capped
independently, so a folder's reported size is a safety-bounded best effort rather than
unbounded on a pathologically deep tree), on-demand-only recursive walk from a user-entered root
path, returning the largest 30 files/folders found. Every subtree is enumerated independently with
its own try/catch, so one inaccessible folder (System Volume Information, another user's profile,
...) is skipped rather than failing the whole scan - the same graceful per-subtree degradation
`ReadRecentlyInstalledSoftware`'s per-registry-key loop already uses. Never runs automatically or
on a timer, the same "expensive, so make it explicit" tradeoff as the SMART table and HDD
fragmentation analysis above/before it.

**Throughput test, clearly labeled as approximate**: `Services/StorageThroughputService.cs` writes
then reads a temp file (capped to the smaller of 256 MB or available free space minus a 64 MB
safety margin, always deleted afterward even on failure) on a user-chosen volume, timing each pass
with a `Stopwatch`. Deliberately a single-threaded sequential pass only - no queue-depth sweep, no
random I/O pattern, no full cache-bypass beyond `FileOptions.WriteThrough`/`SequentialScan` - and
both the result message and the Storage tab's own UI text say so explicitly in-line, the same
"quick sanity check, not a verdict" honesty this app already applies to its other heuristic checks.

**NVMe controller-vs-flash-die temperature split**: `EnergyThermalsViewModel` gains
`StorageHotspotDeltaC`, following the exact same shape as Round 4's GPU hotspot-vs-edge
differential and Round 5's motherboard/VRM lookup - restricted to `HardwareType.Storage` sensor
readings, grouped by hardware name, and only populated (the Energy & Thermals tab tile hides
itself otherwise) when a single drive reports more than one temperature sensor, which
LibreHardwareMonitorLib exposes on some but not all NVMe drives/drivers.

**Historical connection-count totals, honestly scoped**: `Services/NetworkHistoryService.cs`
persists daily per-process connection-count totals to
`%AppData%\TaskManagerPlus\network-history.json` (same shape as `boot-history.json`/`alerts.json`,
trimmed to the most recent 180 days), aggregated into "today" and "this month" views on the Network
tab. This is deliberately **not** byte-level bandwidth history - Round 6's Network section already
documents that Windows exposes no public API for true per-process byte attribution (Task Manager's
own network column is built on an undocumented NSI call this app has consistently declined to
depend on), so a persisted *historical* figure built from an unmeasurable quantity would just be a
fabrication with better production values. Instead this persists the same honest connection-count
proxy `NetworkConnectionsService.SummarizeByProcess` already provides live, just accumulated over
time - "which process held the most simultaneous connections today/this month," not "which process
used the most data," documented as such in the code and the UI alike.

**Hosts file shortcut, proxy display, adapter driver version**: the hosts-file button
(`NetworkViewModel.OpenHostsFileCommand`) explicitly launches `notepad.exe` with the file's path
rather than `ShellExecute`-ing the bare path, since a file with no extension has no reliably
registered default handler to fall back to. Proxy configuration
(`NetworkDiagnosticsService.ReadProxyConfig`) is a plain, read-only registry read of the per-user
`Internet Settings` key (`ProxyEnable`/`ProxyServer`/`AutoConfigURL`) - the same source
`netsh winhttp show proxy`/Internet Options itself reads from, never written to by this app.
Adapter driver version/date (`ReadAdapterDriverInfo`) queries the same `Win32_PnPSignedDriver`
class `SystemSpecsService.ReadOutdatedDrivers` already uses, filtered to `DeviceClass = 'NET'` and
excluding virtual/tunnel adapter noise, applying the identical 2-year/2006-placeholder-date
heuristic - deliberately **not** a claim that a newer driver is actually known to exist anywhere;
this round makes no online lookup, and both the code comment and the UI are explicit that
`LooksOld` means "worth a manual check," nothing more, the same honesty tradeoff the BIOS-age hint
and outdated-driver list already established.

**On-demand traceroute and jitter/packet-loss test**: `Services/TracerouteService.cs` shells to
`tracert.exe -d -h 20 -w 1000 <host>` (a basic host-shape regex guards against passing arbitrary
user input to a shelled-out process; a 30s watchdog kills a run that's still going, since a lossy
path can make even a capped 20-hop trace run long) and returns its raw text output as-is rather
than attempting to parse per-hop structure - the same "known tool, not raw ICMP/TTL interop"
tradeoff as `defrag.exe`/`schtasks.exe` elsewhere in this app. The jitter/packet-loss quick test
(`NetworkDiagnosticsService.RunJitterTestAsync`) is ten sequential pings 200ms apart, reporting
min/max/avg round-trip and loss percentage plus a mean-consecutive-deviation jitter figure
(a simple, standard approximation - not a formal RFC 3550 jitter calculation). Both are on-demand
only (a host field plus a button each), never riding the existing 15s connectivity timer, since
either can take several seconds and is only useful when actively diagnosing a specific problem.

**Captive portal detection and metered-connection flag, both riding the existing 15s timer**:
captive portal detection (`CheckCaptivePortalAsync`) is the same NCSI-style check Windows itself
uses - an HTTP GET (redirects disabled, so a portal's redirect-to-login-page behavior itself
becomes the signal rather than being silently followed) to
`http://www.msftconnecttest.com/connecttest.txt`, expecting the literal body `"Microsoft Connect
Test"`; anything else (a redirect, different body, or an outright failure while otherwise
connected) means a portal is likely intercepting traffic. Folded into the existing
`ConnectivityResult`/15s timer per this round's assignment guidance, since it's the same class of
"real network I/O, not a local counter read" exception that timer already exists for. Metered
status (`Services/MeteredConnectionService.cs`) reads the per-network-profile
`DefaultMediaCost\{GUID}\Cost` registry values DUSM (the service behind Settings' own "Set as
metered connection" toggle) itself writes - not a documented public API, so this is the same
best-effort tier as the SecurityCenter2 AV `productState` bitmask read, wrapped to degrade to an
empty (hidden) list on any failure. Deliberately reads the registry directly rather than taking a
`Windows.Networking.Connectivity` WinRT/UWP-contracts package dependency, which would be a real
target-framework-shaped risk for one flag in a classic WPF exe that takes no other WinRT
dependency anywhere else in the app.

### Round 10: new GPU tab, System Specs depth (chassis/edition/monitors/Defender
exclusions/hardware IDs), and Stability polish (bugcheck lookup, crash grouping,
stability index)

A tenth batch of `suggestions.md` items - 16 in total, closing out the GPU (new
tab), System Specs, and Stability categories. The one new top-level tab in this
round is GPU; everything else extends `SystemSpecsViewModel`/`StabilityViewModel`/
`SummaryViewModel` and their existing services, following each category's
established patterns.

**GPU tab (per-engine utilization, VRAM, driver/WDDM version, multi-GPU list)**:
the new top-level tab follows the exact "UI shell" pattern every prior tab used -
a `TabItem` in `MainWindow.xaml` with a hand-drawn `Viewbox`/`Canvas` glyph (a
card body + fan + PCIe-pin lines, distinct from the CPU tab's chip-with-legs
icon) styled via the existing `NavIconStroke` style. `GpuViewModel` is the
newest addition to the small set of tab view-models that own their own
`DispatcherTimer` instead of riding `PerformanceViewModel`'s shared sampler
(joining `EnergyThermalsViewModel`) - `Services/GpuMonitorService.cs` reads the
`"GPU Engine"`/`"GPU Adapter Memory"` perf-counter categories (the same `"GPU
Engine"` category Round 4's per-process GPU column already reads via
`ProcessMonitorService.ReadGpuUsageByPid`, aggregated per-adapter here instead
of per-process) plus a one-time static `Win32_VideoController` + registry read
for driver version/date and a best-effort WDDM version (a `"WddmVersion"`
REG_DWORD under the display driver's Class subkey - not a documented Microsoft
contract, so a value outside the plausible 10-39 range degrades to "Unknown").
Both perf-counter categories key their instances by a LUID with no public API
mapping it back to a `Win32_VideoController` row, so `GpuMonitorService.Sample`
only *pairs* a live LUID group with a static adapter identity when the live
LUID count matches the static adapter count exactly (the common single-GPU
case, paired ordinally); when the counts don't match (a hybrid laptop whose
integrated GPU has no live counter data until something renders on it, ...)
this deliberately does not guess - the live row falls back to a generic
"GPU N" label with blank identity fields rather than risk showing one
adapter's driver info next to another's utilization. The "Installed adapters"
card always lists every adapter from the static read regardless of pairing
success, so the multi-GPU list is honestly complete even when live pairing
isn't. "Which app is using it" reuses the Processes tab's already-polling
`GpuPercent` column, live-sorted here rather than sampled a second time - the
same "second `ICollectionView` over one shared collection" pattern
`MemoryViewModel.TopMemoryProcesses` already established.

**System Specs: chassis, Windows edition/activation, .NET runtimes, monitors,
chipset driver, longest uptime, Defender exclusions, copy hardware IDs**: eight
more `SystemSpecsService` reads, each following an existing tradeoff family
rather than a new one. Chassis/form-factor (`Win32_SystemEnclosure.ChassisTypes`)
and Windows edition/activation (`SoftwareLicensingProduct.LicenseStatus`,
filtered to the Windows edition's `ApplicationID` - informational text only,
no product key ever read) are both plain WMI reads. Installed .NET runtimes
walk the `dotnet\shared\<RuntimeName>\<Version>` directory layout `dotnet
--list-runtimes` itself reads, across both Program Files and Program Files
(x86). Monitor/display inventory pairs two independent WMI sources the same
"only pair when counts match, don't guess" way the GPU tab pairs LUIDs to
adapters: resolution/refresh rate from `Win32_VideoController`'s current-mode
fields, and connection type from `root\wmi`'s `WmiMonitorConnectionParams`
(`VideoOutputTechnology`, a documented `D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY` enum,
not a guessed mapping) - HDR support has no reliable enumeration source short
of DXGI/`IDXGIOutput6` COM interop, a materially higher risk tier than
anything else this app takes on, so it's left out of `MonitorInfo` entirely
rather than shown as a fake "Unknown" field. Motherboard chipset driver
version is best-effort: there's no single canonical "chipset driver" WMI
class, so `ReadChipsetDriverInfo` searches `Win32_PnPSignedDriver`'s
System/SMBus-class rows for a device name mentioning "Chipset"/"SMBus"/"PCI
Express Root" and reports the newest match by driver date, degrading to
"Unknown" when nothing matches - the same tier as the BIOS-age hint and
outdated-driver filtering. Longest-uptime-this-month/year is a pure derived
read over the existing `boot-history.json` (`BootPerformanceService.
ComputeLongestUptimeRecords`, no new sampling) - a completed session's uptime
is approximated as the gap between one recorded boot timestamp and the next,
the same approximation this app's boot-time correlation already relies on
elsewhere. Defender exclusions read the `HKLM\SOFTWARE\Microsoft\Windows
Defender\Exclusions\*` subkeys directly - the key can legitimately be denied
by Tamper Protection even to this app's elevated process, so a failed read
returns `null` ("Unknown/inaccessible"), kept distinct from a successful read
that finds zero exclusions, the same "don't collapse two different
situations into one value" discipline `VolumeInfo`'s optional fields already
follow. "Copy hardware IDs" bundles the system product UUID
(`Win32_ComputerSystemProduct.UUID`), CPU ID (`Win32_Processor.ProcessorId`),
and GPU names onto the clipboard via a new `CopyHardwareIdsCommand` - plain
WMI identifiers with no personal/account data, unlike a Windows product key
(never read anywhere in this app).

**Stability: bugcheck lookup table, crash grouping by module, stability
index**: `Services/BugcheckCodeLookup.cs` is a small, explicitly
non-exhaustive table (~35 entries) of the most common Windows STOP codes,
appending a plain-English name onto the existing hex `BugcheckCode` string
(via a new `Converters/BugcheckCodeToDescriptionConverter`) rather than
replacing it - an unmatched code still shows the real hex value, the same
"informational, never replace the real value with a guess" tradeoff
`JedecManufacturerLookup` already established for RAM manufacturer codes.
Repeated-crash grouping (`StabilityViewModel.CrashesByModule`) is a pure
re-aggregation of the same `RecentEvents` list already loaded - grouped by
`FaultingModule`, sorted by count descending - shown as its own "Crashes by
faulting module" card (collapsed when empty) alongside the existing flat
event grid rather than replacing it, since the flat grid still serves every
other event type this tab shows, not just app crashes. The 0-10 stability
index (`StabilityViewModel.ComputeStabilityIndex`) is a documented, simple
weighted formula, not a black box: starts at a perfect 10 and subtracts up to
4 points for recent daily Critical/Error density (0.5 points per average
daily event over the last 7 days), 1.5 points flat for an unexpected shutdown
on the current boot, up to 2 points for TDR events (0.3 each), up to 1 point
for low-memory resource-exhaustion events (0.1 each), and 2 or 1 points for a
crash within the last 24 hours or 7 days respectively - clamped to [0, 10],
entirely derived from data this tab already reads (no new event-log query).
Shown as a fifth `VfdMeter` tile on the Stability tab (color-coded by a new
`StabilityIndexToBrushConverter`, the inverse direction of the existing
`PercentToBrushConverter` since a higher stability index is better) and,
alongside `TimeSinceLastCrashText` (#67 - already computed by
`StabilityViewModel`, just not previously surfaced on the Summary tab), as
two new tiles on the Summary tab's existing "System" card -
`SummaryViewModel` now exposes its already-held `StabilityViewModel`
reference publicly (`Stability`) rather than adding wrapper properties for
each figure.

### Round 11: dashboard tile hide/reorder, snapshot A/B diff, log rotation gzip/cleanup,
high-contrast theme + UI scale/compact rows, always-on-top + tab shortcuts

An eleventh batch of `suggestions.md` items - 14 in total, closing out the remaining Summary/
Health Check, Logging, and Theming/UX categories. Two items turned out to already be satisfied by
earlier rounds under different original numbering (suggestions.md renumbers as items are removed,
so a current item number doesn't correspond to the round that actually implemented it) - noted
below rather than re-implemented.

**Volume-nearly-full Health Check rule / log viewer sparklines - already done**: two of this
round's assigned items were, on inspection, already live: `SummaryViewModel.RefreshHealthIssues`
already flags a nearly-full volume (from the Health Check card's original introduction), and
`LogReplayService`/`LoggingViewModel.LoadLogFileCommand` (Round 6) already reopens a saved CSV and
re-charts its CPU/RAM/Disk columns as `LineSeries` sparklines on the Summary tab's "Log replay"
card. Both are left as-is; the volume rule gained a short comment clarifying its (intentionally
more conservative, 90%/97%) thresholds relative to the Volumes card's own 85% progress-bar tint.

**Dashboard tile hide/reorder (Summary)**: a full freeform drag-and-drop layout (dragging a tile
anywhere on the page, spanning the two-column grid) is a meaningfully larger WPF undertaking than
anything else in this round, so this is the honestly-scoped version per the assignment's own
guidance: each of the Summary tab's seven main tiles (CPU/Memory charts, Top CPU processes, Top
CPU 10s avg, Disk, Network, System) can be hidden and moved up/down within its column via a
"Customize tiles" panel, persisted to `%AppData%\TaskManagerPlus\dashboard-layout.json`
(`DashboardLayoutService`, same shape as `ThemeService`) - genuinely functional (hide a tile,
reorder it, restart the app, it's remembered), just reorder-within-column rather than freeform
drag-and-drop. Which column a tile belongs to (left/right) stays a structural page-layout choice,
not something the user reassigns. Mechanically, the two-column `Grid`'s previously-hardcoded
`StackPanel`s became `ItemsControl`s bound to `SummaryViewModel.LeftTiles`/`RightTiles`
(`ObservableCollection<DashboardTileViewModel>`), rendered through a new
`Views/DashboardTileTemplateSelector.cs` that resolves each tile's real content by looking up a
`"Tile_<id>"`-keyed `DataTemplate` in `SummaryView.xaml`'s `UserControl.Resources` - every binding
those templates used to reach `SummaryViewModel` directly now goes through
`DataContext.X, RelativeSource={RelativeSource AncestorType=UserControl}` (each item's own
DataContext is now the `DashboardTileViewModel`, not `SummaryViewModel`), the same
cross-view-model indirection trick the Settings drawer and log-replay card already use.

**Baseline-vs-baseline snapshot diff (Summary)**: `SnapshotService.Diff` already just takes two
plain `SystemSnapshot` objects - nothing about it assumed the second one was "current" - so this
needed no service-layer change, only a second, independent load/compare UI flow
(`LoadSnapshotACommand`/`LoadSnapshotBCommand`/`CompareSnapshotsAbCommand`, `SnapshotAbDiff`/
`SnapshotAbStatusText`) alongside the existing baseline-vs-current one, so the two comparison modes
don't share or clobber each other's state.

**Windows Update reboot-pending Health Check rule (System Specs/Summary)**:
`SystemSpecsService.ReadRebootPending` checks three well-known, widely-documented indicator keys
(the Component Based Servicing `RebootPending` key, the Windows Update Auto Update client's own
`RebootRequired` key, and a nonempty `PendingFileRenameOperations` value) rather than an exhaustive
enumeration of every possible reboot-pending source, which is large, undocumented, and
version-specific - any one present means true; a denied/failed registry read degrades to false
("not pending") rather than risk a false positive, the same "don't over-claim" tradeoff the volume
dirty-bit check already takes. Surfaced both as a plain Yes/No line on the System tab's Security
card and as a new Health Check entry.

**Log rotation: gzip + auto-cleanup (Logging)**: `LoggingService.RotateFile` now compresses the
just-closed part file with `GZipStream` (never the file still being actively written to) on a
background `Task.Run`, deleting the plain `.csv` once the `.csv.gz` copy succeeds - a plain-text
one-row-per-second CSV compresses very well, so this meaningfully shrinks an unattended long
session's footprint. `LoggingService.CleanupOldRotatedParts` (a new static helper, called once per
app launch from `LoggingViewModel`'s constructor when `LoggingSettings.AutoCleanupEnabled` is on)
deletes `-partN` files (plain or gzipped) older than `AutoCleanupDays` by last-write time - never
the currently-active file - both settings persisted alongside the existing rolling-buffer settings
in `LoggingSettings`/`LoggingSettingsService`.

**Configurable sample interval (Logging)**: `LoggingViewModel`'s previously-hardcoded 1s
`DispatcherTimer` interval is now `LoggingSettings.SampleIntervalSeconds` (1/5/10, radio buttons in
the Settings drawer via a new `Converters/IntEqualsConverter.cs` two-way int-equality check),
re-intervaling the already-running timer live on change. Applies to both manual logging and the
Round 6 rolling buffer; the rolling buffer's "N minutes" window math now divides by the interval
(`RollingBufferMinutes * 60 / SampleIntervalSeconds`) so "15 minutes" still means 15 minutes of
wall-clock time at any interval, not 15 minutes' worth of samples.

**Compact rows + independent UI scale (Theming)**: `ThemeViewModel.CompactRows` swaps two more
resource-dictionary values (`DataGridRowHeightValue`/`DataGridCellPaddingValue`, declared with
defaults in `Dark.xaml` and overwritten live by `ApplyRowHeight` the same "mutate the app resource
dictionary" way `ApplyPalette` already repaints colors) that `Dark.xaml`'s `DataGrid`/
`DataGridCell` styles now reference via `DynamicResource` instead of a hardcoded `34`/`10,4` -
every `DataGrid` across the app (Processes/Services/Startup/...) picks up the change live with no
per-view wiring. `ThemeViewModel.FontScale` is deliberately **not** a literal font-size override -
threading a font-size resource through every explicit `FontSize` setter across dozens of XAML
files (most cards hardcode it rather than inheriting from the `Window` style) would be a far more
invasive change than this round's other items. Instead it drives a `ScaleTransform` on
`MainWindow.xaml`'s `TabControl` via `LayoutTransform` - a uniform layout scale that grows/shrinks
text, tiles, charts, and grids together, achieving the practical "make the whole app easier to
read, independent of Windows' own display scaling" goal through a single, low-risk hook point
rather than a font-metric change.

**High-contrast theme variant (Theming)**: a 7th `PaletteDefinition` entry ("High Contrast": pure
black background, near-white text, status colors chosen for a high contrast ratio against black)
added to `ThemeViewModel`'s `Palettes` table and `ThemeModes` array - no new mechanism, the exact
same `ApplyPalette`/saturation/`ColorBlindSafeAlerts`-layering the original six families already
go through, so it shows up automatically in the Settings drawer's theme-family `ItemsControl`.

**Always-on-top + Ctrl+1..9 tab shortcuts (App-level UX)**: both are small window-level
preferences that don't belong in `ThemeColors` (window behavior/keyboard nav, not color/scale), so
they get their own `Models/UiPreferences.cs` + `Services/UiPreferencesService.cs`
(`ui-preferences.json`, same shape as every other settings file) owned by `MainViewModel` rather
than `ThemeViewModel`. `AlwaysOnTop` is a straight `Window.Topmost` binding - the same `Topmost`
behavior the mini dashboard/toast windows already use, just opt-in and user-visible for the main
window itself. Ctrl+1..Ctrl+9 (`MainWindow.xaml.cs`'s new `PreviewKeyDown` handler) jump to a tab
by matching `MainViewModel.TabShortcutOrder` (a plain ordered list of tab header strings, falling
back to this app's first nine tabs when `ui-preferences.json`'s `TabShortcuts` list is empty)
against each `TabItem.Header` - matching by header text rather than a hardcoded index means the
mapping still works if tabs are ever reordered in `MainWindow.xaml`. Per the assignment's guidance,
there's no in-app remapping UI (edit the JSON list directly to customize); the underlying Ctrl+1..9
navigation itself is fully functional with its default mapping.

**Export/import palette only (Theming)**: `ThemeViewModel.ExportPaletteCommand`/
`ImportPaletteCommand` write/read just the accent/family/saturation subset of `theme.json` (a new
small `Models/PalettePreset.cs` type, deliberately separate from `ThemeColors` so this file's shape
stays stable even as `ThemeColors` grows unrelated fields like `CompactRows`/`FontScale`) to a
`SaveFileDialog`/`OpenFileDialog`-picked `.tmpalette.json` file - a small, shareable "here's my
color scheme" file distinct from the app's full `theme.json`, which is already persisted
automatically and never hand-exported.

**"Generate report on exit" (Summary)**: a `SummaryViewModel.GenerateReportOnExit` toggle
(`Models/SummarySettings.cs`/`Services/SummarySettingsService.cs`, `summary-settings.json`) checked
from a new `MainWindow.xaml.cs` `Closing` handler that calls
`SummaryViewModel.GenerateReportOnExitIfEnabled()` - reuses `BuildReportMarkdown()` verbatim (the
same content the manual "Markdown report" button produces) but writes silently to a fixed,
timestamped path under `%AppData%\TaskManagerPlus\Reports\` rather than popping a `SaveFileDialog`
during shutdown, which would block the app from actually closing until the user responded to it.

### Round 12 (final): tray/hotkey/portable mode, power plan & sleep-state depth, remote monitor
token, per-tab poll interval

The twelfth and final batch of this cycle's `suggestions.md` backlog - 18 items across App-level/
cross-cutting, Energy & Thermals, Remote monitoring, and Misc, closing out this round of
`suggestions.md`'s 100-item backlog (the file's second full cycle - Round 6 closed out the
original 100; this closes out the 100 items numbered 1-100 in the version of the file that
followed it). Same "known tool / graceful degradation / no fabricated data" ethos as every prior
round, with two items - USB selective suspend and GPU power-limit - deliberately scoped down per
the assignment's own guidance, documented below.

**Portable mode (`AppPaths`)**: every settings-persisting service in this app
(`ThemeService`, `AlertThresholdsService`, `DashboardLayoutService`, `LoggingSettingsService`,
`NetworkHistoryService`, `RemoteMonitorSettingsService`, `SummarySettingsService`,
`UiPreferencesService`, `BootPerformanceService`, plus a few inline `SnapshotService`/
`LoggingViewModel` paths for the Snapshots/Logs/Reports folders) previously hardcoded
`Environment.GetFolderPath(SpecialFolder.ApplicationData)\TaskManagerPlus` independently - a new
`Services/AppPaths.cs` centralizes this into one `Initialize(args)` call (from `App.xaml.cs`,
before any other service runs) plus a `GetPath(...)` helper every one of those files now routes
through. `--portable` (or a `portable.marker` file dropped next to the exe, so a portable USB copy
"just works" without editing a shortcut) redirects `AppPaths.SettingsDirectory` to a `Settings`
folder next to the exe instead of `%AppData%`; the Settings drawer shows a read-only "Settings
storage" status line (portable mode is a launch-time decision, not a live toggle).

**`--tab <name>` launch flag**: `App.xaml.cs` looks for `--tab` in `e.Args` alongside the existing
`--dump-json` handling, and (once `MainWindow` is shown) calls a new
`MainWindow.SelectTabByName(name)` - matches by `TabItem.Header` text, case-insensitively, the
same header-based lookup `MainWindow_PreviewKeyDown`'s Ctrl+1..9 handler (Round 11) already uses,
so it keeps working if tabs are ever reordered. Silently no-ops on an unrecognized name rather than
erroring, since it's a convenience shortcut, not a required argument.

**System tray icon + minimize-to-tray + global hotkey**: the csproj now sets
`<UseWindowsForms>true</UseWindowsForms>` alongside the existing `<UseWPF>true</UseWPF>` - WPF has
no tray-icon API of its own, so this pulls in `System.Windows.Forms.NotifyIcon` from the Windows
Desktop targeting pack (not a NuGet package, the same "known built-in escape hatch" tier as
`PerformanceCounter`/WMI elsewhere in this app). Combining `UseWPF`/`UseWindowsForms` means both
frameworks' implicit global usings collide (`System.Drawing`/`System.Windows.Forms` both define
`Color`/`Brush`/`UserControl`/`Application`/`KeyEventArgs`, colliding with WPF's own) - fixed with
`<Using Remove="System.Drawing" />`/`<Using Remove="System.Windows.Forms" />` in the csproj, since
only `MainWindow.xaml.cs` needs either, via an explicit `Forms = System.Windows.Forms` alias and
fully-qualified `System.Drawing.Icon` calls rather than either implicit using. The tray icon is
extracted from this app's own exe (no separate `.ico` asset needed), carries an Open/Exit context
menu, and its tooltip is a live "CPU N% RAM N%" mini readout kept in sync off
`PerformanceViewModel`'s existing `PropertyChanged` notifications - no new polling, the same
"reuse an already-ticking ViewModel's notifications" trick the Health Check card and mini
dashboard already lean on. Minimize-to-tray (`UiPreferences.MinimizeToTray`, on by default) hides
the window on `StateChanged` instead of a normal taskbar minimize; the global hotkey
(`Services/GlobalHotkeyService.cs`, `RegisterHotKey`/`UnregisterHotKey` via a `HwndSource` message
hook - the same native-interop risk tier as `CpuTopologyService`'s own P/Invoke) is deliberately
Ctrl+Alt+T, not the literal Ctrl+Shift+Esc: that combination is intercepted by the shell itself to
launch the real Task Manager before it would ever reach this app's message loop, so claiming it
here would be a no-op at best. Registration failure (another app already owns the combination)
degrades to `GlobalHotkeyService.IsRegistered` staying false, never a crash.

**Read-only GitHub Releases update check**: `Services/UpdateCheckService.cs` does one `HttpClient`
GET to `api.github.com/repos/MasstarVT/TaskManagerPlus/releases/latest` (the repo's actual `origin`
remote) on startup, comparing the release tag against this build's own assembly version -
notify-only, a dismissible-free banner under the header with a "View release" button
(`Process.Start` with `UseShellExecute = true`), never an auto-download/install. Offline,
rate-limited (GitHub's unauthenticated 60/hour cap), or any other failure just means the banner
never appears - the same graceful-degradation tier `PublicIpLookupService`/
`NetworkDiagnosticsService`'s own outbound calls already established.

**Localization scaffolding**: `Resources/Strings.resx` + a hand-written `Resources/Strings.cs`
wrapper over a plain `ResourceManager` (deliberately not relying on Visual Studio's
ResXFileCodeGenerator custom tool, which needs the IDE rather than just `dotnet build` to
regenerate a Designer.cs) - a small, honestly-scoped first pass covering a handful of
header/footer labels (`AppTitle`, the Colors/Start Logging/mini-dashboard button text, the
elevation status line), wired into `MainWindow.xaml` via `{x:Static res:Strings.X}`. This is
scaffolding, not a finished localization pass - every other user-visible string across this app's
several dozen `.xaml` files is still a plain hardcoded literal; a future round extending this
pattern app-wide is explicitly out of scope here, per the assignment's own guidance.

**Power plan display/switch + Modern Standby (S0) vs. legacy S3 detection (Energy & Thermals)**:
`Services/PowerPlanService.cs` shells out to `powercfg.exe` (`/list`, `/setactive <guid>`, `/a`) -
the same "known Windows tool, not raw registry/COM interop" tradeoff `ScheduledTaskService`/
`ServiceControlService`'s recovery-action reader already take, since the underlying power-policy
API surface is COM-based and a meaningfully larger undertaking for what's ultimately "read and
lightly reformat a command's own text output." Both are on-demand (a "Load power info" button on
the Energy & Thermals tab, next to the new fan RPM chart) rather than polled, since a power scheme
essentially never changes outside a direct user action. Sleep-state support looks for the
well-known "S0 Low Power Idle"/"Standby (S3)" phrases in `powercfg /a`'s report text rather than
trying to parse its full structure (which isn't a stable, versioned contract across Windows
builds), degrading to "Unknown" when neither phrase is found.

**USB selective-suspend status - honestly scoped down (Energy & Thermals)**: a correct per-device
read needs SetupAPI device-property interop (walking device property sets by GUID) - a materially
larger native-interop undertaking than anything else in this app, well past the tier
`CpuTopologyService`/`NetworkConnectionsService`'s own P/Invoke already sits at. `Services/
UsbPowerService.cs` instead takes one real best-effort shot via the legacy `root\WMI`
`MSPower_DeviceEnable` class (present on many, not all, Windows builds/drivers), matched against
each USB `Win32_PnPEntity` by normalized-prefix comparison - the same prefix-match technique
`SystemSpecsService.ReadFailurePredictStatus` already uses for the SMART failure-prediction class,
since neither class publishes a clean, exact join key. Expect "Unknown" for a fair number of
devices on a fair number of systems, per the assignment's own explicit "reasonably degrade"
guidance for this item - the on-demand "Load USB devices" card's own subtext says as much.

**GPU power-limit/TDP - honestly scoped down (Energy & Thermals)**: `EnergyThermalsViewModel`
tries the same `FindByNameContains` name-hint lookup ("Power Limit"/"TDP Limit"/"TDP") already
used for `TotalPackagePowerW`, restricted to GPU hardware entries. Most GPU backends in
LibreHardwareMonitorLib 0.9.6 only expose instantaneous draw, not a distinct limit/TDP sensor, so
`GpuPowerLimitW` reads null (tile hidden) on the large majority of systems - the same sparse-sensor
honesty every other LHM-dependent readout in this app already documents, not a bug.

**Idle-temperature trend vs. baseline (Summary, ties into the existing snapshot mechanism)**:
`SystemSnapshot.IdleCpuTempC` is a new, optional field - `SnapshotService.Capture` takes an
optional `idleCpuTempC` parameter (the static service has no sensor access of its own;
`SummaryViewModel.CaptureIdleCpuTempOrNull` supplies it, and only when `Performance.CpuCurrentPercent`
is genuinely low at capture time, so a snapshot taken mid-benchmark never records a misleading
"idle" baseline). `BuildIdleTempTrendText` renders the before/after comparison on both the
baseline-vs-current flow (`CompareSnapshot`) and the Round 11 A/B flow (`CompareSnapshotsAb`),
framed explicitly in its own text as "a rough thermal-paste-age proxy, not a diagnosis" - room
temperature and dust affect this as much as paste age does, the same honesty tier the outdated-
driver date filtering and BIOS-age hint already established for their own rough proxies.

**Fan RPM history chart (Energy & Thermals)**: a second history buffer (`FanRpmHistory`) tracking
the same "primary CPU fan" the Round 5 fan-curve scatter already resolves each tick, rendered with
the exact glow+core `LineOf` pattern every other history chart on this tab uses. Deliberately a
plain time series alongside the scatter, not a replacement for it - a fan that's "hunting"
(repeatedly ramping RPM up and down at a near-constant temperature) reads as visible oscillation
in a time series, something the scatter cloud's shape alone hides.

**Out-of-spec voltage-rail flagging (Energy & Thermals)**: a simple ±5%-of-nominal threshold check
against the three common rails (12V/5V/3.3V, matched by sensor-name hints since LibreHardwareMonitorLib
doesn't standardize voltage sensor names any more than it standardizes CPU/battery sensor names) -
`SensorReading.IsVoltageOutOfSpec` is stamped on by a new `EnergyThermalsViewModel.WithVoltageSpecCheck`
as the Voltages collection is built, the same "set by the ViewModel, not SensorMonitorService"
shape `SessionMin`/`SessionMax` already established. Null (not false) for any rail name this app
doesn't confidently recognize, so an unrecognized auxiliary rail is never falsely flagged - the
Voltages card tints only a confirmed `true` red.

**Optional shared-token remote-monitor check**: `RemoteMonitorSettings.Token` (persisted like every
other opt-in toggle) plus a settable `RemoteMonitorService.RequiredToken` - when set, every request
needs a matching `?token=...` query-string parameter or gets a bare 401 (no body, so a scan gets no
free confirmation the endpoint even exists); unset (the default) behaves exactly as the endpoint
always has. The served dashboard page's own polling JS forwards `location.search` onto every
`/metrics.json` fetch, so a page opened once with the right token keeps working with no separate
client-side token entry. Explicitly documented in the Settings drawer (and the status line's
suggested URL, which now includes the token) as still not real authentication - a plain-text
query-string token over unencrypted `HttpListener` HTTP is visible to anything on the LAN path,
just a minimal step up from the endpoint's original fully-open design.

**"Pending reboot" baseline rollup (Summary)**: a pure derived correlation, no new registry reads -
`RefreshHealthIssues` (the Health Check card's existing rule engine) now cross-references
`SnapshotDiff` (already computed by `CompareSnapshot`) against `SystemSpecsService.ReadRebootPending`
(already read by Round 11's own reboot-pending rule, right above this one) and, only when *both*
are true, adds one more Health Check line naming how many changes since the last baseline comparison
may still be waiting on that pending restart. Silent whenever no baseline comparison has been run
this session (`SnapshotDiff` is null until then) - a reboot-pending flag with no diff on hand says
nothing about *which* change caused it, so this rule doesn't imply a link it can't actually show.

**Clipboard "copy summary" (Summary)**: `SummaryViewModel.CopySummaryCommand` builds a handful of
plain-text lines (OS/CPU/RAM/GPU/uptime/current load/a short Health Check digest) and puts them on
the clipboard via `System.Windows.Clipboard.SetText` - genuinely a few lines, not the full
Markdown/HTML report's content reformatted as text, for pasting straight into a chat message or a
forum/support-ticket reply without attaching or even generating a file.

**Configurable per-tab poll interval**: a new `Models/PollIntervalSettings.cs` +
`Services/PollIntervalSettingsService.cs` (`poll-intervals.json`, same shape as every other
settings file) backs a `PollIntervalSeconds` property added to the four ViewModels that actually
own a `DispatcherTimer` per CLAUDE.md's own Architecture section: `ProcessesViewModel`, the shared
`PerformanceViewModel` (whose slider is therefore the single interval knob for the CPU/Memory/
Storage/Network thin-wrapper tabs too, not four independent ones - consistent with the "one shared
sampler" model those tabs are built around), `ServicesViewModel`, and `EnergyThermalsViewModel`.
`StartupViewModel`/`SystemSpecsViewModel`/`StabilityViewModel` are deliberately excluded - they're
on-demand (an initial load plus a manual Refresh, no timer at all, per their own existing remarks),
so there's no interval to make configurable; the assignment's own guidance named `StartupViewModel`
as a candidate, but this is a factual correction based on how the codebase actually works, not an
oversight. Each setter reloads `PollIntervalSettingsService.Load()` fresh and saves back
immediately after mutating only its own field (never keeping a long-lived cached copy), so two
tabs' sliders changed in the same session can never clobber each other's saved value in the shared
JSON file. Defaults are unchanged from every prior round's hardcoded interval.

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
