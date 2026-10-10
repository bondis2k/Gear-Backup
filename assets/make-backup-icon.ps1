# Draws the general Gear Backup icon (assets\Backup.ico and a 256px PNG) with System.Drawing: a teal memory
# card with gold contacts and a smiling label, and the coral backup badge shared with the device icons.
# It stands for any device with a card (synths, cameras, ...). It's the app's icon (exe,
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

# An SD card outline: rounded corners, with the top-right corner cut off.
function CardShape([float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [float]$cut) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddLine($x + $r, $y, $x + $w - $cut, $y)
    $p.AddLine($x + $w - $cut, $y, $x + $w, $y + $cut)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
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

    $small = $size -le 32

    # Soft shadow and the teal memory card
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))), (CardShape 44 22 172 214 22 48))
    $card = CardShape 36 12 172 214 22 48
    $cardBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 12)), (New-Object System.Drawing.PointF(0, 226)),
        (Color '#1FB3A1'), (Color '#00675D'))
    $g.FillPath($cardBrush, $card)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#00544C'), 4)), $card)

    # Gold contacts along the top
    if (-not $small) {
        $gold = Brush '#F4C84F'
        $goldEdge = New-Object System.Drawing.Pen((Color '#B98A1E'), 2)
        for ($i = 0; $i -lt 5; $i++) {
            $contact = RoundRect (56 + $i * 22) 26 14 34 4
            $g.FillPath($gold, $contact); $g.DrawPath($goldEdge, $contact)
        }
    }

    # White label with the same happy face as the device icons
    $label = RoundRect 54 $(if ($small) { 46 } else { 76 }) 136 $(if ($small) { 140 } else { 124 }) 16
    $g.FillPath((Brush '#FFFFFF'), $label)
    $face = Brush '#0E6F65'
    $top = $(if ($small) { 70 } else { 98 })
    if ($small) {
        $g.FillRectangle($face, 82, $top, 20, 30)
        $g.FillRectangle($face, 138, $top, 20, 30)
    } else {
        $g.FillPath($face, (RoundRect 86 $top 16 24 5))
        $g.FillPath($face, (RoundRect 140 $top 16 24 5))
        $cheek = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 255, 120, 160))
        $g.FillEllipse($cheek, 70, ($top + 28), 20, 11)
        $g.FillEllipse($cheek, 152, ($top + 28), 20, 11)
    }
    $smile = New-Object System.Drawing.Pen($face.Color, $(if ($small) { 12 } else { 8 }))
    $smile.StartCap = 'Round'; $smile.EndCap = 'Round'
    $g.DrawArc($smile, 100, ($top + 22), 42, 30, 20, 140)

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
