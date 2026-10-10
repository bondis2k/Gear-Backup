# Rebuild the manual illustrations using the same palette and device icons as the app.
# Usage: powershell -File assets\make-manual-pictures.ps1
# The two GUI screenshots come from the app's synthetic preview (no real device or INI):
# After build-gui.cmd --no-pause, run these commands from the repository root:
# .\GearBackup.GUI.exe --render-preview assets\manual\gui-main.png
# .\GearBackup.GUI.exe --render-settings assets\manual\gui-settings.png
param([string]$OutDir = (Join-Path $PSScriptRoot 'manual'))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r = 20) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function Box($x, $y, $w, $h, $fill = '#FFFFFF', $edge = '#DCE7E5') {
    $path = RoundRect $x $y $w $h
    $brush = New-Object System.Drawing.SolidBrush (Color $fill)
    $pen = New-Object System.Drawing.Pen ((Color $edge), 2)
    try { $script:g.FillPath($brush, $path); $script:g.DrawPath($pen, $path) }
    finally { $path.Dispose(); $brush.Dispose(); $pen.Dispose() }
}
function Label([string]$value, $x, $y, $w, $h, $size = 24, $color = '#1D2D36', [switch]$Bold) {
    $style = if ($Bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $font = New-Object System.Drawing.Font ('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush (Color $color)
    $format = New-Object System.Drawing.StringFormat
    $format.Trimming = 'Word'
    try { $script:g.DrawString($value, $font, $brush, [System.Drawing.RectangleF]::new($x, $y, $w, $h), $format) }
    finally { $font.Dispose(); $brush.Dispose(); $format.Dispose() }
}
function Icon([string]$name, $x, $y, $size = 100) {
    $img = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot ($name + '-256.png')))
    try { $script:g.DrawImage($img, [int]$x, [int]$y, [int]$size, [int]$size) }
    finally { $img.Dispose() }
}
function Arrow($x, $y, $length = 38) {
    $pen = New-Object System.Drawing.Pen ((Color '#00756A'), 5)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    try {
        $script:g.DrawLine($pen, [float]$x, [float]$y, [float]($x + $length), [float]$y)
        $script:g.DrawLine($pen, [float]($x + $length - 10), [float]($y - 10), [float]($x + $length), [float]$y)
        $script:g.DrawLine($pen, [float]($x + $length - 10), [float]($y + 10), [float]($x + $length), [float]$y)
    } finally { $pen.Dispose() }
}
function Badge([string]$value, $x, $y, $fill = '#F2546E') {
    $brush = New-Object System.Drawing.SolidBrush (Color $fill)
    try { $script:g.FillEllipse($brush, [float]$x, [float]$y, 42, 42) }
    finally { $brush.Dispose() }
    Label $value ($x + 12) ($y + 3) 30 38 25 '#FFFFFF' -Bold
}
function FileRow([string]$name, [string]$version, $x, $y, $fill = '#E7F5F2', $color = '#00756A') {
    Box $x $y 308 62 $fill $fill
    Label $name ($x + 16) ($y + 15) 204 35 23 $color -Bold
    Label $version ($x + 225) ($y + 16) 75 34 22 $color
}
function Picture([string]$file, [string]$title, [string]$subtitle, [scriptblock]$draw) {
    $bitmap = New-Object System.Drawing.Bitmap (1280, 660)
    $script:g = [System.Drawing.Graphics]::FromImage($bitmap)
    $script:g.SmoothingMode = 'AntiAlias'
    $script:g.PixelOffsetMode = 'HighQuality'
    $script:g.TextRenderingHint = 'AntiAliasGridFit'
    try {
        $script:g.Clear((Color '#F3F6F6'))
        Icon 'Backup' 38 25 78
        Label $title 132 28 1100 52 36 '#00544C' -Bold
        Label $subtitle 134 81 1090 42 23 '#63747B'
        & $draw
        Label 'GEAR BACKUP  /  VISUAL GUIDE' 42 616 900 32 18 '#63747B'
        $bitmap.Save((Join-Path $OutDir $file), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $script:g.Dispose(); $bitmap.Dispose() }
}

Picture 'device-connections.png' 'From your gear to a backup history' 'Choose the connection for your device, then review the preview before copying.' {
    Box 40 147 470 202
    Icon 'M8' 59 169 110
    Icon 'TrackerMini' 178 169 110
    Label 'M8 / Tracker Mini' 304 169 192 75 25 '#00544C' -Bold
    Label 'USB drive or card reader' 65 293 420 40 24

    Box 40 367 470 218
    Icon 'OP1Field' 60 382 97
    Icon 'OPXY' 184 382 97
    Icon 'X100VI' 310 382 97
    Label 'OP-1 Field / OP-XY / X100VI' 65 486 425 38 25 '#00544C' -Bold
    Label 'MTP: shown without a drive letter' 65 530 420 38 23

    Arrow 521 247 38
    Arrow 521 475 38
    Box 574 147 268 202 '#E7F5F2'
    Label 'Source files' 600 181 220 48 29 '#00544C' -Bold
    Label 'Read directly from the drive or folder.' 600 240 217 89 24
    Box 574 367 268 218 '#E7F5F2'
    Label 'Local mirror' 600 400 220 48 29 '#00544C' -Bold
    Label 'Read from the device into a PC folder first.' 600 459 217 98 24

    Arrow 855 247 38
    Arrow 855 475 38
    Box 909 147 331 438
    Icon 'Backup' 1004 169 130
    Label 'Backup history' 937 318 282 50 30 '#00544C' -Bold
    Label 'Full or incremental backup' 937 383 282 80 26
    Label 'A saved snapshot after every successful run.' 937 486 277 85 24 '#63747B'
}

Picture 'backup-history.png' 'A backup you can browse. A history you can restore.' 'Example: edit one song and delete one sample, then run an incremental backup.' {
    $xs = @(40, 460, 880)
    foreach ($x in $xs) { Box $x 157 360 421 }
    Badge '1' 64 179
    Label 'First full backup' 119 179 259 45 27 '#00544C' -Bold
    FileRow 'Jam.m8s' 'v1' 66 250
    FileRow 'Kick.wav' 'v1' 66 326
    Label 'Snapshot 1' 66 428 302 43 28 '#00544C' -Bold
    Label 'Both files, as they were at the first backup.' 66 477 304 83 24 '#63747B'

    Badge '2' 484 179
    Label 'Next incremental' 539 179 259 45 27 '#00544C' -Bold
    FileRow 'Jam.m8s' 'v2' 486 250
    FileRow 'Kick.wav' 'kept' 486 326 '#FFF0EB' '#AA4D48'
    Label 'Snapshot 2' 486 428 304 43 28 '#00544C' -Bold
    Label 'Only Jam v2. Deleted Kick stays in the browsable backup.' 486 477 304 95 24 '#63747B'

    Badge '3' 904 179
    Label 'Pick a snapshot' 959 179 259 45 27 '#00544C' -Bold
    Label 'Restore snapshot 1' 906 252 307 42 25 '#00544C' -Bold
    Label 'Jam v1 + Kick v1' 906 299 307 42 25
    Label 'Restore snapshot 2' 906 382 307 42 25 '#00544C' -Bold
    Label 'Jam v2 only' 906 429 307 42 25
    Label 'Use a dated snapshot to recover an earlier state.' 906 498 307 76 23 '#63747B'
    Arrow 410 361 38
    Arrow 830 361 38
}

Picture 'restore-modes.png' 'Merge or exact: choose what happens to extra files' 'Example: restore all files from a saved snapshot and replace the changed song.' {
    foreach ($x in @(40, 460, 880)) { Box $x 157 360 421 }
    Label 'Before restore' 66 187 308 45 29 '#00544C' -Bold
    Label 'Saved snapshot' 66 246 308 36 23 '#63747B'
    FileRow 'Jam.m8s' 'v1' 66 289
    Label 'Target card / folder' 66 374 308 36 23 '#63747B'
    FileRow 'Jam.m8s' 'v2' 66 417
    FileRow 'Extra.wav' '' 66 493 '#FFF0EB' '#AA4D48'

    Label 'Result: merge' 486 187 308 45 29 '#00544C' -Bold
    FileRow 'Jam.m8s' 'v1' 486 289
    FileRow 'Extra.wav' 'kept' 486 365 '#FFF0EB' '#AA4D48'
    Label 'Restore snapshot files.' 486 463 308 44 25 '#00544C' -Bold
    Label 'Keep the extra files already on the target.' 486 510 308 67 24 '#63747B'

    Label 'Result: exact' 906 187 308 45 29 '#00544C' -Bold
    FileRow 'Jam.m8s' 'v1' 906 289
    Box 906 365 308 62 '#FFF0EB' '#FFF0EB'
    Label 'Extra.wav removed' 922 380 282 38 23 '#AA4D48' -Bold
    Label 'Confirm removals.' 906 463 308 44 25 '#AA4D48' -Bold
    Label 'Extras are removed after all selected copies succeed.' 906 510 308 67 24 '#63747B'
}
