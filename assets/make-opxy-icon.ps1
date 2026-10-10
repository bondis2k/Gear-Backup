# Draws the OP-XY icon (assets\OPXY.ico and a 256px PNG) with System.Drawing, in the same style as the
# other device icons: a friendly device with a smiling screen and a coral backup badge. A stylised dark
# graphite body with knobs and a row of step-sequencer keys tells it apart from the silver OP-1 Field.
# Usage: powershell -File assets\make-opxy-icon.ps1 -OutDir assets
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
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))), (RoundRect 14 70 236 136 20))

    # Wide dark graphite body
    $body = RoundRect 8 58 236 136 20
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 58)), (New-Object System.Drawing.PointF(0, 194)),
        (Color '#585E68'), (Color '#2A2E35'))
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#16191E'), 4)), $body)

    # Screen with the same happy face as the M8 icon
    $screen = RoundRect 24 74 92 62 10
    $g.FillPath((Brush '#0E1420'), $screen)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#8E949D'), 3)), $screen)
    $face = Brush '#7DF9B4'
    if ($small) {
        $g.FillRectangle($face, 44, 86, 14, 18)
        $g.FillRectangle($face, 82, 86, 14, 18)
    } else {
        $g.FillPath($face, (RoundRect 46 86 12 17 4))
        $g.FillPath($face, (RoundRect 82 86 12 17 4))
        $cheek = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 120, 160))
        $g.FillEllipse($cheek, 34, 106, 14, 8)
        $g.FillEllipse($cheek, 92, 106, 14, 8)
    }
    $smile = New-Object System.Drawing.Pen($face.Color, $(if ($small) { 9 } else { 6 }))
    $smile.StartCap = 'Round'; $smile.EndCap = 'Round'
    $g.DrawArc($smile, 56, 100, 28, 20, 20, 140)

    # Four coloured knobs: blue, green, white, orange
    $knobs = @('#3D7FE0', '#35B865', '#F7F7F7', '#F36F2D')
    for ($i = 0; $i -lt 4; $i++) {
        $cx = 140 + $i * 28; $cy = 98; $r = $(if ($small) { 13 } else { 11 })
        $g.FillEllipse((Brush '#3A3F47'), $cx - $r - 2, $cy - $r - 2, 2 * $r + 4, 2 * $r + 4)
        $g.FillEllipse((Brush $knobs[$i]), $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        if (-not $small) { $g.FillRectangle((Brush '#3A3F47'), $cx - 1.5, $cy - $r + 2, 3, 7) }
    }

    # Step-sequencer keys: a row of light keys, some lit
    if (-not $small) {
        $key = Brush '#D9DCE1'
        $lit = @{ 0 = '#F36F2D'; 3 = '#7DF9B4'; 4 = '#F36F2D'; 7 = '#7DF9B4' }
        for ($row = 0; $row -lt 2; $row++) {
            for ($i = 0; $i -lt 8; $i++) {
                $x = 24 + $i * 26; $y = 146 + $row * 22
                $k = RoundRect $x $y 22 16 4
                if ($row -eq 0 -and $lit.ContainsKey($i)) { $g.FillPath((Brush $lit[$i]), $k) } else { $g.FillPath($key, $k) }
            }
        }
    }

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
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'OPXY-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
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
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'OPXY.ico'), $ico.ToArray())
"done"
