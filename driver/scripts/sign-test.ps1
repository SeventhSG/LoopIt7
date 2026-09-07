# Creates a self signed test certificate and signs the built driver package with it.
#
# This is for the machine you develop on and nowhere else. Windows will only load the result
# once test signing is on, which needs a reboot and leaves a watermark on the desktop. Shipping
# to other people needs Microsoft attestation signing; see driver\README.md.

param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]     [string]$Platform = 'x64',
    [string]$Subject = 'CN=LoopIt7 Test Signing'
)

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $driverRoot "vendor\audio\sysvad\$Platform\$Configuration"

if (-not (Test-Path $output)) { throw "Nothing built at $output. Run driver\scripts\build.ps1 first." }

$package = Get-ChildItem $output -Directory | Where-Object { Test-Path (Join-Path $_.FullName '*.inf') } | Select-Object -First 1
if (-not $package) { $package = Get-Item $output }

$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
if (-not $certificate) {
    Write-Host "Creating a test certificate: $Subject"
    $certificate = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears(5)
}

Write-Host "certificate  $($certificate.Thumbprint)"

# Windows only trusts a test certificate that is in both of these stores.
foreach ($store in @('Root', 'TrustedPublisher')) {
    $path = "Cert:\LocalMachine\$store"
    $present = Get-ChildItem $path -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }
    if (-not $present) {
        Write-Host "Import the certificate into LocalMachine\$store as Administrator:" -ForegroundColor Yellow
        Write-Host "  Export-Certificate -Cert Cert:\CurrentUser\My\$($certificate.Thumbprint) -FilePath `$env:TEMP\loopit7-test.cer" -ForegroundColor Yellow
        Write-Host "  Import-Certificate -FilePath `$env:TEMP\loopit7-test.cer -CertStoreLocation Cert:\LocalMachine\$store" -ForegroundColor Yellow
    }
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK.' }

$catalogs = Get-ChildItem $package.FullName -Filter '*.cat' -Recurse
if (-not $catalogs) { throw "No catalog file under $($package.FullName). The build did not produce a driver package." }

foreach ($catalog in $catalogs) {
    & $signtool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint /t http://timestamp.digicert.com $catalog.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($catalog.Name)." }
    Write-Host "signed $($catalog.Name)" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Now, as Administrator, and then reboot:' -ForegroundColor Yellow
Write-Host '  bcdedit /set testsigning on' -ForegroundColor Yellow
Write-Host 'After the reboot: driver\scripts\install.ps1'
