# Authenticode signing, if there is a certificate to sign with.
#
#   powershell -File build\sign.ps1 -Path dist\LoopIt7-Setup.exe
#
# The certificate comes from the environment, never from the repository:
#
#   LOOPIT7_CERT_PFX        path to a .pfx file
#   LOOPIT7_CERT_BASE64     or the same .pfx, base64 encoded (this is what CI uses)
#   LOOPIT7_CERT_PASSWORD   its password
#   LOOPIT7_CERT_SHA1       or the thumbprint of a certificate already in the user's store,
#                           which is how a hardware token or a cloud HSM is used
#
# With none of those set this exits quietly and the file ships unsigned, which is the
# current state of things and better than a self signed certificate: Windows trusts a
# self signed publisher no further than an unsigned one, and shows a worse dialog for it.

param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [switch]$Required
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    $found = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }

    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $tool = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    return $null
}

$hasCert = $env:LOOPIT7_CERT_PFX -or $env:LOOPIT7_CERT_BASE64 -or $env:LOOPIT7_CERT_SHA1
if (-not $hasCert) {
    if ($Required) { throw 'No signing certificate is configured. Set LOOPIT7_CERT_PFX, LOOPIT7_CERT_BASE64 or LOOPIT7_CERT_SHA1.' }
    Write-Host '   not signed: no certificate configured' -ForegroundColor DarkGray
    return
}

$signtool = Find-SignTool
if (-not $signtool) {
    if ($Required) { throw 'signtool.exe was not found. Install the Windows SDK signing tools.' }
    Write-Host '   not signed: signtool.exe was not found' -ForegroundColor DarkYellow
    return
}

$pfx = $env:LOOPIT7_CERT_PFX
$temporary = $null

if (-not $pfx -and $env:LOOPIT7_CERT_BASE64) {
    $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("loopit7-" + [guid]::NewGuid().ToString('N') + ".pfx")
    [System.IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String($env:LOOPIT7_CERT_BASE64))
    $pfx = $temporary
}

# RFC 3161, so the signature stays valid after the certificate expires.
$common = @('/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', '/v')

try {
    foreach ($file in $Path) {
        if (-not (Test-Path $file)) { throw "Nothing to sign at $file" }

        $args = @('sign') + $common
        if ($pfx) {
            $args += @('/f', $pfx)
            if ($env:LOOPIT7_CERT_PASSWORD) { $args += @('/p', $env:LOOPIT7_CERT_PASSWORD) }
        } else {
            $args += @('/sha1', $env:LOOPIT7_CERT_SHA1)
        }
        $args += $file

        & $signtool @args | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool failed on $file with code $LASTEXITCODE" }

        $signature = Get-AuthenticodeSignature $file
        Write-Host ("   signed {0}  [{1}]  {2}" -f (Split-Path $file -Leaf), $signature.Status, $signature.SignerCertificate.Subject) -ForegroundColor Green
    }
}
finally {
    if ($temporary -and (Test-Path $temporary)) { Remove-Item $temporary -Force }
}
