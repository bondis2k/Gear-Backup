# Makes the small device icons shown beside the device names in the manual's "Connecting a device" table.
# They are 80 px copies of the app's device pictures (the manual shows them at 40 px so they stay sharp).
# Usage: powershell -File assets\make-manual-device-icons.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot 'manual'))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$icons = @{ 'M8' = 'device-m8'; 'TrackerMini' = 'device-tracker-mini'; 'OP1Field' = 'device-op1-field'; 'OPXY' = 'device-opxy'; 'X100VI' = 'device-x100vi' }
foreach ($name in $icons.Keys) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot "$name-256.png"))
    $bitmap = New-Object System.Drawing.Bitmap 80, 80, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($source, 0, 0, 80, 80)
        $bitmap.Save((Join-Path $OutDir ($icons[$name] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $g.Dispose(); $bitmap.Dispose(); $source.Dispose() }
}
