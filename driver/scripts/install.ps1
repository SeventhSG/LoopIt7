# Installs the signed driver package. Run as Administrator, after a reboot with test signing on.

param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]     [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installing a driver needs an Administrator prompt. Reopen PowerShell as Administrator.'
}

$testSigning = bcdedit /enum '{current}' | Select-String -Pattern 'testsigning\s+Yes'
if (-not $testSigning) {
    Write-Host 'Test signing is off, so Windows will refuse this driver.' -ForegroundColor Yellow
    Write-Host 'Run "bcdedit /set testsigning on", reboot, then try again.' -ForegroundColor Yellow
    exit 1
}

$driverRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $driverRoot "vendor\audio\sysvad\$Platform\$Configuration"
$inf = Get-ChildItem $output -Filter '*.inf' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $inf) { throw "No INF found under $output. Build and sign first." }

Write-Host "installing $($inf.FullName)"
pnputil /add-driver $inf.FullName /install
if ($LASTEXITCODE -ne 0) { throw 'pnputil refused the package. Check the signature and the test signing state.' }

Write-Host ''
Write-Host 'Installed. The new endpoints appear in LoopIt7 on the Devices tab.' -ForegroundColor Green
Write-Host 'To remove it: pnputil /enum-drivers, then pnputil /delete-driver oem#.inf /uninstall'
