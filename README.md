# Task Manager Plus

A Windows Task Manager alternative built in C# / WPF (.NET 8), with a few things
the built-in one doesn't do as well: smooth live graphs (CPU usage *and* clock
speed, per-core breakdown, RAM, disk, network), and a Startup tab that flips
the same Windows flags Explorer itself uses.

## Features

- **Processes** — live CPU%, memory, threads, status, owner and path for every
  process. End a single process or its entire tree.
- **Performance** — real-time charts for total & per-core CPU usage, current
  clock speed (computed the same way Task Manager does: base clock ×
  `% Processor Performance`), RAM, disk activity, and network throughput, plus
  a system summary (process/thread/handle counts, uptime).
- **Services** — list every Windows service with status, startup type and
  owning PID; start, stop, or restart any of them.
- **Startup** — see everything registered to launch at sign-in (registry Run
  keys + Startup folders, current user and all users) and enable/disable
  individual entries.
- **Colors** — click the ⊙ button in the header to open the Colors panel and
  pick the accent color plus each Performance chart's color from swatches or
  a hex code. Applies live and is saved to `%AppData%\TaskManagerPlus\theme.json`.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (already
  installed on this machine)

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
                ServiceController, registry) - no UI dependencies
  ViewModels/   One view model per tab, each owns its own refresh timer
  Views/        XAML for each tab
  Converters/   Value converters for formatting (bytes, percentages, ...)
  Themes/       Dark theme resource dictionary
```

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
