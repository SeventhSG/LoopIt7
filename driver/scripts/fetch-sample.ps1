# Fetches Microsoft's sysvad audio driver sample into driver\vendor and applies LoopIt7 Cable
# on top of it.
#
# The sample is MIT licensed and is not committed here. What is committed is the change that
# turns it into LoopIt7 Cable, driver\patches\loopit7-cable.patch, made against one exact
# upstream commit. That commit is pinned below: the samples repository moves, and a patch
# applied to a different sample is not a driver anyone has tested.
#
# Two sparse paths come down: audio/sysvad (the driver) and setup/devcon (the tool install.ps1
# uses to create the cable's device node), a few megabytes rather than the whole repository.

param(
    [switch]$Force,
    # Where to put the sample. Only worth changing to check the patch applies cleanly.
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$vendor = if ($Destination) { $Destination } else { Join-Path $driverRoot 'vendor' }
$patch  = Join-Path $driverRoot 'patches\loopit7-cable.patch'

$upstream = 'https://github.com/microsoft/Windows-driver-samples.git'
$commit   = '67d81f217bc01edf7a4320e4911c11065635acfa'

if (-not (Test-Path $patch)) { throw "Missing $patch." }

if (Test-Path $vendor) {
    if (-not $Force) {
        Write-Host "$vendor already exists. Pass -Force to replace it, which discards any" -ForegroundColor Yellow
        Write-Host 'driver changes not yet saved with driver\scripts\export-patch.ps1.' -ForegroundColor Yellow
        exit 0
    }
    Remove-Item $vendor -Recurse -Force
}

New-Item -ItemType Directory -Force $vendor | Out-Null
Push-Location $vendor
try {
    # Fixed line ending handling, so the patch applies the same way on every machine whatever
    # the user's global git settings are.
    git init -q .
    git config core.autocrlf true
    git remote add origin $upstream
    git sparse-checkout init --cone | Out-Null
    git sparse-checkout set audio/sysvad setup/devcon | Out-Null
    git fetch -q --depth 1 origin $commit
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $commit from $upstream." }
    git checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Checkout of the pinned sample failed.' }

    git apply --whitespace=nowarn $patch
    if ($LASTEXITCODE -ne 0) { throw "The LoopIt7 Cable patch did not apply to $commit." }
}
finally {
    Pop-Location
}

$sample = Join-Path $vendor 'audio\sysvad\TabletAudioSample\TabletAudioSample.vcxproj'
if (-not (Test-Path $sample)) { throw 'The sysvad sample did not arrive. Check the network and try again.' }

Write-Host "sysvad $($commit.Substring(0, 12)) + LoopIt7 Cable -> $vendor" -ForegroundColor Green
Write-Host 'Next: driver\scripts\build.ps1'
