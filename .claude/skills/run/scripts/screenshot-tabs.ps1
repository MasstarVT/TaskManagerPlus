<#
Launches TaskManagerPlus.exe once per requested tab (--tab "<header text>"), waits for the
window plus a settle period, screenshots the window rect, kills the instance, and prints one
OK/CRASH/NOWIN line per tab.

Meant for the asInvoker build from build-asinvoker.ps1 — an elevated instance can't be killed
(or sent input) from a non-elevated shell, so this script would leave stray elevated processes.

An "OK" line only proves a window existed: LOOK AT the saved screenshots. A blank frame, a
light-gray (un-themed) region, or a Windows error dialog in front of the window are the
failures this sweep exists to catch.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    # Leaf-tab header texts — what --tab matches. Default: all 18 leaves.
    [string[]]$Tabs = @(
        "Summary", "CPU", "Memory", "Storage", "Network", "GPU", "Energy & Thermals",
        "Processes", "Services", "Startup", "System", "Devices & Drivers", "Windows Health",
        "Troubleshoot", "Responsiveness", "Stability", "Events", "Security"),
    [string]$OutDir = (Join-Path $env:TEMP "TaskManagerPlus-asinvoker\shots"),
    # Time after the window appears before capturing — polled data needs a few ticks to land.
    [int]$SettleSeconds = 7
)
$ErrorActionPreference = 'Continue'
# When invoked via `powershell -File`, array syntax isn't evaluated — a caller's
# -Tabs "CPU","Stability" arrives as the single string "CPU,Stability". No tab header
# contains a comma, so splitting on it makes both calling shapes work.
$Tabs = @($Tabs | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'TmpRunNative').Type) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TmpRunNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int cb);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}
# Without DPI awareness the window rect and CopyFromScreen disagree on a scaled display.
[TmpRunNative]::SetProcessDPIAware() | Out-Null

foreach ($tab in $Tabs) {
    $safe = ($tab -replace '[^A-Za-z0-9]', '_')
    $p = Start-Process -FilePath $Exe -ArgumentList "--tab `"$tab`"" -PassThru
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { break }
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($p.HasExited) { "CRASH  $tab (exit $($p.ExitCode))"; continue }
    if ($hwnd -eq [IntPtr]::Zero) {
        "NOWIN  $tab"
        try { Stop-Process -Id $p.Id -Force -Confirm:$false } catch {}
        continue
    }
    Start-Sleep -Seconds $SettleSeconds
    if ($p.HasExited) { "CRASH-LATE  $tab (exit $($p.ExitCode))"; continue }

    # PrintWindow with PW_RENDERFULLCONTENT (2) captures the window's OWN rendering, so an
    # overlapping window (this terminal, a notification) can't photobomb the shot and the
    # sweep never has to steal focus. Crop the invisible resize border via the delta between
    # GetWindowRect and DWMWA_EXTENDED_FRAME_BOUNDS (9).
    $wr = New-Object TmpRunNative+RECT
    $dr = New-Object TmpRunNative+RECT
    [TmpRunNative]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    [TmpRunNative]::DwmGetWindowAttribute($hwnd, 9, [ref]$dr, [System.Runtime.InteropServices.Marshal]::SizeOf($dr)) | Out-Null
    $fullW = $wr.Right - $wr.Left; $fullH = $wr.Bottom - $wr.Top
    $w = $dr.Right - $dr.Left; $h = $dr.Bottom - $dr.Top
    if ($fullW -gt 0 -and $fullH -gt 0 -and $w -gt 0 -and $h -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap($fullW, $fullH)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        $printed = [TmpRunNative]::PrintWindow($hwnd, $hdc, 2)
        $g.ReleaseHdc($hdc)
        if (-not $printed) {
            # Rare fallback: front the window and copy from screen instead.
            [TmpRunNative]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 400
            $g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, $bmp.Size)
        }
        $g.Dispose()
        $cropRect = New-Object System.Drawing.Rectangle(($dr.Left - $wr.Left), ($dr.Top - $wr.Top), $w, $h)
        $cropped = $bmp.Clone($cropRect, $bmp.PixelFormat)
        $bmp.Dispose()
        $cropped.Save((Join-Path $OutDir "$safe.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $cropped.Dispose()
        "OK     $tab (${w}x${h}) -> $safe.png"
    } else {
        "NORECT $tab"
    }
    try { Stop-Process -Id $p.Id -Force -Confirm:$false } catch {}
    Start-Sleep -Milliseconds 500
}
"Shots: $OutDir"
