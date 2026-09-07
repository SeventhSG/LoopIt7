# Builds the driver package with MSBuild and the WDK.

param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]     [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $driverRoot 'vendor\audio\sysvad\sysvad.sln'

if (-not (Test-Path $solution)) { throw 'Run driver\scripts\fetch-sample.ps1 first.' }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw @'
Visual Studio was not found.

Install Visual Studio 2022 Build Tools with the "Desktop development with C++" workload,
then the Windows Driver Kit:
  https://visualstudio.microsoft.com/downloads/
  https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk
'@
}

$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found. Add the "Desktop development with C++" workload.' }

$wdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\build" -Filter 'WindowsDriver.Common.targets' -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $wdk) {
    throw 'The Windows Driver Kit was not found. Install it from https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk'
}

Write-Host "msbuild  $msbuild"
Write-Host "wdk      $($wdk.FullName)"
Write-Host ''

& $msbuild $solution /p:Configuration=$Configuration /p:Platform=$Platform /m /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'The driver build failed.' }

$output = Join-Path $driverRoot "vendor\audio\sysvad\$Platform\$Configuration"
Write-Host ''
Write-Host "output -> $output" -ForegroundColor Green
Write-Host 'Next: driver\scripts\sign-test.ps1'
