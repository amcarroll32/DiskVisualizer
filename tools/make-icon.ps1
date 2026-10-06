<#
.SYNOPSIS
  Draws the app icon (a tiny treemap on a dark rounded tile) and writes Assets\app.ico.

.DESCRIPTION
  Each size is drawn natively rather than downscaled, so small sizes stay crisp:
  16-24 px use a simpler three-block layout with 1 px gaps. Colors are the app's
  dark-theme file-type colors. Run from anywhere:  powershell -File tools\make-icon.ps1
#>
param([string]$OutFile = (Join-Path $PSScriptRoot '..\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$tileFill   = [System.Drawing.ColorTranslator]::FromHtml('#262624')
$tileBorder = [System.Drawing.ColorTranslator]::FromHtml('#55544f')
$blue   = [System.Drawing.ColorTranslator]::FromHtml('#3987e5')
$orange = [System.Drawing.ColorTranslator]::FromHtml('#d95926')
$aqua   = [System.Drawing.ColorTranslator]::FromHtml('#199e70')
$yellow = [System.Drawing.ColorTranslator]::FromHtml('#c98500')

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $path.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $path }
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-Block($g, [System.Drawing.Color]$color, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $brush = New-Object System.Drawing.SolidBrush $color
    $path = New-RoundedRect $x $y $w $h $r
    $g.FillPath($brush, $path)
    $path.Dispose(); $brush.Dispose()
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $small = $size -le 24
    $s = $size / 256.0

    # Tile
    $margin = if ($small) { 0.5 } else { 8 * $s }
    $radius = if ($small) { [Math]::Max(2, $size * 0.18) } else { 48 * $s }
    $tile = New-RoundedRect $margin $margin ($size - 2 * $margin) ($size - 2 * $margin) $radius
    $g.FillPath((New-Object System.Drawing.SolidBrush $tileFill), $tile)
    $penWidth = if ($small) { 1 } else { [Math]::Max(1, 6 * $s) }
    $g.DrawPath((New-Object System.Drawing.Pen $tileBorder, $penWidth), $tile)

    if ($small) {
        # Three blocks on whole pixels with 1 px gaps.
        $pad = [Math]::Round($size * 0.22)
        $inner = $size - 2 * $pad
        $leftW = [Math]::Round($inner * 0.55)
        $rightX = $pad + $leftW + 1
        $rightW = $size - $pad - $rightX
        $topH = [Math]::Round($inner * 0.5)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
        Fill-Block $g $blue   $pad    $pad $leftW  $inner 0
        Fill-Block $g $orange $rightX $pad $rightW $topH 0
        Fill-Block $g $aqua   $rightX ($pad + $topH + 1) $rightW ($inner - $topH - 1) 0
    }
    else {
        # Four blocks: a big file on the left, a folder of three on the right.
        $pad = 40 * $s; $gap = 10 * $s; $r = 10 * $s
        $x0 = $pad; $y0 = $pad; $w = $size - 2 * $pad; $h = $w
        $leftW = $w * 0.56
        $x1 = $x0 + $leftW + $gap
        $rightW = $w - $leftW - $gap
        $topH = $h * 0.55
        $bottomY = $y0 + $topH + $gap
        $bottomH = $h - $topH - $gap
        $halfW = ($rightW - $gap) / 2
        Fill-Block $g $blue   $x0 $y0 $leftW $h $r
        Fill-Block $g $orange $x1 $y0 $rightW $topH $r
        Fill-Block $g $aqua   $x1 $bottomY $halfW $bottomH $r
        Fill-Block $g $yellow ($x1 + $halfW + $gap) $bottomY $halfW $bottomH $r
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Pack PNG-compressed images into an .ico (supported since Windows Vista).
$images = foreach ($size in $sizes) { , (Draw-Icon $size) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutFile)) | Out-Null
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
"Wrote $OutFile ($($sizes -join ', ') px)"
