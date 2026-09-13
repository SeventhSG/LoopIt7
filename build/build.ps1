# Builds LoopIt7 end to end: artwork, publish, installer.
#
#   powershell -ExecutionPolicy Bypass -File build\build.ps1
#   powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipInstaller
#
# Output lands in publish\ (the app) and dist\ (the setup executable).

param(
    [string]$Configuration = 'Release',
    [switch]$SkipAssets,
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LoopIt7\LoopIt7.csproj'
$tests = Join-Path $root 'tests\LoopIt7.Tests\LoopIt7.Tests.csproj'
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

function Find-Tool([string[]]$candidates, [string]$name) {
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $found = Get-Command $name -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    return $null
}

$dotnet = Find-Tool @(
    "$env:USERPROFILE\.dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe"
) 'dotnet'
if (-not $dotnet) { throw 'The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download' }

if (-not $SkipAssets) {
    Write-Host '== artwork ==' -ForegroundColor DarkYellow
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-assets.ps1')
}

# The routing safety rules run before anything is packaged. They open no device and make no
# sound, so there is no reason for a release to skip them.
if (-not $SkipTests) {
    Write-Host '== tests ==' -ForegroundColor DarkYellow
    & $dotnet run --project $tests -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed. Nothing was published.' }
}

Write-Host '== publish ==' -ForegroundColor DarkYellow
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The runtime config and deps files are needed; nothing else in here is.
Get-ChildItem $publishDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

$size = (Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host ("   published {0:N1} MB to {1}" -f ($size / 1MB), $publishDir)

# Sign the app before it goes into the installer, so the file the user ends up running
# carries the signature too. Quietly does nothing when no certificate is configured.
Write-Host '== signing ==' -ForegroundColor DarkYellow
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sign.ps1') `
    -Path (Join-Path $publishDir 'LoopIt7.exe')

if ($SkipInstaller) { return }

Write-Host '== installer ==' -ForegroundColor DarkYellow
$iscc = Find-Tool @(
    "$env:LOCALAPPDATA\Programs\InnoSetup7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) 'iscc'
if (-not $iscc) { throw 'Inno Setup was not found. Install it from https://jrsoftware.org/isdl.php' }

# Virtual Audio Cable Lite goes into the installer unmodified, as its licence requires for
# redistribution, so it is fetched from the author's own site rather than committed here. The
# hash pins the exact package: a different file is not something anyone has checked, and the
# version it carries has to match CableVersion in LoopIt7.iss. Offline, the installer is built
# without it and the app points people at the download page instead.
Write-Host '== cable ==' -ForegroundColor DarkYellow
$vacUrl    = 'https://software.muzychenko.net/freeware/vac471lite.zip'
$vacSha256 = '1ED42A7E763BF6237438FB62191AE88B2922F014B37658B29D7801D72C9D5357'
$vacDir    = Join-Path $root 'installer\cable\vac'
$vacZip    = Join-Path $env:TEMP 'loopit7-vac471lite.zip'
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

New-Item -ItemType Directory -Force $distDir | Out-Null
& $iscc /Qp (Join-Path $root 'installer\LoopIt7.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer failed to compile.' }

$setup = Get-ChildItem $distDir -Filter '*.exe'
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sign.ps1') `
    -Path ($setup | ForEach-Object { $_.FullName })

$setup | ForEach-Object {
    Write-Host ("   {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}
