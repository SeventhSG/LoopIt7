# Saves the LoopIt7 Cable changes in driver\vendor to driver\patches\loopit7-cable.patch.
#
# driver\vendor is not committed, so an edit made there exists only on this machine until it is
# exported. Run this after changing the driver, then commit the patch. fetch-sample.ps1 applies
# it to the pinned sample.
#
# Only source is exported: build output (x64\, ARM64\) is left out, and new files have to be
# listed in $newFiles below to be included.

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $driverRoot 'vendor'
$patch  = Join-Path $driverRoot 'patches\loopit7-cable.patch'

$newFiles = @(
    'audio/sysvad/EndpointsCommon/CableRing.cpp'
    'audio/sysvad/EndpointsCommon/CableRing.h'
    'audio/sysvad/TabletAudioSample/cablespeakerwavtable.h'
)

if (-not (Test-Path (Join-Path $vendor '.git'))) { throw 'driver\vendor is not a checkout. Run fetch-sample.ps1 first.' }

Push-Location $vendor
try {
    foreach ($file in $newFiles) {
        if (-not (Test-Path $file)) { throw "Listed as new but missing: $file" }
    }
    # Intent to add: new files show up in the diff without being staged.
    git add -N -- $newFiles

    $untracked = git ls-files --others --exclude-standard -- audio/sysvad setup/devcon |
        Where-Object { $_ -notmatch '/(x64|ARM64)/' }
    if ($untracked) {
        Write-Host 'New files not listed in export-patch.ps1, so not exported:' -ForegroundColor Yellow
        $untracked | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    # --binary because the INX is UTF-16, which git treats as binary. Written through cmd so
    # the bytes land exactly as git produced them, with no PowerShell re-encoding.
    New-Item -ItemType Directory -Force (Split-Path -Parent $patch) | Out-Null
    cmd /c "git diff --binary --full-index -- audio/sysvad setup/devcon > `"$patch`""
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }
}
finally {
    Pop-Location
}

$files = (Select-String -Path $patch -Pattern '^diff --git').Count
Write-Host "$files files -> $patch" -ForegroundColor Green
