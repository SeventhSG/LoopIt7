# Draws the LoopIt7 mark once and emits every raster the project needs:
# the multi size application icon and the README banner.
#
#   powershell -ExecutionPolicy Bypass -File build\make-assets.ps1

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $root 'src\LoopIt7\Assets\LoopIt7.ico'
$bannerPath = Join-Path $root 'assets\banner.png'
$markPath = Join-Path $root 'assets\mark.png'

New-Item -ItemType Directory -Force (Split-Path $iconPath) | Out-Null
New-Item -ItemType Directory -Force (Split-Path $bannerPath) | Out-Null

$Accent = [System.Drawing.Color]::FromArgb(0xE8, 0xA3, 0x3D)
$Ink = [System.Drawing.Color]::FromArgb(0x10, 0x0F, 0x0D)
$Surface = [System.Drawing.Color]::FromArgb(0x1A, 0x17, 0x14)
$Text = [System.Drawing.Color]::FromArgb(0xED, 0xE9, 0xE3)
$TextDim = [System.Drawing.Color]::FromArgb(0x9A, 0x91, 0x88)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# One source node fanning out to three destinations, drawn inside a square box.
function Draw-Mark($g, [single]$x, [single]$y, [single]$size, $color) {
    $u = { param([single]$v) $x + $v * $size }
    $v = { param([single]$t) $y + $t * $size }

    $pen = New-Object System.Drawing.Pen($color, ($size * 0.075))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $srcX = & $u 0.10; $srcY = & $v 0.50
    $dstX = & $u 0.90
    foreach ($t in @(0.13, 0.50, 0.87)) {
        $dstY = & $v $t
        $g.DrawBezier($pen, $srcX, $srcY, (& $u 0.50), $srcY, (& $u 0.50), $dstY, $dstX, $dstY)
    }
    $pen.Dispose()

    $brush = New-Object System.Drawing.SolidBrush($color)
    $rBig = $size * 0.135
    $g.FillEllipse($brush, ($srcX - $rBig), ($srcY - $rBig), ($rBig * 2), ($rBig * 2))
    $rSmall = $size * 0.095
    foreach ($t in @(0.13, 0.50, 0.87)) {
        $dstY = & $v $t
        $g.FillEllipse($brush, ($dstX - $rSmall), ($dstY - $rSmall), ($rSmall * 2), ($rSmall * 2))
    }
    $brush.Dispose()
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [single]($size * 0.22)
    $plate = New-RoundedPath 0 0 ([single]$size) ([single]$size) $radius
    $plateBrush = New-Object System.Drawing.SolidBrush($Surface)
    $g.FillPath($plateBrush, $plate)
    $plateBrush.Dispose()

    if ($size -ge 32) {
        $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 0xE8, 0xA3, 0x3D), [single]($size * 0.02))
        $g.DrawPath($edge, $plate)
        $edge.Dispose()
    }
    $plate.Dispose()

    $inset = [single]($size * 0.20)
    Draw-Mark $g $inset $inset ([single]($size - 2 * $inset)) $Accent

    $g.Dispose()
    return $bmp
}

# ICO container with PNG compressed entries, which Windows has read since Vista.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim)
    $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$entry.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $pngs) { $w.Write($entry.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($iconPath, $out.ToArray())
$w.Dispose(); $out.Dispose()
Write-Output "icon  -> $iconPath"

# Standalone mark for the README header
$mark = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mg = [System.Drawing.Graphics]::FromImage($mark)
$mg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$mg.Clear([System.Drawing.Color]::Transparent)
Draw-Mark $mg 24 24 208 $Accent
$mg.Dispose()
$mark.Save($markPath, [System.Drawing.Imaging.ImageFormat]::Png)
$mark.Dispose()
Write-Output "mark  -> $markPath"

# Banner
$bw = 1280; $bh = 300
$banner = New-Object System.Drawing.Bitmap($bw, $bh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($banner)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

$bgRect = New-Object System.Drawing.Rectangle(0, 0, $bw, $bh)
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $bgRect, [System.Drawing.Color]::FromArgb(0x14, 0x12, 0x0F), $Ink, 25.0)
$g.FillRectangle($bgBrush, $bgRect)
$bgBrush.Dispose()

# Meter bars on the right: the app's own vocabulary, at three send levels.
$barX = 830; $barW = 360; $barH = 12
$levels = @(0.86, 0.62, 0.41)
$row = 0
foreach ($level in $levels) {
    $y = 100 + $row * 44
    $trackPath = New-RoundedPath $barX $y $barW $barH ($barH / 2)
    $trackBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x22, 0x1F, 0x1A))
    $g.FillPath($trackBrush, $trackPath)
    $trackBrush.Dispose(); $trackPath.Dispose()

    $fillW = [single]($barW * $level)
    $fillPath = New-RoundedPath $barX $y $fillW $barH ($barH / 2)
    $fillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xE8, 0xA3, 0x3D))
    $g.FillPath($fillBrush, $fillPath)
    $fillBrush.Dispose(); $fillPath.Dispose()
    $row++
}

Draw-Mark $g 104 86 128 $Accent

$titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 74, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$taglineFont = New-Object System.Drawing.Font('Segoe UI', 25, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$titleBrush = New-Object System.Drawing.SolidBrush($Text)
$taglineBrush = New-Object System.Drawing.SolidBrush($TextDim)

$g.DrawString('LoopIt7', $titleFont, $titleBrush, 258, 84)
$g.DrawString('Send one source to every output at once.', $taglineFont, $taglineBrush, 265, 182)

$titleFont.Dispose(); $taglineFont.Dispose(); $titleBrush.Dispose(); $taglineBrush.Dispose()
$g.Dispose()
$banner.Save($bannerPath, [System.Drawing.Imaging.ImageFormat]::Png)
$banner.Dispose()
Write-Output "banner -> $bannerPath"

# Installer artwork. Inno Setup wants 24 bit BMPs at these exact sizes.
$installerDir = Join-Path $root 'installer'
New-Item -ItemType Directory -Force $installerDir | Out-Null

$side = New-Object System.Drawing.Bitmap(164, 314, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$sg = [System.Drawing.Graphics]::FromImage($side)
$sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$sideRect = New-Object System.Drawing.Rectangle(0, 0, 164, 314)
$sideBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $sideRect, [System.Drawing.Color]::FromArgb(0x1A, 0x17, 0x14), $Ink, 60.0)
$sg.FillRectangle($sideBrush, $sideRect)
$sideBrush.Dispose()
Draw-Mark $sg 40 92 84 $Accent
$sideFont = New-Object System.Drawing.Font('Segoe UI Semibold', 21, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$sideText = New-Object System.Drawing.SolidBrush($Text)
$sg.DrawString('LoopIt7', $sideFont, $sideText, 40, 190)
$sideFont.Dispose(); $sideText.Dispose(); $sg.Dispose()
$side.Save((Join-Path $installerDir 'wizard-large.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$side.Dispose()

$small = New-Object System.Drawing.Bitmap(55, 55, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$mg2 = [System.Drawing.Graphics]::FromImage($small)
$mg2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$mg2.Clear($Surface)
Draw-Mark $mg2 6 6 43 $Accent
$mg2.Dispose()
$small.Save((Join-Path $installerDir 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$small.Dispose()
Write-Output "wizard -> $installerDir"
