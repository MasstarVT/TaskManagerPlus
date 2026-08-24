<#
.SYNOPSIS
    One-time setup: creates and trusts the local self-signed code-signing
    certificate used by Sign-Release.ps1.

.DESCRIPTION
    Creates a self-signed certificate (CN=Task Manager Plus (Dev Build)) in
    Cert:\CurrentUser\My, then adds it to CurrentUser\Root and
    CurrentUser\TrustedPublisher so Windows treats binaries signed with it as
    trusted on THIS user account, on THIS machine.

    This only affects the current Windows user profile - no admin rights
    needed, nothing machine-wide changes. It removes "Unknown Publisher"
    warnings for this app's builds, but it does NOT satisfy Smart App
    Control (which checks reputation, not just presence of a valid
    signature). If Smart App Control is blocking local dev builds, the
    actual fix is turning it off in Windows Security - see README.md.

    Safe to re-run: if a non-expired certificate with this subject already
    exists, it's reused instead of creating a duplicate.
#>

$ErrorActionPreference = 'Stop'

$subject = 'CN=Task Manager Plus (Dev Build)'

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existing) {
    Write-Host "Using existing certificate $($existing.Thumbprint) (expires $($existing.NotAfter))."
    $cert = $existing
} else {
    Write-Host "Creating new self-signed code-signing certificate..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'TaskManagerPlus Dev Signing'
    Write-Host "Created $($cert.Thumbprint) (expires $($cert.NotAfter))."
}

foreach ($storeName in 'Root', 'TrustedPublisher') {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
    $store.Open('ReadWrite')
    if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        $store.Add($cert)
        Write-Host "Added to CurrentUser\$storeName."
    } else {
        Write-Host "Already present in CurrentUser\$storeName."
    }
    $store.Close()
}

Write-Host "Done. Run Sign-Release.ps1 after each build to sign the exe." -ForegroundColor Green
