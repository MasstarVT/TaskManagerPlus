<#
.SYNOPSIS
    Signs the built TaskManagerPlus.exe with the local dev code-signing certificate.

.DESCRIPTION
    Uses a self-signed certificate (subject "CN=Task Manager Plus (Dev Build)") stored
    in Cert:\CurrentUser\My. This removes the "Unknown Publisher" label and gives the
    exe a valid Authenticode signature, but it does NOT satisfy Windows Smart App
    Control - that feature blocks based on reputation (Microsoft Store distribution or
    a certificate with an established trust history), not merely "is this file signed".
    For a local dev build, the actual fix for Smart App Control blocking the app is to
    turn it off: Windows Security > App & browser control > Smart App Control settings.

    If the certificate doesn't exist yet on this machine, run New-DevCertificate.ps1
    (in this same folder) once first.

.PARAMETER Configuration
    Build configuration whose output to sign. Defaults to Release.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot "src\TaskManagerPlus\bin\$Configuration\net8.0-windows\TaskManagerPlus.exe"

if (-not (Test-Path $exePath)) {
    throw "Build output not found at '$exePath'. Build the project first (dotnet build -c $Configuration)."
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=Task Manager Plus (Dev Build)' } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    throw "No dev signing certificate found in Cert:\CurrentUser\My. Run scripts\New-DevCertificate.ps1 first."
}

Write-Host "Signing $exePath with certificate $($cert.Thumbprint) (expires $($cert.NotAfter))..."

try {
    $result = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert `
        -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256
} catch {
    Write-Warning "Timestamped signing failed ($($_.Exception.Message)); signing without a timestamp."
    $result = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert -HashAlgorithm SHA256
}

if ($result.Status -ne 'Valid') {
    throw "Signing failed: $($result.Status) - $($result.StatusMessage)"
}

Write-Host "Signed OK: $($result.StatusMessage)" -ForegroundColor Green
