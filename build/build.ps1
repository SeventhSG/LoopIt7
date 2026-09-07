# Builds LoopIt7 end to end: artwork, publish, installer.
#
#   powershell -ExecutionPolicy Bypass -File build\build.ps1
#   powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipInstaller
#
# Output lands in publish\ (the app) and dist\ (the setup executable).

param(
    [string]$Configuration = 'Release',
    [switch]$SkipAssets,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\LoopIt7\LoopIt7.csproj'
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

Write-Host '== publish ==' -ForegroundColor DarkYellow
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The runtime config and deps files are needed; nothing else in here is.
Get-ChildItem $publishDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

$size = (Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host ("   published {0:N1} MB to {1}" -f ($size / 1MB), $publishDir)

if ($SkipInstaller) { return }

Write-Host '== installer ==' -ForegroundColor DarkYellow
$iscc = Find-Tool @(
    "$env:LOCALAPPDATA\Programs\InnoSetup7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) 'iscc'
if (-not $iscc) { throw 'Inno Setup was not found. Install it from https://jrsoftware.org/isdl.php' }

New-Item -ItemType Directory -Force $distDir | Out-Null
& $iscc /Qp (Join-Path $root 'installer\LoopIt7.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer failed to compile.' }

Get-ChildItem $distDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("   {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}
