# Renames Microsoft's sample endpoints to LoopIt7 Cable.
#
# Every replacement is checked. If the sample changes shape upstream and an expected string is
# missing, this stops and says which one, rather than half rewriting the package and leaving
# you to work out why the driver installs under the wrong name.

$ErrorActionPreference = 'Stop'
$driverRoot = Split-Path -Parent $PSScriptRoot
$sample = Join-Path $driverRoot 'vendor\audio\sysvad'

if (-not (Test-Path $sample)) { throw 'Run driver\scripts\fetch-sample.ps1 first.' }

# The INX files are INF templates; the build turns them into INFs.
$targets = Get-ChildItem "$sample\TabletAudioSample" -Filter '*.inx' -ErrorAction SilentlyContinue
if (-not $targets) { throw "No .inx files under $sample\TabletAudioSample. The sample layout changed." }

$replacements = [ordered]@{
    'Microsoft Virtual Audio Device (Simple)' = 'LoopIt7 Cable'
    'Microsoft Virtual Audio Device'          = 'LoopIt7 Cable'
    'Virtual Audio Device (Simple)'           = 'LoopIt7 Cable'
    'Speaker (Virtual Audio)'                 = 'LoopIt7 Cable Out'
    'Microphone (Virtual Audio)'              = 'LoopIt7 Cable In'
}

$changedFiles = 0
$missed = New-Object System.Collections.Generic.List[string]

foreach ($file in $targets) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $original = $text

    foreach ($pair in $replacements.GetEnumerator()) {
        if ($text.Contains($pair.Key)) {
            $text = $text.Replace($pair.Key, $pair.Value)
        }
    }

    if ($text -ne $original) {
        [System.IO.File]::WriteAllText($file.FullName, $text)
        $changedFiles++
        Write-Host "branded $($file.Name)" -ForegroundColor Green
    }
}

foreach ($pair in $replacements.GetEnumerator()) {
    $found = $targets | Where-Object {
        [System.IO.File]::ReadAllText($_.FullName).Contains($pair.Value)
    }
    if (-not $found) { $missed.Add($pair.Key) }
}

if ($changedFiles -eq 0) {
    throw 'Nothing was renamed. Either this has already run, or the sample no longer uses those strings.'
}

if ($missed.Count -gt 0) {
    Write-Host ''
    Write-Host 'These strings were not found and may need renaming by hand:' -ForegroundColor Yellow
    foreach ($item in $missed) { Write-Host "  $item" -ForegroundColor Yellow }
}

Write-Host ''
Write-Host "Branded $changedFiles file(s)." -ForegroundColor Green
Write-Host 'Next: driver\scripts\build.ps1'
