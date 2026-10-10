# Draws the general M8 Backup icon (assets\Backup.ico and a 256px PNG) with System.Drawing: a teal tile
# with music notes and the coral backup badge shared with the device icons. It's the app's icon (exe,
# window, taskbar) and the GUI header's picture until a device is detected.
# Usage: powershell -File assets\make-backup-icon.ps1 -OutDir assets
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

# A note head: an ellipse tilted like a written note.
function NoteHead($g, $brush, [float]$cx, [float]$cy, [float]$w, [float]$h) {
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy)
    $g.RotateTransform(-22)
    $g.FillEllipse($brush, -$w / 2, -$h / 2, $w, $h)
    $g.Restore($state)
}

# Draws the icon on a 256-unit canvas, scaled to $size pixels.
function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Soft shadow and the teal tile
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))), (RoundRect 26 26 200 200 44))
    $tile = RoundRect 18 14 200 200 44
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 14)), (New-Object System.Drawing.PointF(0, 214)),
        (Color '#1FB3A1'), (Color '#00675D'))
    $g.FillPath($tileBrush, $tile)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#00544C'), 4)), $tile)
    # A soft highlight across the top
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 255, 255, 255))), (RoundRect 30 24 176 70 34))

    # Two beamed music notes in white
    $white = Brush '#FFFFFF'
    $stem = $(if ($size -le 24) { 14 } else { 11 })
    NoteHead $g $white 82 160 46 34
    NoteHead $g $white 150 140 46 34
    $g.FillRectangle($white, 97, 70, $stem, 92)
    $g.FillRectangle($white, 165, 50, $stem, 92)
    $beam = New-Object System.Drawing.Drawing2D.GraphicsPath
    $beam.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(97, 70)), (New-Object System.Drawing.PointF(176, 50)),
        (New-Object System.Drawing.PointF(176, 76)), (New-Object System.Drawing.PointF(97, 96))))
    $g.FillPath($white, $beam)

    # Backup badge: the coral bubble with a white arrow into a tray, as on the device icons
    $g.FillEllipse((Brush '#FFFFFF'), 150, 152, 104, 104)
    $badge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 160)), (New-Object System.Drawing.PointF(0, 248)),
        (Color '#FF8A73'), (Color '#F2546E'))
    $g.FillEllipse($badge, 158, 160, 88, 88)
    $arrow = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $(if ($size -le 24) { 16 } else { 11 }))
    $arrow.StartCap = 'Round'; $arrow.EndCap = 'Round'; $arrow.LineJoin = 'Round'
    $g.DrawLine($arrow, 202, 176, 202, 214)
    $g.DrawLines($arrow, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(186, 200)), (New-Object System.Drawing.PointF(202, 216)), (New-Object System.Drawing.PointF(218, 200))))
    if ($size -gt 24) {
        $g.DrawLines($arrow, [System.Drawing.PointF[]]@(
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
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'Backup-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
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
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Backup.ico'), $ico.ToArray())
"done"
