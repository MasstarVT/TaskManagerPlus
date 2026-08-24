# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Task Manager Plus — a Windows Task Manager replacement written in C# / WPF
(.NET 8): Processes, Performance (live charts), Services, Startup manager,
System specs, and a live color-theming system.

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
  disposes it in `Dispose()`. `MainViewModel` composes all of them plus
  settings-drawer state (`IsSettingsOpen`) and elevation status
  (`IsElevated`, checked once via `WindowsPrincipal`).
- **Views/** — XAML + minimal code-behind per tab, under `MainWindow.xaml`'s
  tab control.
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
`ThemeViewModel.ColorsChanged` to `PerformanceViewModel.ApplyColors` so
chart colors update live when the user changes them in the Colors panel;
otherwise tabs don't know about each other.

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
