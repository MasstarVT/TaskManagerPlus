# Task Manager Plus

A Windows Task Manager replacement built in C# / WPF (.NET 8), with a TMOG/
HWiNFO-inspired depth the built-in one doesn't offer: real hardware sensors,
per-subsystem tabs, live theming, CSV logging, and a lot of "what's actually
wrong with this PC" diagnostics.

## Features

- **Summary** — a dashboard with live CPU/RAM charts, top-process cards, a
  rule-based Health Check list, configurable threshold alerts, snapshot
  baseline/diff, and one-click Markdown/HTML diagnostic reports.
- **CPU** — total & per-core usage, clock speed (base × `% Processor
  Performance`, the same way Task Manager computes it), NUMA/P-core-E-core
  topology, SMT sibling pairing, core parking, C-state residency, a turbo-
  boost histogram, and thermal-throttle/power-limit flags.
- **Memory** — Available/Committed/Cached breakdown, page faults, standby
  list, kernel pool, page file, and top memory/pool-consuming processes.
- **Storage** — activity, queue length/latency bottleneck diagnostics, SSD
  wear, HDD fragmentation analysis, and on-demand SMART details.
- **Network** — throughput, adapter errors, gateway/DNS reachability,
  active connections by process, Wi-Fi signal, and on-demand traceroute/
  jitter tests.
- **GPU** — per-engine utilization, VRAM, driver/WDDM version, and which
  processes are using it.
- **Energy & Thermals** — real temperature/fan/voltage/power sensors (via
  LibreHardwareMonitorLib), fan curve, battery health, and power-plan info.
- **Processes** — live CPU%/memory/threads/handles for every process, a
  process tree view, priority/affinity/suspend controls, and per-file
  signature checks. End a single process or its whole tree.
- **Services** — status, startup type, dependency graph, recovery actions,
  and "failed to start" detection for every Windows service.
- **Startup** — everything registered to launch at sign-in (registry Run
  keys + Startup folders) plus Scheduled Tasks, with measured logon delay
  and enable/disable.
- **System Specs** — hardware inventory, security posture (Secure Boot/
  TPM/VBS), disk health, installed software, and outdated-driver hints.
- **Stability** — event-log-based crash/TDR/minidump diagnostics and a
  computed stability index.
- **Theming** — six palette families (Dark/Light/Green/Amber/Blue/
  Monochrome) plus High Contrast, a saturation slider, and per-metric
  accent colors. Click the ⊙ button in the tab strip to open the Colors
  panel; applies live and saves to `%AppData%\TaskManagerPlus\theme.json`.
- **Logging** — a footer Start/Stop Logging control writes every metric to
  CSV (one row/second, HWiNFO-style), with rotation and gzip cleanup.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Running it

Ending other users' processes and controlling services needs elevation, so
the app requests administrator rights on launch (one UAC prompt at startup
instead of one per action).

```bash
dotnet run --project src/TaskManagerPlus
```

Or open `TaskManagerPlus.sln` in Visual Studio / Rider and hit Run — Visual
Studio's debugger already runs elevated if VS itself is elevated, otherwise
you'll get the UAC prompt.

To build a standalone Release build:

```bash
dotnet build -c Release
```

The executable will be at
`src/TaskManagerPlus/bin/Release/net8.0-windows/TaskManagerPlus.exe`. Running
it directly (double-click or from Explorer) will trigger the UAC prompt since
it's marked `requireAdministrator` in `app.manifest`.

## Troubleshooting: "Application Control policy has blocked this file"

If the app was working and then suddenly won't launch (crashes instantly,
no window), check Windows Security → **App & browser control** → **Smart
App Control settings**. If it's **On**, it's blocking this locally-built,
unsigned executable — that's normal for any dev build that isn't distributed
through the Microsoft Store or signed by a certificate with an established
reputation. Two options:

- Turn Smart App Control **Off**. ⚠️ Once turned off it can't be turned back
  on without a full OS reset, so know that going in.
- Sign the build with the included dev certificate scripts (below). This
  removes the "Unknown Publisher" label and gives the exe a valid, locally
  trusted signature, but **does not** satisfy Smart App Control — that
  feature blocks on reputation, not merely "is this file signed". Turning it
  off is the only real fix for running unsigned local builds.

### Signing local builds (optional)

```powershell
# One-time setup: creates + trusts a self-signed cert for the current user only.
.\scripts\New-DevCertificate.ps1

# After each Release build:
dotnet build -c Release
.\scripts\Sign-Release.ps1
```

## Project layout

```
src/TaskManagerPlus/
  Models/       Data classes for rows shown in the UI (ProcessRow, ServiceRow, ...)
  Services/     The actual system-interaction code (WMI, performance counters,
                ServiceController, registry, native interop) - no UI dependencies
  ViewModels/   One view model per tab; most poll on a timer, a few (Startup,
                System Specs, Stability) load on demand
  Views/        XAML for each tab
  Converters/   Value converters for formatting (bytes, percentages, ...)
  Themes/       Theme resource dictionary (palettes, tab strip, control styles)
```

See [CLAUDE.md](CLAUDE.md) for the fuller architecture writeup and the
conventions the codebase follows.

## Notes on how a few things work

- **CPU clock speed**: Windows doesn't expose "current" clock speed directly.
  It's derived the same way the real Task Manager does it: read the CPU's
  rated base clock once via WMI (`Win32_Processor.MaxClockSpeed`), then each
  tick read the `% Processor Performance` counter and multiply — that counter
  reflects turbo boost / throttling in real time.
- **Startup enable/disable**: Windows doesn't delete the registry Run value or
  move the shortcut when you disable a startup item — it flips a binary flag
  under `...\Explorer\StartupApproved\Run` (or `\StartupApproved\StartupFolder`
  for Startup-folder shortcuts) that Explorer checks before launching things at
  sign-in. This app does the same, so it stays consistent with what Explorer
  and the built-in Task Manager show.
- **Sensors** (temperature/fan/voltage/power): Windows has no reliable API for
  these, so the Energy & Thermals tab is the one place this app takes a
  third-party dependency, LibreHardwareMonitorLib (MPL-2.0).
