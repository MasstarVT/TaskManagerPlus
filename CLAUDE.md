# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8). Seventeen top-level tabs (Summary, CPU, Memory, Storage, Network,
GPU, Energy & Thermals, Responsiveness, Processes, Services, Startup,
System Specs, Stability, Security, Windows Health, Events, Devices &
Drivers), live color-theming (six palette families + saturation + high-contrast +
color-blind-safe alerts), a CSV/HTML/Markdown logging & reporting system,
a system tray icon with a global hotkey, and an optional LAN-visible
remote monitor endpoint. Navigation is a TMOG-style top horizontal tab
strip (`TabStripPlacement="Top"`), matching the visual/IA style of
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
  `StartupViewModel`, `SystemSpecsViewModel`, `StabilityViewModel`,
  `SecurityViewModel`, `WindowsHealthViewModel`, `EventsViewModel`, and
  `DevicesDriversViewModel` are on-demand instead (an initial load plus a
  manual Refresh command, or purely selection/button-driven with no
  initial load at all for `EventsViewModel`) since their underlying
  queries (registry/WMI inventory sweeps, event-log scans, DISM/SFC runs)
  aren't cheap enough to repeat on a tick. `SecurityViewModel`
  (Round 14, #801-900) is the most extreme case of
  this — a single large on-demand ViewModel piling up one
  `ObservableCollection`/`IsLoading` flag/`AsyncRelayCommand` trio per
  section (Persistence, File trust, Process activity, Protection status,
  Platform security, Network exposure, Accounts, Bloatware, Cleanup, ...),
  each gated behind its own explicit Scan/Refresh button rather than one
  shared timer, matching `StartupViewModel`'s precedent of several
  unrelated on-demand sections coexisting in one VM. Its backing services
  live under `Services/` with no single umbrella class — `AutorunsService`
  (persistence-location enumeration), `DefenderService`,
  `PlatformSecurityService`, `FirewallService`/`ShareAuditService`/
  `RemoteManagementExposureService`/`HostsFileAuditService`/
  `DnsPostureService`/`CertificateStoreAuditService`, `AccountSecurityService`,
  `BloatwareInventoryService`/`OemCleanupService`,
  `BrowserHijackCheckService`, plus supporting services for hashing
  (`FileHashService`), quarantine (`QuarantineService`), the action journal
  (`SecurityActionJournalService`), System Restore points
  (`RestorePointService`), and evidence bundles (`SecurityEvidenceBundleService`).
  Every heuristic in this tab is explicitly framed as "quick flag, not a
  verdict" in both code comments and UI copy — see the house rule below.
  `DevicesDriversViewModel` is the largest on-demand ViewModel besides
  `SecurityViewModel` — it composes several independently-loaded sections
  (driver inventory, device tree, resources/power, driver store, filter
  drivers, Driver Verifier) behind a handful of `Is*ViewActive` bool
  properties that switch which section's XAML is visible, rather than a
  real `TabControl`/enum — a pragmatic pattern that has scaled to four
  sections but would be worth promoting to an enum or nested TabControl if
  a fifth is added. Its own Driver Verifier controls call
  `DriverVerifierControlService`, a separate, independently-built service
  from the Stability tab's `DriverVerifierService` guided wizard — see
  that class's own remarks for why the two coexist rather than share one
  implementation.
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
  `StressTestViewModel` is the one ViewModel that isn't one-per-tab: it
  backs a panel (`StressTestPanel.xaml`) embedded inside `EnergyThermalsView`
  via the usual cross-ViewModel `RelativeSource AncestorType=Window` binding
  rather than getting its own `TabItem`, since a stress-test suite is a
  panel-sized feature, not a new subsystem to monitor. It's on-demand only
  (Start/Stop, never a timer) and, because it deliberately loads CPU/GPU/
  memory/disk for real, always runs every sample through
  `StressTestSafetyMonitor` — an unconditional, unthrottled temperature-
  ceiling (and optional WHEA/TDR-delta) abort check with no setting able to
  disable the check itself, only where its thresholds sit. Any future
  feature that drives real hardware load on demand should follow this same
  "always-on safety monitor, no opt-out" shape rather than relying on the
  user-configured duration alone.
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

### Registry change journal

`RegistryChangeJournalService` (`registry-changes.json` under
`AppPaths.SettingsDirectory`, same fail-silent-to-empty-history pattern as
every other settings file) is an append-only log of registry writes this
app makes on the user's behalf — each entry records the key, value name,
previous data, new data and a timestamp, with a per-entry programmatic
undo and an export-as-`.reg` of the prior state. It backs a "Changes made
by this app" list on the **Windows Health** tab (and a compact undo-only
mirror in the settings drawer). It is deliberately **not** wired into every
registry-writing call in the app — introduced late in the Windows Health
work, it currently covers `StartupManagerService`'s approval-flag flip,
`FastStartupService`'s `HiberbootEnabled` write, `PrefetchAuditService`'s
restore-to-defaults, and the Windows Health tab's own registry writes
(RegBack `EnablePeriodicBackup`, the environment-variable editor). Other
registry-writing services (`ServiceControlService`, `WindowsUpdatePolicyService`,
BCD-adjacent writes, etc.) still write directly. When you touch one of
those other write paths, prefer routing it through the journal too rather
than leaving it as a silent direct write — but don't treat the absence of
journaling elsewhere as a bug to fix in an unrelated change.

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

### Events tab (nested sub-ViewModels for toggleable panels)

The Events tab (`EventsViewModel`/`EventsView.xaml`) is a real Event Viewer
replacement (channel tree, paged/virtualized grid via `EventLogReader` +
`EventLogQuery`, XPath filter builder, friendly/raw-XML detail pane, live
tail via `EventLogWatcher`, saved filters) backed mainly by
`EventLogExplorerService` — a separate, richer reader from the original
`EventLogService` the Stability tab still uses for its own fixed digest;
the two intentionally don't share one service, since the Events tab's
paging/live-tail/multi-channel needs are a different shape from Stability's
"read the last 60 Critical/Error rows" query. Several heavier features
(ETW capture via `logman`/`wpr`/`tracerpt`, servicing-log parsing) are
**toggleable side panels with their own nested sub-ViewModel** —
`EtwCaptureViewModel`/`EtwCapturePanel.xaml` and
`ServicingLogsViewModel`/`ServicingLogsPanel.xaml` — composed into
`EventsViewModel` as plain properties (`public EtwCaptureViewModel Etw {
get; }`) the same way `MainViewModel` composes top-level tab ViewModels,
rather than flattening everything onto one giant `EventsViewModel`. Use
this nested-sub-ViewModel-plus-toggle-panel shape for the next large,
mostly-self-contained feature block that belongs on an existing tab instead
of growing that tab's ViewModel further.

Known-bad-event explanations use a **bundled-resource-plus-user-override**
pattern, distinct from the plain settings-file pattern below:
`EventKnowledgeBaseService` loads a read-only, ships-with-the-app
`Resources/EventKnowledgeBase.json` (embedded resource) keyed by
`provider|eventId`, then merges a user-editable `event-kb-overrides.json`
from `AppPaths.SettingsDirectory` on top (override replaces on collision,
otherwise adds) — the same "degrade gracefully, never fabricate" spirit as
everything else, but reusable anywhere a curated dataset needs to ship with
the app and still be user-extensible without a rebuild.

A registry or `wevtutil`-style write that isn't purely additive (examples:
WER `LocalDumps`, the `Reliability Analysis\WMI\WMIEnable` key,
`wevtutil sl` retention changes) follows a **confirm-backup-revert** pattern:
an explicit `MessageBox.Show` Yes/No confirmation stating the exact change
and its cost, the pre-change value(s) backed up to a small JSON file under
`AppPaths.SettingsDirectory` before writing, and a one-click revert command
that restores from that backup — see `WerReportService`'s LocalDumps toggle
for the reference implementation.

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
  authoritative: `SignatureCheckService` (Round 14, #836) runs a real
  `WinVerifyTrust` chain-and-catalog check (both embedded and catalog
  signatures, with a `CryptCATAdminEnumCatalogFromHash`-based catalog
  lookup — see its remarks for that check's own known limitation:
  hash-membership in *some* system catalog, not a full
  `WTD_CHOICE_CATALOG` re-verification), but revocation checking is off by
  default (never in a per-tick poll path) and only ever run on-demand with
  a hard timeout; high-privilege/duplicate-instance/memory-leak/
  thermal-throttle/power-limit/process-trust-name flags are pattern-matches
  on otherwise-ambiguous data, AV/mitigation-status reads use undocumented
  bitmask/registry conventions, and outdated-driver/BIOS-age checks are
  "worth a manual check," not a confirmed update available.
- **Settings persistence**: every persisted setting is a small JSON file
  under `AppPaths.SettingsDirectory` (`%AppData%\TaskManagerPlus` normally,
  or a `Settings` folder next to the exe in portable mode — see
  `Services/AppPaths.cs`, initialized once from `App.xaml.cs` before any
  other service runs). Each settings file fails silently to its type's
  defaults on a missing/corrupt file, same as `ThemeService`/`theme.json`.
  The Security tab added several more under the same directory:
  `autoruns-baseline.json` (known-good persistence snapshot, #803),
  `signature-cache.json` (per-path signature verification cache keyed by
  path+size+last-write-time, #844), and `security-actions.json` (the
  owner-initiated-cleanup action journal, #899) — plus two subfolders,
  `Quarantine\<timestamp>\` (moved-never-deleted flagged files, #899) and
  `EvidenceBundles\<timestamp>\` (exported report+diff+hashes+event
  excerpts for a helper/forum post, #900).
- **Maintained reference lists (embedded seed → editable settings-dir
  copy)**: a curated, updatable-without-a-rebuild reference list (the pool
  tag dictionary, the known-problem-driver list) ships as an embedded
  resource under `Resources/` and is copied to a same-named file under
  `AppPaths.SettingsDirectory` the first time it's needed if no copy exists
  there yet; the service then always reads from the settings-dir copy, so a
  user (or a future update mechanism) can replace it without a rebuild.
  Both existing lists are explicitly labelled in-code and in the UI as a
  curated/partial subset, never a complete authority.
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
- **True per-process network bandwidth**: no counter or WMI class attributes
  bytes to a PID in real time, so `ProcessBandwidthEtwService` is the other
  place the app takes a third-party dependency
  (`Microsoft.Diagnostics.Tracing.TraceEvent`, MIT) to run a short,
  explicitly user-initiated ETW session on the `Microsoft-Windows-Kernel-
  Network` provider and aggregate `KERNEL_NETWORK_TASK_TCPIP` events by PID.
  Never runs unattended — same on-demand-only rule as any other expensive
  capture, with a visible "capture running" indicator while it's active.
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
