# Draws the Polyend Tracker Mini icon (assets\TrackerMini.ico and a 256px PNG) with System.Drawing, in the
# same style as the other device icons: a friendly device with a smiling screen and a coral backup badge.
# A black portrait body like the real one: a wide screen with a row of eight buttons under it, the
# D-pad, the coloured Note/Instr/FX1/FX2 keys with a red Rec, and the master volume column.
# Usage: powershell -File assets\make-tracker-icon.ps1 -OutDir assets
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
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))), (RoundRect 38 14 192 240 14))

    # Black portrait body
    $body = RoundRect 32 6 192 242 14
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 6)), (New-Object System.Drawing.PointF(0, 248)),
        (Color '#34373D'), (Color '#17181B'))
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#0A0B0D'), 4)), $body)

    # Wide screen at the top with the same happy face as the M8 icon
    $screen = RoundRect 48 20 160 92 6
    $g.FillPath((Brush '#0E1420'), $screen)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#5A5F68'), 3)), $screen)
    $face = Brush '#7DF9B4'
    if ($small) {
        $g.FillRectangle($face, 94, 38, 18, 24)
        $g.FillRectangle($face, 144, 38, 18, 24)
    } else {
        $g.FillPath($face, (RoundRect 100 42 12 19 4))
        $g.FillPath($face, (RoundRect 144 42 12 19 4))
        $cheek = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 120, 160))
        $g.FillEllipse($cheek, 84, 68, 16, 9)
        $g.FillEllipse($cheek, 156, 68, 16, 9)
    }
    $smile = New-Object System.Drawing.Pen($face.Color, $(if ($small) { 9 } else { 6 }))
    $smile.StartCap = 'Round'; $smile.EndCap = 'Round'
    $g.DrawArc($smile, 110, 58, 36, 26, 20, 140)

    $key = Brush '#4C5059'
    # Row of eight buttons under the screen
    if (-not $small) {
        for ($i = 0; $i -lt 8; $i++) { $g.FillPath($key, (RoundRect (50 + $i * 20) 120 15 7 2)) }
    }

    # D-pad on the left
    $dpad = Brush '#5C616B'
    $g.FillPath($dpad, (RoundRect 66 146 12 40 3))
    $g.FillPath($dpad, (RoundRect 52 160 40 12 3))
    if (-not $small) { $g.FillEllipse((Brush '#2A2D33'), 67, 161, 10, 10) }

    # Coloured Note, Instr, FX1 and FX2 keys, with Rec in red below
    $colours = @('#4FD1B5', '#F2D94E', '#E07AD8', '#7CC8E8')
    for ($i = 0; $i -lt 4; $i++) { $g.FillPath((Brush $colours[$i]), (RoundRect (102 + $i * 21) 138 16 9 2)) }
    $g.FillPath((Brush '#F0544F'), (RoundRect 123 154 16 9 2))
    if (-not $small) {
        # Play, Insert, Menu, Delete, Copy, the number keys and Shift
        $g.FillPath($key, (RoundRect 102 154 16 9 2))
        foreach ($row in 0, 1) { foreach ($col in 0, 1) { $g.FillPath($key, (RoundRect (102 + $col * 21) (170 + $row * 15) 16 9 2)) } }
        for ($i = 0; $i -lt 4; $i++) { $g.FillPath($key, (RoundRect (84 + $i * 21) 201 16 9 2)) }
        $g.FillPath($key, (RoundRect 110 217 36 9 2))
        # Master volume column on the right
        for ($i = 0; $i -lt 4; $i++) { $g.FillPath($key, (RoundRect 178 (146 + $i * 14) 16 9 2)) }
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
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'TrackerMini-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
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
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'TrackerMini.ico'), $ico.ToArray())
"done"
