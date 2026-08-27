# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8): a Summary dashboard, per-subsystem CPU/Memory/Storage/Network/
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
  event-log query isn't cheap enough to repeat on a tick. `MainViewModel`
  composes all of them plus settings-drawer state (`IsSettingsOpen`) and
  elevation status (`IsElevated`, checked once via `WindowsPrincipal`).
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
horizontally so all 11 tabs stay reachable at narrower window widths instead
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
