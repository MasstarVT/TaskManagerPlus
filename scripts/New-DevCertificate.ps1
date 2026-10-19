<#
.SYNOPSIS
    One-time setup: creates and trusts the local self-signed code-signing
    certificate used by Sign-Release.ps1.

.DESCRIPTION
    Creates a self-signed certificate (CN=Task Manager Plus (Dev Build)) in
    Cert:\CurrentUser\My, then adds it to CurrentUser\Root so Windows treats
    binaries signed with it as validly signed on THIS user account, on THIS
    machine.

    This only affects the current Windows user profile - no admin rights
    needed, nothing machine-wide changes. It removes "Unknown Publisher"
    warnings for this app's builds, but it does NOT satisfy Smart App
    Control (which checks reputation, not just presence of a valid
    signature). If Smart App Control is blocking local dev builds, the
    actual fix is turning it off in Windows Security - see README.md.

    The private key is created non-exportable and the certificate is valid
    for one year. Any program running as you can still sign with the key, so
    the certificate is trusted only as far as it has to be: Root (a valid
    chain, which is what removes "Unknown Publisher") and NOT TrustedPublisher,
    which would also make PowerShell AllSigned and Office treat anything signed
    with it as coming from a trusted publisher.

    Safe to re-run: a non-expired certificate with this subject and a
    non-exportable key is reused. Certificates from earlier versions of this
    script (exportable key, or added to TrustedPublisher) are retired - removed
    from My, Root and TrustedPublisher - and replaced. Windows asks you to
    confirm each removal from Root.
#>

$ErrorActionPreference = 'Stop'

$subject = 'CN=Task Manager Plus (Dev Build)'

function Test-KeyExportable($certificate) {
    try {
        $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($key -is [System.Security.Cryptography.RSACng]) {
            $exportFlags = [System.Security.Cryptography.CngExportPolicies]::AllowExport -bor
                [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
            return ($key.Key.ExportPolicy -band $exportFlags) -ne 0
        }
        if ($key -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            return $key.CspKeyContainerInfo.Exportable
        }
        return $true # unknown key type - replace it rather than trust it
    } catch {
        return $true
    }
}

function Remove-FromStore($storeName, $thumbprint) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
    $store.Open('ReadWrite')
    try {
        foreach ($match in @($store.Certificates | Where-Object Thumbprint -eq $thumbprint)) {
            $store.Remove($match)
            Write-Host "Removed $thumbprint from CurrentUser\$storeName."
        }
    } finally {
        $store.Close()
    }
}

$candidates = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject })
$cert = $candidates |
    Where-Object { $_.NotAfter -gt (Get-Date) -and -not (Test-KeyExportable $_) } |
    Sort-Object NotBefore -Descending |
    Select-Object -First 1

foreach ($old in $candidates) {
    if ($cert -and $old.Thumbprint -eq $cert.Thumbprint) { continue }
    Write-Host "Retiring certificate $($old.Thumbprint) (expires $($old.NotAfter)): expired or its key is exportable."
    foreach ($storeName in 'TrustedPublisher', 'Root', 'My') { Remove-FromStore $storeName $old.Thumbprint }
}

if ($cert) {
    Write-Host "Using existing certificate $($cert.Thumbprint) (expires $($cert.NotAfter))."
} else {
    Write-Host "Creating new self-signed code-signing certificate..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(1) `
        -FriendlyName 'TaskManagerPlus Dev Signing'
    Write-Host "Created $($cert.Thumbprint) (expires $($cert.NotAfter))."
}

# Earlier versions of this script also trusted the certificate as a publisher.
Remove-FromStore 'TrustedPublisher' $cert.Thumbprint

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
$store.Open('ReadWrite')
if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    $store.Add($cert)
    Write-Host "Added to CurrentUser\Root."
} else {
    Write-Host "Already present in CurrentUser\Root."
}
$store.Close()

Write-Host "Done. Run Sign-Release.ps1 after each build to sign the exe." -ForegroundColor Green
