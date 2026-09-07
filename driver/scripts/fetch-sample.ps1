# Sparse clones Microsoft's sysvad audio driver sample into driver\vendor.
#
# The sample is MIT licensed and is not committed here, so this script is how you get it.
# It pulls only audio/sysvad, which is a few megabytes rather than the whole samples repo.

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $driverRoot 'vendor'

if (Test-Path $vendor) {
    if (-not $Force) {
        Write-Host "vendor\ already exists. Pass -Force to replace it." -ForegroundColor Yellow
        exit 0
    }

    Remove-Item $vendor -Recurse -Force
}

New-Item -ItemType Directory -Force $vendor | Out-Null
Push-Location $vendor
try {
    git init -q .
    git remote add origin https://github.com/microsoft/Windows-driver-samples.git
    git config core.sparseCheckout true
    git sparse-checkout init --cone | Out-Null
    git sparse-checkout set audio/sysvad | Out-Null
    git fetch --depth 1 origin main
    git checkout FETCH_HEAD --quiet
}
finally {
    Pop-Location
}

$sample = Join-Path $vendor 'audio\sysvad'
if (-not (Test-Path $sample)) { throw 'The sysvad sample did not arrive. Check the network and try again.' }

Write-Host "sysvad -> $sample" -ForegroundColor Green
Write-Host 'Next: driver\scripts\brand.ps1'
