# Fetches Virtual Audio Cable Lite into installer\cable\vac for the installer to bundle.
#
# VAC Lite's licence allows distributing it together with another product, unmodified and not
# for profit, so it is taken from the author's own site rather than committed here. The hash
# pins the exact package: a different file is not something anyone has checked, and the
# version it carries has to match CableVersion in installer\LoopIt7.iss. When the download
# fails the folder is left absent, and the installer builds without the cable; the app then
# points people at the download page instead. Used by build.ps1 and the GitHub workflow.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$vacUrl    = 'https://software.muzychenko.net/freeware/vac471lite.zip'
$vacSha256 = '1ED42A7E763BF6237438FB62191AE88B2922F014B37658B29D7801D72C9D5357'
$vacDir    = Join-Path $root 'installer\cable\vac'
$vacZip    = Join-Path ([IO.Path]::GetTempPath()) 'loopit7-vac471lite.zip'

try {
    Invoke-WebRequest $vacUrl -OutFile $vacZip -UseBasicParsing
    $hash = (Get-FileHash $vacZip -Algorithm SHA256).Hash
    if ($hash -ne $vacSha256) { throw "hash is $hash, expected $vacSha256" }
    if (Test-Path $vacDir) { Remove-Item $vacDir -Recurse -Force }
    Expand-Archive $vacZip $vacDir
    Write-Host "   VAC Lite 4.71 -> $vacDir" -ForegroundColor Green
} catch {
    Write-Host "   VAC Lite not bundled: $_" -ForegroundColor Yellow
    if (Test-Path $vacDir) { Remove-Item $vacDir -Recurse -Force }
}
