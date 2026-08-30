---
name: run
description: Launch, drive, and screenshot Task Manager Plus. Use whenever the user asks to run, start, launch, or demo the app — and whenever a change needs verifying in the real app rather than just a build: screenshotting a tab, checking a theme/layout/XAML fix, confirming the window still loads after editing views or Dark.xaml, or sweeping all tabs for load crashes. Covers both the real elevated run (one UAC prompt) and a no-UAC asInvoker build for automated screenshot sweeps.
---

# Running Task Manager Plus

Two distinct modes. Pick by intent, not habit:

| Intent | Mode |
|---|---|
| The user wants to see/use the app | **Real run** (elevated, one UAC prompt, full data) |
| You need to verify UI, screenshot tabs, or automate anything | **Verification loop** (asInvoker build, no UAC, killable) |

The split exists because the app manifest is `requireAdministrator`: an
elevated instance can't be killed or sent input from a non-elevated shell
(screen-capturing its pixels still works), and every launch costs the user
a UAC click. The asInvoker variant is layout/theming-identical; it just
shows "Not elevated" in the header and loads less data on a few tabs
(Services list, Events channels).

## Real run (for the user)

```bash
dotnet build -c Release
```

Then launch `src/TaskManagerPlus/bin/Release/net8.0-windows10.0.19041.0/TaskManagerPlus.exe`
via `Start-Process` and **tell the user to expect one UAC prompt**. Verify it's up with
`Get-Process TaskManagerPlus` (allow ~20s for approval + startup); optionally confirm
visually with a screen capture of the window rect (see the screenshot script's
DwmGetWindowAttribute approach — capture works on elevated windows, input doesn't).
Leave it running for the user — you can't stop it anyway.

## Verification loop (automated, no UAC)

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/run/scripts/build-asinvoker.ps1
```

Prints `EXE: <path>` (builds to `%TEMP%\TaskManagerPlus-asinvoker`, never the repo tree).
Then sweep and screenshot — all 18 tabs by default, or `-Tabs "CPU","Stability"` for a
targeted check:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/run/scripts/screenshot-tabs.ps1 -Exe "<path from step 1>"
```

Each tab gets its own fresh launch (`--tab "<header>"`), a settle wait for polled data,
a window screenshot, and a kill. Output is one `OK`/`CRASH`/`NOWIN` line per tab plus
PNGs in `%TEMP%\TaskManagerPlus-asinvoker\shots`. Captures use `PrintWindow`
(`PW_RENDERFULLCONTENT`), so they stay clean when other windows overlap the app and the
sweep never steals the user's focus.

**Then read the screenshots with the Read tool — actually look at them.** `OK` only means
a window existed. The failures this sweep exists to catch are visual: a blank frame, a
light-gray un-themed control (the missing-`BasedOn` bug), clipped labels, an error dialog
sitting in front of the window. The default 1266x853 window surfaces most clipping. A tab
that never shows a window at all usually means a XAML `StaticResource` throw killed
MainWindow's constructor — check Application event log or run the exe by hand for the
dialog text.

### Tab addresses

`--tab` matches leaf-tab **header text** (same resolver as Ctrl+1..9 and the Ctrl+K
palette). The 18 leaves:

Summary · CPU · Memory · Storage · Network · GPU · Energy & Thermals · Processes ·
Services · Startup · System · Devices & Drivers · Windows Health · Troubleshoot ·
Responsiveness · Stability · Events · Security

Note the System group's leaf is literally "System" — "System Specs" resolves nothing
(the ViewModel is named SystemSpecs*, the header is not).

## Gotchas (each one has burned a session before)

- **Never pass `-p:BaseIntermediateOutputPath`** to the asInvoker build. The WPF temp
  project then double-includes generated `*.g.cs` from both obj roots → ~1600 CS0111
  errors. Overriding only `BaseOutputPath` and sharing `obj/` is correct and is what
  the script does.
- **Smart App Control**, if On, blocks unsigned local builds outright (instant exit,
  no window, `0xE0434352`) — signing doesn't help; SAC blocks on reputation. See
  CLAUDE.md "Local dev environment notes".
- A running **Debug build locks its exe** — build/run Release in parallel instead of
  waiting for it to exit.
- A leftover **elevated** instance from a real run can't be `Stop-Process`ed from your
  shell; ask the user to close it if it's in the way.
- Screenshots on a DPI-scaled display need `SetProcessDPIAware()` before reading window
  rects — the script does this; do the same in any ad-hoc capture code.
