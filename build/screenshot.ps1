# Captures the running LoopIt7 window to a PNG. Used to check the interface during
# development and to produce the screenshot in the README.
#
#   powershell -ExecutionPolicy Bypass -File build\screenshot.ps1 -Out assets\app.png
#
# PrintWindow reads the window's own composition surface, so the capture never picks up
# whatever else happens to be on screen and the window does not need to be in front.

param([string]$Out = 'assets\app.png')

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Shot {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }

$proc = Get-Process LoopIt7 -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw 'LoopIt7 is not running with a visible window.' }

$handle = $proc.MainWindowHandle

# DWMWA_EXTENDED_FRAME_BOUNDS, so the size matches what the user sees.
$rect = New-Object Shot+RECT
[void][Shot]::DwmGetWindowAttribute($handle, 9, [ref]$rect, 16)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT, required for composed WPF surfaces.
$ok = [Shot]::PrintWindow($handle, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

if (-not $ok) { $bmp.Dispose(); throw 'PrintWindow failed.' }

New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "shot -> $outPath ($w x $h)"
