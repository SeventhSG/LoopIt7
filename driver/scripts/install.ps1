# Installs the signed LoopIt7 Cable package and proves the right build is the one running.
#
# Run as Administrator, after driver\scripts\build.ps1 and sign-test.ps1, with Secure Boot off
# and test signing on. Any copy already installed is removed first, because rebuilding only
# changes the files in the build folder, never the copy Windows has already bound to the
# device; that mistake once reloaded an old, crashing build after the fix had been made.
#
# If anything goes wrong: turn Secure Boot back on (Windows then refuses the test-signed
# driver), boot, and run driver\scripts\uninstall.ps1.

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

$hardwareId = 'Root\LoopIt7Cable'
$driverRoot = Split-Path -Parent $PSScriptRoot
$package    = Join-Path $driverRoot "vendor\audio\sysvad\TabletAudioSample\$Platform\$Configuration\Package"
$inf        = Join-Path $package 'LoopIt7Cable.inf'
$sys        = Join-Path $package 'LoopIt7Cable.sys'
$cat        = Join-Path $package 'loopit7cable.cat'
$devcon     = Join-Path $driverRoot "vendor\setup\devcon\$Platform\$Configuration\devcon.exe"

# --- Can this machine load a test-signed driver at all? ------------------------------------

$secureBoot = $false
try { $secureBoot = Confirm-SecureBootUEFI } catch { }
if ($secureBoot) {
    Write-Host 'Secure Boot is on, so Windows ignores test signing and will not load this driver.' -ForegroundColor Yellow
    Write-Host 'Turn Secure Boot off in the firmware setup, boot, and try again.' -ForegroundColor Yellow
    exit 1
}

# What this boot actually runs with, not what the boot configuration says for the next one:
# software such as anti-cheat can remove the stored testsigning flag before it takes effect.
# The one-boot "Disable driver signature enforcement" startup option works too, and is the
# safer way to test: a normal restart enforces signatures again, so a bad build cannot load.
$startOptions = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions
if ($startOptions -notmatch 'TESTSIGNING|DISABLE_INTEGRITY_CHECKS') {
    Write-Host 'This boot enforces driver signatures, so Windows will refuse this driver. Either:' -ForegroundColor Yellow
    Write-Host '  Shift+Restart > Troubleshoot > Advanced options > Startup Settings > Restart, then 7' -ForegroundColor Yellow
    Write-Host '  or "bcdedit /set testsigning on" and reboot.' -ForegroundColor Yellow
    exit 1
}
Write-Host "boot options      $($startOptions.Trim())"

# --- Is the package complete and signed? ---------------------------------------------------

foreach ($file in @($inf, $sys, $cat)) {
    if (-not (Test-Path $file)) { throw "Missing $file. Run build.ps1 and sign-test.ps1 first." }
}
if (-not (Test-Path $devcon)) { throw "Missing $devcon. build.ps1 builds it from the pinned sample." }

$signature = Get-AuthenticodeSignature $cat
if (-not $signature.SignerCertificate) { throw "The catalog is not signed. Run sign-test.ps1 first." }
Write-Host "catalog signed by  $($signature.SignerCertificate.Subject)  ($($signature.Status))"

$driverVer = (Select-String -Path $inf -Pattern '^\s*DriverVer\s*=\s*[^,]+,\s*([\d.]+)').Matches[0].Groups[1].Value
$sysHash   = (Get-FileHash $sys -Algorithm SHA256).Hash
Write-Host "package version    $driverVer"
Write-Host "driver SHA-256     $sysHash"
Write-Host ''

# --- Remove what is there, remember the defaults, install ----------------------------------

& (Join-Path $PSScriptRoot 'uninstall.ps1')
if ($LASTEXITCODE -ne 0) { throw 'The previous copy could not be removed completely. Reboot and try again.' }
Write-Host ''

. (Join-Path $PSScriptRoot 'DefaultEndpoints.ps1')
$defaults = @(Get-DefaultEndpoints)

# devcon creates the root-enumerated device node and binds the package to it. pnputil can
# stage a package but cannot create the device a software-only driver needs.
Write-Host "installing $inf"
& $devcon install $inf $hardwareId | Out-Host
switch ($LASTEXITCODE) {
    0       { }
    1       { Write-Host 'Windows asks for a reboot to finish.' -ForegroundColor Yellow }
    default { throw "devcon could not install the package (exit code $LASTEXITCODE)." }
}

# --- Prove it: the device started, on this build's binary, with both endpoints -------------

$device = $null
for ($i = 0; $i -lt 40; $i++) {
    $device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.HardwareID -contains $hardwareId } | Select-Object -First 1
    if ($device -and $device.Status -eq 'OK') { break }
    Start-Sleep -Milliseconds 500
}

$failed = $false
function Fail([string]$Message) { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failed = $true }
function Pass([string]$Message) { Write-Host "  ok    $Message" -ForegroundColor Green }

Write-Host ''
Write-Host 'checks'

if (-not $device) {
    Fail 'no LoopIt7 Cable device exists'
} elseif ($device.Status -ne 'OK') {
    $code = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode').Data
    Fail "device $($device.InstanceId) is $($device.Status), problem code $code"
} else {
    Pass "device $($device.InstanceId) started"

    $boundVer = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverVersion').Data
    if ($boundVer -eq $driverVer) { Pass "bound driver version $boundVer" }
    else { Fail "bound driver version is $boundVer, the package is $driverVer" }
}

$imagePath = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\LoopIt7Cable" -ErrorAction SilentlyContinue).ImagePath
if ($imagePath) {
    $loaded = $imagePath -replace '^\\SystemRoot', $env:SystemRoot -replace '^\\\?\?\\', ''
    if ((Test-Path $loaded) -and (Get-FileHash $loaded -Algorithm SHA256).Hash -eq $sysHash) {
        Pass "service binary matches this build ($loaded)"
    } else {
        Fail "service binary $loaded is not this build"
    }
} else {
    Fail 'no LoopIt7Cable service is registered'
}

$endpoints = @()
for ($i = 0; $i -lt 40; $i++) {
    $endpoints = @(Get-PnpDevice -Class AudioEndpoint -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -like '*LoopIt7 Cable*' })
    if ($endpoints.Count -ge 2) { break }
    Start-Sleep -Milliseconds 500
}
foreach ($name in 'Cable In', 'Cable Out') {
    $endpoint = $endpoints | Where-Object { $_.FriendlyName -like "$name*" } | Select-Object -First 1
    if ($endpoint) { Pass "endpoint `"$($endpoint.FriendlyName)`" is $($endpoint.Status)" }
    else { Fail "no `"$name`" endpoint appeared" }
}

Restore-DefaultEndpoints $defaults

Write-Host ''
if ($failed) {
    Write-Host 'Installed, but not everything checked out. Details above.' -ForegroundColor Red
    Write-Host 'To take it out again: driver\scripts\uninstall.ps1' -ForegroundColor Red
    exit 1
}
Write-Host 'LoopIt7 Cable is installed and running this build.' -ForegroundColor Green
Write-Host 'To take it out again: driver\scripts\uninstall.ps1'
