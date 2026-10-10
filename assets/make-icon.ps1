# Draws the M8 Backup icon (assets\M8Backup.ico and a 256px PNG) with System.Drawing.
# Usage: powershell -File assets\make-icon.ps1 -OutDir assets
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

# Draws the icon on a 256-unit canvas, scaled to $size pixels.
function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 24

    # Soft shadow
    $shadow = RoundRect 46 30 176 214 38
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 0, 0, 0))), $shadow)

    # Device body: rounded, warm dark grey with a gentle top highlight
    $body = RoundRect 38 18 176 214 38
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 18)), (New-Object System.Drawing.PointF(0, 232)),
        (Color '#4A4F5C'), (Color '#262931'))
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#15171C'), 5)), $body)

    # Screen
    $screen = RoundRect 58 38 136 104 16
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#0E1420')), $screen)
    $g.DrawPath((New-Object System.Drawing.Pen((Color '#5B6272'), 4)), $screen)

    # Happy face: pixel eyes, rosy cheeks, smile
    $mint = New-Object System.Drawing.SolidBrush (Color '#7DF9B4')
    if ($small) {
        $g.FillRectangle($mint, 88, 66, 22, 26)
        $g.FillRectangle($mint, 142, 66, 22, 26)
    } else {
        $g.FillPath($mint, (RoundRect 90 64 18 26 5))
        $g.FillPath($mint, (RoundRect 144 64 18 26 5))
        $pink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 120, 160))
        $g.FillEllipse($pink, 74, 94, 22, 13)
        $g.FillEllipse($pink, 156, 94, 22, 13)
    }
    $smilePen = New-Object System.Drawing.Pen($mint.Color, $(if ($small) { 12 } else { 8 }))
    $smilePen.StartCap = 'Round'; $smilePen.EndCap = 'Round'
    $g.DrawArc($smilePen, 106, 88, 40, 30, 20, 140)

    # Buttons: a little d-pad on the left, four keys on the right (the M8 has eight)
    if (-not $small) {
        $key = New-Object System.Drawing.SolidBrush (Color '#C9CED8')
        foreach ($k in @(@(78, 158), @(78, 194), @(60, 176), @(96, 176))) {
            $g.FillPath($key, (RoundRect $k[0] $k[1] 18 18 5))
        }
        $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#FFB3C7')), (RoundRect 136 160 20 20 6))
        $g.FillPath($key, (RoundRect 164 160 20 20 6))
        $g.FillPath($key, (RoundRect 136 188 20 20 6))
        $g.FillPath((New-Object System.Drawing.SolidBrush (Color '#7DF9B4')), (RoundRect 164 188 20 20 6))
    }

    # Backup badge: a round coral bubble with a white arrow into a tray
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color '#FFFFFF')), 150, 152, 104, 104)
    $badgeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 160)), (New-Object System.Drawing.PointF(0, 248)),
        (Color '#FF8A73'), (Color '#F2546E'))
    $g.FillEllipse($badgeBrush, 158, 160, 88, 88)
    $white = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $(if ($small) { 16 } else { 11 }))
    $white.StartCap = 'Round'; $white.EndCap = 'Round'; $white.LineJoin = 'Round'
    $g.DrawLine($white, 202, 176, 202, 214)
    $g.DrawLines($white, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(186, 200)), (New-Object System.Drawing.PointF(202, 216)), (New-Object System.Drawing.PointF(218, 200))))
    if (-not $small) {
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
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'M8Backup-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
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
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'M8Backup.ico'), $ico.ToArray())

# Preview sheet: every size on a light and a dark background
$sheet = New-Object System.Drawing.Bitmap(760, 300)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear((Color '#F4F4F6'))
$g.FillRectangle((New-Object System.Drawing.SolidBrush (Color '#1E1F24')), 0, 150, 760, 150)
$x = 10
foreach ($size in $sizes) {
    $bmp = Render $size
    $g.DrawImage($bmp, $x, [int](75 - $size / 2 + 0), $size, $size)
    $g.DrawImage($bmp, $x, [int](225 - $size / 2), $size, $size)
    $x += [Math]::Max($size, 30) + 14
    $bmp.Dispose()
}
$g.Dispose()
$sheet.Save((Join-Path $OutDir 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"done"
