# Draws the Fujifilm X100VI icon (assets\X100VI.ico and a 256px PNG) with System.Drawing, in the same
# style as the other device icons: a friendly device with a smiling face and a coral backup badge. A
# front view of the camera: silver top plate, black body, the viewfinder window and a big lens, whose
# glass carries the smiling face.
# Usage: powershell -File assets\make-x100vi-icon.ps1 -OutDir assets
param([string]$OutDir)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function Color([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Brush([string]$hex) { return New-Object System.Drawing.SolidBrush (Color $hex) }

# Draws the icon on a 256-unit canvas, scaled to $size pixels.
function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 32

    # Soft shadow
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))), (RoundRect 12 56 240 160 20))

    # Black body with a silver top plate
    $body = RoundRect 4 46 240 162 20
    $g.FillPath((Brush '#1C1D20'), $body)
    $top = New-Object System.Drawing.Drawing2D.GraphicsPath
    $top.AddArc(4, 46, 40, 40, 180, 90)
    $top.AddArc(204, 46, 40, 40, 270, 90)
    $top.AddLine(244, 66, 244, 92)
    $top.AddLine(244, 92, 4, 92)
    $top.CloseFigure()
    $silver = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 46)), (New-Object System.Drawing.PointF(0, 92)),
        (Color '#F2F3F5'), (Color '#A9ADB4'))
    $g.FillPath($silver, $top)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#0A0B0D'), 4)), $body)
    $g.DrawLine((New-Object System.Drawing.Pen((Color '#6E737C'), 2)), 6, 92, 242, 92)

    # Shutter button and dial on top
    $g.FillPath((Brush '#C3C7CD'), (RoundRect 176 34 44 14 4))
    $g.FillEllipse((Brush '#8E939B'), 190, 26, 16, 12)

    # Viewfinder window at the top left
    $g.FillPath((Brush '#2E3A4A'), (RoundRect 20 58 42 26 5))
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#5A5F68'), 3)), (RoundRect 20 58 42 26 5))
    if (-not $small) { $g.FillEllipse((Brush '#7FA3C8'), 28, 63, 10, 7) }

    # Big lens with the same happy face as the M8 icon on its glass
    $g.FillEllipse((Brush '#B9BDC3'), 70, 70, 132, 132)
    $g.FillEllipse((Brush '#2A2C31'), 78, 78, 116, 116)
    $g.FillEllipse((Brush '#0E1420'), 90, 90, 92, 92)
    $face = Brush '#7DF9B4'
    if ($small) {
        $g.FillRectangle($face, 108, 108, 16, 22)
        $g.FillRectangle($face, 148, 108, 16, 22)
    } else {
        $g.FillPath($face, (RoundRect 112 110 11 18 4))
        $g.FillPath($face, (RoundRect 149 110 11 18 4))
        $cheek = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 120, 160))
        $g.FillEllipse($cheek, 100, 134, 14, 8)
        $g.FillEllipse($cheek, 158, 134, 14, 8)
    }
    $smile = New-Object System.Drawing.Pen($face.Color, $(if ($small) { 9 } else { 6 }))
    $smile.StartCap = 'Round'; $smile.EndCap = 'Round'
    $g.DrawArc($smile, 118, 124, 36, 26, 20, 140)

    # Backup badge: the same coral bubble with a white arrow into a tray
    $g.FillEllipse((Brush '#FFFFFF'), 150, 152, 104, 104)
    $badge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 160)), (New-Object System.Drawing.PointF(0, 248)),
        (Color '#FF8A73'), (Color '#F2546E'))
    $g.FillEllipse($badge, 158, 160, 88, 88)
    $white = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $(if ($size -le 24) { 16 } else { 11 }))
    $white.StartCap = 'Round'; $white.EndCap = 'Round'; $white.LineJoin = 'Round'
    $g.DrawLine($white, 202, 176, 202, 214)
    $g.DrawLines($white, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(186, 200)), (New-Object System.Drawing.PointF(202, 216)), (New-Object System.Drawing.PointF(218, 200))))
    if ($size -gt 24) {
        $g.DrawLines($white, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(180, 216)), (New-Object System.Drawing.PointF(180, 228)),
            (New-Object System.Drawing.PointF(224, 228)), (New-Object System.Drawing.PointF(224, 216))))
    }

    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @()
foreach ($size in $sizes) {
    $bmp = Render $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($size, $ms.ToArray())
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'X100VI-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO file: header, one directory entry per size, then the PNG images
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$img[1].Length); $w.Write([UInt32]$offset)
    $offset += $img[1].Length
}
foreach ($img in $images) { $w.Write($img[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'X100VI.ico'), $ico.ToArray())
"done"
