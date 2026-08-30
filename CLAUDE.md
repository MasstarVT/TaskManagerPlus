# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8). Eighteen tabs, grouped into six top-level groups — Summary;
Hardware (CPU, Memory, Storage, Network, GPU, Energy & Thermals); Activity
(Processes, Services, Startup); System (System, Devices & Drivers, Windows
Health); Diagnostics (Troubleshoot, Responsiveness, Stability, Events);
Security. Note the System group's first leaf is also headed by the single
word "System", though its ViewModel/view are named SystemSpecs* — `--tab`
and SelectTabByName match header text, so "System Specs" finds nothing.
Also live color-theming (six palette families + saturation + high-contrast
+ color-blind-safe alerts), a CSV/HTML/Markdown logging & reporting
system, a system tray icon with a global hotkey, and an optional
LAN-visible remote monitor endpoint. Navigation is a TMOG-style top
horizontal tab strip (`TabStripPlacement="Top"`), matching the visual/IA style of
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
src/TaskManagerPlus/bin/Release/net8.0-windows10.0.19041.0/TaskManagerPlus.exe
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
  real `TabControl`/enum — a pragmatic pattern that predates the section
  chip bar (see "Three levels of navigation") and is now the one tab that
  switches sections differently from every other. Its buttons at least use
  `SectionChipButton` so they look identical to real chips; a new section
  here is the point at which to convert it to a `SectionTabControl` rather
  than adding a fifth bool. Its own Driver Verifier controls call
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
  adjustment), `Formatting` (shared byte/rate and TimeSpan formatting
  helpers), `CsvLine` (quoted-CSV line splitter for shelled-out tools'
  CSV output) — the entire "MVVM framework" for this project, intentionally
  hand-rolled.
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

### Three levels of navigation

Navigation is three nested levels, each a `TabControl` with a deliberately
different shape so a glance tells you which one you are looking at:

1. **Groups** — the strip at the top of the window. Icon + label, filled
   pill. Six of them. Uses the *implicit* `TabControl`/`TabItem` styles.
2. **Tabs** — the row inside a group (Hardware → CPU / Memory / ...). Text
   only, underline indicator, page-coloured. `GroupTabControl`, whose
   `ItemContainerStyle` supplies `GroupTabItem`, so leaf tabs stay plain
   `<TabItem Header="...">`. A group with one leaf (Summary, Security)
   hosts its view directly instead of rendering a one-item row — and takes
   that leaf's own name, since the name is what `--tab` addresses.
3. **Sections** — a chip bar at the top of a tab's content.
   `SectionTabControl` + `SectionTabItem`, same `ItemContainerStyle` trick.

All three levels share **one** strip `ControlTemplate` (`TabStripTemplate`
in `Dark.xaml`), parameterized per level via the `Common/TabStrip.cs`
attached properties (header background/border, strip margin) — the items
host is a horizontal `StackPanel` inside a `ScrollViewer` (scrollbar
`Hidden`, since an `Auto` bar appearing re-measures the whole content row),
never a `TabPanel`, which clips each tab by its own `Margin` during
arrange. The `*TabItem` styles are declared *before* the `*TabControl`
styles that consume them so `ItemContainerStyle` stays `StaticResource`.

Level 3 exists because tabs grew by appending one card per backlog item to
a single `ScrollViewer > StackPanel`: Stability reached 91 stacked cards in
one unbroken scroll, Energy & Thermals 63, Responsiveness 64, Storage 43.
Wrapping runs of cards in section `TabItem`s needs **no ViewModel change** —
no `IsXxxViewActive` bool, no new commands — and WPF handles selection,
keyboard nav and automation. Prefer this over adding another bool-per-section
switch. A section's content starts with a
`<ScrollViewer Style="{StaticResource SectionBody}">` wrapper (the shared
gutters), and the chip geometry lives in the `ChipCornerRadius`/
`ChipPadding` tokens shared by `SectionTabItem` and `SectionChipButton`.
`DevicesDriversView` is the one tab that still switches sections with
ViewModel bools (it predates the chip bar); its buttons use
`SectionChipButton` with `Tag` bound to the section's `Is*ViewActive` bool —
the style's own trigger paints the accent active chip (and keeps it through
hover), so no per-button accent setters.

**Every leaf tab keeps its original header text**, because that text is the
address used by `--tab`, Ctrl+1..9, the Ctrl+K palette and every cross-tab
jump. `SelectTabByName(name, section?)` walks the group tree breadth-first
(so a group beats a same-named leaf), selects the whole ancestor path,
outermost first, then normalizes everything *below* the match to its first
leaf (so selecting a group is never a silent no-op when a different leaf
was open), and returns whether the name resolved. It walks
`TabItem.Content`, not the visual tree, because a `TabControl` only
realizes the selected tab's content — the nested `TabControl`s are
XAML-declared objects and are reachable whether or not their group has ever
been shown. The optional `section` argument extends the address to
`tab › section`: it switches the leaf's `SectionTabControl` (found by style
identity in the view's logical tree) to the chip with that header — use it
for any cross-tab jump whose target card lives in a section, or the user
lands on the tab's first section instead of the advertised card. The Ctrl+K
palette's tab/section destinations are generated from this same tab tree at
startup (`MainWindow.EnumerateTabDestinations` →
`GlobalSearchViewModel.SetTabDestinations`), so a new tab or section is
searchable with no list to maintain. If you add a group, do not rename the
leaves inside it. A `TabItem` whose Header must carry non-string content
(the Stability dump-alert badge, and its mirror on the Diagnostics group
header so the alert is visible from other groups) needs
`AutomationProperties.Name` — see the XAML rule below.

### UI shell (top tab strip, icons, footer, tray)

`MainWindow.xaml`'s `TabControl` is re-templated (in `Themes/Dark.xaml`) as
a TMOG-style top icon+label tab strip — `TabStripPlacement="Top"` gets the
horizontal layout for free, and the strip scrolls horizontally so all tabs
stay reachable at narrower widths. Each **level-1 group** `TabItem.Tag`
holds a small hand-drawn `Viewbox`/`Canvas` glyph styled via
`NavIconStroke`/`NavIconFill` (bound to the ancestor `TabItem.Foreground`,
so icons recolor automatically with the theme); only the level-1 template
presents `Tag`, so don't give a nested leaf tab one — it would be parsed
and allocated but never rendered. The "Colors" button and other footer
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
re-theme.** `ThemeViewModel.FontScale` doesn't touch font sizes; it drives
a `ScaleTransform` via `LayoutTransform` on the whole `TabControl`. The
color-blind-safe alert set picks its light-vs-dark variant with
`ColorMath.WcagRelativeLuminance` (the sRGB-linearized WCAG luminance) of
the *saturation-adjusted* background — the plain `RelativeLuminance` is a
cheap gamma-space average whose midpoint doesn't match WCAG's, fine for the
accent-foreground flip but not for anything tuned against contrast ratios.

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

A chart with a visible legend (`LegendPosition="Bottom"`) also needs
`common:ChartTheme.ThemedLegend="True"` — LiveCharts' default legend text
paint is near-black and invisible on the dark cards (the GPU per-engine
legend rendered as colored dashes with no labels). SkiaSharp paints live
outside WPF's resource system, so this is an attached property setting
`LegendTextPaint` in code (`Common/ChartTheme.cs`), using the same fixed
gray as the ViewModels' `AxisTextColor` axis labels.

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

### Troubleshoot tab (guided diagnostics, rules engine, remediation)

The Troubleshoot tab is a landing page of symptom cards (`TroubleshootViewModel`,
`Services/TroubleshootService.cs`) plus several sibling sub-pages reached by
buttons that swap the tab's content area — Timeline, Baselines, Background
Health, Changes (the change journal), Evidence Bundle, Glossary, Network
Activity — rather than being separate top-level tabs of their own. Follow
this same swap-a-content-area pattern (not a new `TabItem`) for anything
that belongs "inside" guided diagnostics. A symptom branch is a declarative
`Models/TroubleshootBranchDefinition.cs` (an ordered list of `DiagnosticStep`
objects with per-step timeout and a `ShouldRun` predicate that can read an
earlier step's result), evaluated by one generic runner — adding a symptom
is a data change (`RegisterBranch(...)`), not a new hand-written procedural
method.

The Summary tab's Health Check card is now driven by `RulesEngineService`
reading JSON rule packs from `AppPaths.SettingsDirectory\Rules\*.json`
(seeded from a built-in pack) instead of a hardcoded `if` chain — a rule is
metric-bag conditions (`{"metric":...,"op":...,"value":...}` plus
`all`/`any`/`not`, no embedded scripting engine) plus presentation metadata
(severity, confidence, docs URL, plain-English body, applicable remediation
action ids). **Any new Health-Check-style finding should be added as a rule
in the pack, not as a new `if` in `SummaryViewModel`.** `HealthIssue` is the
one finding shape rendered everywhere (Health Check card, Troubleshoot,
reports, evidence bundles) — don't add a second parallel finding type.

Every system-mutating action this app performs — service control, startup
toggles, process priority/affinity/suspend, power-plan changes, and the
`RemediationActionCatalog` fix-actions — is expected to append to
`Services/ChangeJournalService.cs`'s `change-journal.jsonl` (undoable
per-entry) and to respect `Services/ReadOnlyModeService.cs`'s app-wide
read-only switch in its command's `CanExecute`. A remediation action shows
its literal command line before running, offers a dry-run/preview command
where one exists, and offers a restore point + a `reg export` backup ahead
of a risky/registry-writing change — copy this shape for any new mutating
action rather than wiring a bare "run it" button.

`Services/BackgroundHealthCollectorService.cs` is a fifth always-on timer,
deliberately separate from the four in "Configurable poll intervals" below
and from the user-started CSV logging feature: low-frequency (60s default),
writes compact rows to a self-pruning `health-history.jsonl`, and
self-measures its own CPU/duration cost each cycle (visible in the
Background Health panel) with automatic backoff if a cycle runs long — the
explicit bar for this collector is that it must never be the thing that
makes the machine feel slow.

## Cross-cutting conventions

These recur across almost every feature added after the original five
tabs — worth knowing before adding something new rather than re-deriving
per file:

- **Prefer a known Windows tool/API over raw interop or an undocumented
  struct layout.** `schtasks.exe`, `sc.exe`, `vssadmin.exe`, `fsutil.exe`,
  `defrag.exe`, `tracert.exe`, `powercfg.exe`, `netsh` are all shelled out
  to and their text output parsed, rather than reimplementing what they do
  via COM/registry/native structs. The run-tool/capture-output/kill-on-
  timeout mechanics live in one shared `Services/ToolRunner` (the timeout
  kill is always `Kill(entireProcessTree: true)`); each service keeps only
  a thin adapter for its own timeout/sentinel/exit-code semantics — don't
  hand-roll a new `RunCaptured` copy. Raw P/Invoke (`CpuTopologyService`,
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
- **Palette colors must clear WCAG AA (4.5:1) against their own family's
  three surfaces.** `ThemeViewModel.Palettes` is the authority at runtime —
  `Dark.xaml`'s literal palette only mirrors the Dark entry for the moment
  before `ApplyPalette` first runs, so a color changed in one and not the
  other silently does nothing. Ten values across six families used to fail
  (`TextTertiary` in every family but High Contrast, worst 2.85:1 — and it
  is what the `FontCaption` step is colored with), as did the
  color-blind-safe triple, which used one fixed set on both light and dark
  surfaces and so was the *least* readable mode in the app. All were lifted
  in lightness with hue preserved; the color-blind set is now two
  Okabe-Ito-hued sets chosen by background luminance. Check new colors
  before adding them, and keep the primary/secondary/tertiary ramp visibly
  stepped rather than just individually compliant.

- **Type and spacing come from tokens in `Dark.xaml`, not literals.** The
  views once carried 1,889 hardcoded `FontSize` values across 18 distinct
  sizes (the most common body size was 11.5px), 179 distinct `Margin`
  values and 11 `CornerRadius` values. Those are collapsed onto a 7-step
  type scale (`FontCaption` 12 / `FontBody` 13 / `FontStrong` 13.5 /
  `FontSection` 15 / `FontTitle` 17 / `FontDisplay` 22 / `FontHero` 30)
  plus the spacing/radius tokens that have real consumers (`StackGapSm/Md/
  Lg`, `RadiusLg`, `ChipCornerRadius`/`ChipPadding`). There are **zero**
  literal font sizes in `Views/` — and none left in `Dark.xaml`'s own
  `DataGrid`/`TabItem`/`ToolTip` styles either, which sat below the 12px
  floor until suggestions.md #1005. Use `FontSize="{StaticResource
  FontBody}"`; a literal `FontSize="11.5"` is exactly what the scale exists
  to stop coming back. (An earlier revision also shipped `Text*` TextBlock
  styles wrapping these keys; they gained zero consumers — every view
  inlines the attributes — so #1010 deleted them rather than leave a
  convention the codebase doesn't follow.) Unlike the palette these are
  plain `StaticResource`-consumed doubles — they never change at runtime,
  so they don't need the `DynamicResource` treatment the color keys do.

- **XAML: `<Run Text="{Binding ...}">` must carry `Mode=OneWay`.** Unlike
  `TextBlock.Text`, `Run.Text` sets `BindsTwoWayByDefault`, so a Run bound to
  a get-only property throws *"A TwoWay or OneWayToSource binding cannot work
  on the read-only property ..."* the moment that view loads. Every one of the
  ~1,100 Run bindings in this codebase is `Mode=OneWay` (two are `OneTime`);
  a Run is display-only and never writes back, so OneWay is correct even when
  the source property does have a setter. Add it to any new one. A bare
  `{Binding}` (bind the DataContext itself) takes `{Binding Mode=OneWay}` —
  no comma, since the positional path slot is empty.

- **XAML: a style used by more than one view belongs in `Themes/Dark.xaml`.**
  `StaticResource` resolves against the *referencing* element's own lookup
  chain, not globally, so a key declared in some `UserControl.Resources` is
  invisible to every other view — and the failure is not a graceful one: it
  throws while the referencing view's BAML loads, which (for anything reached
  from `MainWindow.xaml`) means MainWindow's constructor throws and the app
  starts with an error dialog and no main window. `ColorRowLabel` sat in
  `SettingsPanel.xaml`'s own resources while `StabilityView.xaml` referenced
  it 20 times, and did exactly that. Note this is *not* symmetric with the
  `DynamicResource` rule under "Theming" below: a missing DynamicResource key
  silently resolves to null (a brush that renders transparent), a missing
  StaticResource key throws.

- **XAML: a local `<Style TargetType="...">` on a control type Dark.xaml
  themes implicitly (Button, ToggleButton, ComboBox, TextBox, DataGrid, ...)
  must carry `BasedOn="{StaticResource {x:Type ...}}"`.** A local style
  *replaces* the implicit one, so a bare visibility-toggle style silently
  reverts that control to Windows' default light chrome — the Events tab's
  two DataGrids each carried a bare style and rendered as a white grid in
  the middle of the dark theme, and ~20 Button/TextBox/DataGrid toggles
  across the views had the same bug. Dark.xaml now also themes `ComboBox`/
  `ComboBoxItem` and `ToggleButton` (both custom-templated; the default
  chrome ignores Background setters and was unreadable against the dark
  palette — CheckBox has its own type key and is deliberately left stock).
  No ComboBox in the app is `IsEditable`; the template omits
  `PART_EditableTextBox`, so add that part before making one editable.

- **DataGrid text cells ellipsize instead of hard-clipping.** The implicit
  DataGrid style carries a `Style.Resources` TextBlock style setting
  `TextTrimming="CharacterEllipsis"` for every grid, and `CellTextTrim`
  (ellipsis + full-value hover tooltip) is the ElementStyle for columns
  whose values are routinely wider than the column (paths, commands, app
  names — see the Startup grid). A cell template that sets its own local
  TextBlock style loses the implicit trimming (same BasedOn rule as above)
  and needs `TextTrimming` inline.

- **DataGrid: never mix star-sized columns into a grid whose fixed columns
  alone exceed the viewport.** With any star column present, DataGrid fits
  ALL columns into the viewport instead of scrolling horizontally — the
  Processes grid (~40 fixed columns plus star Name/Path) compressed every
  column to ~28px single-letter stubs. All-fixed widths overflow into a
  normal horizontal scrollbar. Stars are fine in small grids that always
  fit (the module list, Events' burst grid).

- **A fixed toolbar row that can outgrow the window wraps instead.** The
  Services filter/action row was an 11-column Grid that clipped its
  trailing buttons at the default window width; it's a `WrapPanel` now.
  Prefer `WrapPanel` (as most tabs' button rows already do) for any row of
  filters/actions that isn't guaranteed to fit at 1266px.

- **XAML: a `TabItem` whose `Header` isn't a string needs
  `AutomationProperties.Name`.** `SelectTabByName` (the `--tab` launch flag,
  Ctrl+1..9, the Ctrl+K command palette, every cross-tab jump) matches on
  header text and falls back to `AutomationProperties.Name`; UI Automation
  and screen readers read the same value. Only the Stability tab is like this
  today — its Header is a StackPanel carrying the new-dump alert badge — and
  without the attached name it was both unreachable by name and announced as
  the literal `"System.Windows.Controls.TabItem Header: Content:"`.

- **Parse tool output with `InvariantCulture`; parse user input with the
  current culture.** Everything shelled-out tools emit (`powercfg`, DISM,
  `fltmc`, `defrag`, battery/SRUM reports, WMI strings) is machine-formatted,
  so `double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, ...)`
  is the rule — on a comma-decimal locale (de-DE, fr-FR, ...) a bare
  `double.Parse("42.5")` reads `.` as a *group* separator and silently returns
  4250, with no exception to notice. The exception is a value the user typed
  into a TextBox (e.g. the PSU wattage box), which should follow their locale.

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
- **Performance counters are read one CATEGORY per tick, never one
  `PerformanceCounter` object per value** — every raw `NextValue()`
  re-reads the *entire* category from the provider. The counter-per-
  instance version of `ProcessPerfCounterService` cost 4–9 **seconds** per
  tick on the ~350-instance "Process" category alone (the process list
  took ~10s to first populate while the column it fed showed nothing), and
  `HardwareMonitorService`'s ~130 individual counters cost ~70ms of every
  1-second tick. `Services/CategorySampler.cs` is the shared
  implementation (`ReadCategory()` + `CounterSample.Calculate`, previous
  samples kept per (counter, instance) for rate math — a rate's first
  sight reads 0, replacing the old priming-`NextValue()` trick, so ctors
  `Tick()` once to prime); `ProcessPerfCounterService` and
  `GpuMonitorService` carry their own equivalent for pid-keyed reads.
  When touching these, verify value parity the way the rewrite did: run
  old and new implementations *simultaneously* in a harness and diff the
  snapshot fields.

- **An `async` method with no real `await` before its expensive work runs
  that work on the CALLER'S thread.** `BitLockerService.ReadAllAsync` was
  async-shaped but never truly awaited, so `StorageViewModel`'s
  constructor ran its full WMI conversation on the UI thread during
  startup — ~10s of the app's ~14.5s time-to-first-window (elevated
  launches were "mysteriously" faster only because that WMI namespace
  answers quickly when access succeeds). Its `Task.Run` wrapper is
  load-bearing; give any similar fire-and-forget constructor load the
  same treatment, and check new ones with a startup trace
  (`dotnet-trace collect -- <exe>`) rather than assuming `async` means
  off-thread.

- **The Services poll caches rarely-changing metadata** (Description,
  DelayedAutostart, both dependency directions — see
  `ServiceControlService.StaticServiceInfo`; the cache refreshes on a slow
  cadence, every 30th tick, so an external `sc config` change lands within
  about a minute) and reads pid + last exit
  code for all services from one `EnumServicesStatusEx` syscall
  (`NativeServiceStatusReader`, WMI fallback inside, values verified
  bit-identical to `Win32_Service`); the logon-account WMI query runs
  every 30th tick, not every tick. Together: ~450ms → ~80ms per tick.
- **Signature checks on the polled path are non-blocking.**
  `SignatureCheckService.GetResultOrQueue` returns the cached result or
  queues the path for one background worker and reports Unknown until the
  verify lands; `ProcessesViewModel.MergeInto` reassigns
  `SignatureStatus`/`Publisher`/`IsSelfSigned` every tick so the real
  values appear once computed. Indeterminate (Unknown) results are never
  persisted to `signature-cache.json` and get only a short in-memory TTL,
  so a transient verify failure can't poison future sessions. The
  synchronous `GetResult` stays for button-driven callers (Startup
  refresh, Security scans) and deliberately re-verifies rather than
  trusting the cross-session disk cache, since the `path|size|mtime` cache
  key is forgeable. This matters
  mainly when elevated: `MainModule.FileName` then resolves for
  essentially every process, and the first tick used to verify 150+
  uncached system binaries synchronously before the first row appeared.
- **Elevation**: the whole app runs elevated (`app.manifest` →
  `requireAdministrator`) rather than elevating per-action, so ending other
  users' processes and controlling services just work without extra
  prompts.

- **`EnableUnsafeBinaryFormatterSerialization` is explicitly off.**
  `UseWindowsForms` (present only for the tray icon's `NotifyIcon`) turns it
  on by default for WinForms clipboard/drag-drop compat. Nothing here uses
  `BinaryFormatter`, and the only clipboard calls are `Clipboard.SetText`,
  which doesn't go through it — so leaving it on was just deserialization
  attack surface on a process that runs elevated by manifest. If a future
  feature genuinely needs `Clipboard.SetData` with a custom object, or
  WinForms drag-drop, that is what to revisit — not the flag.

- **Target framework carries an explicit Windows API version**
  (`net8.0-windows10.0.19041.0`, not a bare `net8.0-windows`). This is
  load-bearing, not incidental: `net8.0-windows` expands to
  `net8.0-windows7.0`, and `SkiaSharp.Views.WPF` (pulled in by LiveCharts)
  ships assets only under `net462`, `net6.0-windows10.0.19041` and
  `net8.0-windows10.0.19041`. `net8.0-windows7.0` matches none of the
  windows10 groups, so NuGet fell all the way back to the .NETFramework4.6.2
  group and the app silently compiled against the **net462**
  `SkiaSharp.Views.WPF`, a **net452** `GLWpfControl` and a **.NET 2.0**
  `OpenTK` — which is what the 11 `NU1701` warnings were reporting. Naming
  10.0.19041 selects the real net8.0 assets (OpenTK 4.3.0). Do not
  “simplify” this back to `net8.0-windows`; it reintroduces the fallback
  silently, and the only visible symptom is a set of NU1701 warnings that
  look cosmetic. The consequence is a Windows 10 2004 (build 19041) floor,
  declared via `SupportedOSPlatformVersion`, and it is also why the Release
  output path contains the full moniker (`scripts/Sign-Release.ps1`
  discovers the exe rather than hardcoding it).

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
