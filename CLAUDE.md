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
