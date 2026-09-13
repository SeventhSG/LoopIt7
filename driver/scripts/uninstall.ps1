# Removes LoopIt7 Cable completely: the device, the driver package, and its service entry.
#
# Safe to run in any state, including with Secure Boot on (nothing here needs the driver to
# load). This is also the recovery step: if a test build misbehaves, turn Secure Boot back on
# so Windows refuses to load it, boot, and run this.
#
# Only LoopIt7's own identifiers are touched: the hardware id Root\LoopIt7Cable and packages
# whose original INF is loopit7cable.inf.

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Removing a driver needs an Administrator prompt. Reopen PowerShell as Administrator.'
}

$hardwareId = 'Root\LoopIt7Cable'
$infName    = 'loopit7cable.inf'
$service    = 'LoopIt7Cable'

function Get-CableDevices {
    # Includes devices that are not present, so a half-installed node is found too.
    @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $hardwareId })
}

function Get-CablePackages {
    @(Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
        Where-Object { (Split-Path -Leaf $_.OriginalFileName) -eq $infName })
}

$devices = Get-CableDevices
foreach ($device in $devices) {
    Write-Host "removing device $($device.InstanceId)"
    pnputil /remove-device "$($device.InstanceId)" | Out-Host
}

$packages = Get-CablePackages
foreach ($package in $packages) {
    Write-Host "removing package $($package.Driver) (version $($package.Version))"
    pnputil /delete-driver $package.Driver /uninstall /force | Out-Host
}

# The service entry outlives the package. Leaving it would do no harm, but a clean machine
# should look clean.
if (Get-Service -Name $service -ErrorAction SilentlyContinue) {
    Write-Host "removing service $service"
    sc.exe delete $service | Out-Host
}

$leftDevices  = Get-CableDevices
$leftPackages = Get-CablePackages

Write-Host ''
if ($leftDevices.Count -eq 0 -and $leftPackages.Count -eq 0) {
    Write-Host 'LoopIt7 Cable is not installed.' -ForegroundColor Green
} else {
    foreach ($d in $leftDevices)  { Write-Host "still present: device $($d.InstanceId)" -ForegroundColor Red }
    foreach ($p in $leftPackages) { Write-Host "still present: package $($p.Driver)" -ForegroundColor Red }
    Write-Host 'Reboot and run this again.' -ForegroundColor Red
    exit 1
}
exit 0
