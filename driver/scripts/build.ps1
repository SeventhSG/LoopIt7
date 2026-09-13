# Builds the LoopIt7 Cable driver package: compile, stage, catalog.
#
# Always a full clean rebuild of both projects. TabletAudioSample links EndpointsCommon.lib by
# path rather than through a project reference, so building the driver project alone never
# rebuilds the library, and a stale library once shipped a fix that was not in the binary.
# The freshness check at the end is there so that can never happen quietly again.

param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]     [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$sysvad     = Join-Path $driverRoot 'vendor\audio\sysvad'
$common     = Join-Path $sysvad 'EndpointsCommon\EndpointsCommon.vcxproj'
$project    = Join-Path $sysvad 'TabletAudioSample\TabletAudioSample.vcxproj'
$devconProj = Join-Path $driverRoot 'vendor\setup\devcon\devcon.vcxproj'

if (-not (Test-Path $project)) { throw 'Run driver\scripts\fetch-sample.ps1 first.' }

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

# vswhere's default product set skips Build Tools-only installs, which is exactly what the
# README tells people to install, so it has to be asked for explicitly.
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found. Add the "Desktop development with C++" workload.' }

$wdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\build" -Filter 'WindowsDriver.Common.targets' -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $wdk) {
    throw 'The Windows Driver Kit was not found. Install it from https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk'
}

Write-Host "msbuild  $msbuild"
Write-Host "wdk      $($wdk.FullName)"
Write-Host ''

$commonOut = Join-Path $sysvad "EndpointsCommon\$Platform\$Configuration"
$driverOut = Join-Path $sysvad "TabletAudioSample\$Platform\$Configuration"
foreach ($dir in @($commonOut, $driverOut)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

# File times on NTFS are written by the build, so compare against a moment just before it.
$buildStart = (Get-Date).AddSeconds(-2)

function Invoke-Build([string]$Path) {
    Write-Host "building $(Split-Path -Leaf $Path)" -ForegroundColor Cyan
    & $msbuild $Path /t:Rebuild /p:Configuration=$Configuration /p:Platform=$Platform /m /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "The build failed: $(Split-Path -Leaf $Path)" }
}

# Only the cable's own projects. The sample's sibling projects (APO effects, keyword
# detection) need ATL and are no use to a cable.
Invoke-Build $common
Invoke-Build $project

$lib = Join-Path $commonOut 'EndpointsCommon.lib'
$sys = Join-Path $driverOut 'LoopIt7Cable.sys'
$inf = Join-Path $driverOut 'ComponentizedAudioSample.inf'

foreach ($artifact in @($lib, $sys, $inf, (Join-Path $commonOut 'CableRing.obj'))) {
    if (-not (Test-Path $artifact)) { throw "Expected build output is missing: $artifact" }
    if ((Get-Item $artifact).LastWriteTime -lt $buildStart) {
        throw "Stale build output, not rebuilt by this run: $artifact"
    }
}

# devcon is what creates the cable's root-enumerated device node; pnputil on current Windows
# builds can stage a driver package but not create the device it is for.
if (Test-Path $devconProj) {
    Invoke-Build $devconProj
}

# Stage exactly what the cable is. TabletAudioSample.vcxproj also stamps two unrelated INFs
# (an APO package and an extension) into the same folder, and Inf2Cat fails on those because
# they reference binaries this build deliberately does not produce.
$output = Join-Path $driverOut 'Package'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item $sys $output
Copy-Item $inf (Join-Path $output 'LoopIt7Cable.inf')

# InfVerif as its own step. The WDK's MSBuild task for it looks for a 32-bit DLL the kit does
# not ship, which is why TabletAudioSample.vcxproj turns that task off; the standalone tool
# works. /w is the Windows Driver rule set, /h the stricter one used for Microsoft signing.
$infverif = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Tools" -Filter 'infverif.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $infverif) { throw 'infverif.exe was not found. Install the Windows Driver Kit.' }

$stagedInf = Join-Path $output 'LoopIt7Cable.inf'
foreach ($mode in @('/w', '/h')) {
    $report = & $infverif.FullName $mode $stagedInf 2>&1
    $report | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0 -or ($report -match '(WARNING|ERROR)\(\d+\)')) {
        throw "InfVerif $mode reported problems with $stagedInf."
    }
}

$inf2cat = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter 'Inf2Cat.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x86\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $inf2cat) { throw 'Inf2Cat.exe was not found. Install the Windows SDK.' }

# The INF targets Windows 11 22H2 (build 22621) and later, so the catalog covers those.
& $inf2cat.FullName /driver:$output /os:10_NI_X64,10_GE_X64
if ($LASTEXITCODE -ne 0) { throw 'Inf2Cat failed to produce a catalog.' }

Write-Host ''
Get-ChildItem $output | ForEach-Object { Write-Host ("  {0,-20} {1:yyyy-MM-dd HH:mm:ss}" -f $_.Name, $_.LastWriteTime) }
Write-Host ''
Write-Host "output -> $output" -ForegroundColor Green
Write-Host 'Next: driver\scripts\sign-test.ps1'
