<#
Builds a throwaway asInvoker variant of Task Manager Plus for no-UAC automated UI verification.

Why: the real app.manifest is requireAdministrator, which pops one UAC prompt per launch and
makes the process impossible to kill or automate from a non-elevated shell. The asInvoker
variant is layout/theming-identical — some tabs just load less data non-elevated (Services
list, Events channels) and the header shows "Not elevated".

Output: prints "EXE: <path>" on success; pass that path to screenshot-tabs.ps1.
#>
param(
    # Scratch root for the manifest, bin output, and screenshots. Never the repo tree.
    [string]$WorkDir = (Join-Path $env:TEMP "TaskManagerPlus-asinvoker")
)
$ErrorActionPreference = 'Stop'

# scripts -> run -> skills -> .claude -> repo root
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$project = Join-Path $repoRoot "src\TaskManagerPlus"

New-Item -ItemType Directory -Force $WorkDir | Out-Null

# Derive the asInvoker manifest from the real one each time, so supportedOS/DPI settings
# never go stale against app.manifest.
$manifest = Join-Path $WorkDir "app.asinvoker.manifest"
(Get-Content (Join-Path $project "app.manifest") -Raw) `
    -replace 'level="requireAdministrator"', 'level="asInvoker"' |
    Set-Content -Path $manifest -Encoding utf8

# Only BaseOutputPath is overridden. Do NOT also override BaseIntermediateOutputPath:
# the WPF temp project then double-includes generated *.g.cs files from both obj roots
# and the build fails with ~1600 CS0111 duplicate-member errors. Sharing obj/ is fine.
dotnet build $project -c Release --nologo -v m `
    -p:ApplicationManifest="$manifest" `
    -p:BaseOutputPath="$WorkDir\bin\"
if ($LASTEXITCODE -ne 0) { throw "asInvoker build failed (exit $LASTEXITCODE)" }

$exe = Get-ChildItem -Path (Join-Path $WorkDir "bin\Release") -Recurse -Filter TaskManagerPlus.exe |
    Select-Object -First 1
if ($null -eq $exe) { throw "build succeeded but TaskManagerPlus.exe not found under $WorkDir\bin\Release" }
"EXE: $($exe.FullName)"
