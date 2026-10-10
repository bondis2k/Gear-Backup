#requires -Version 7.0
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = Join-Path $projectRoot 'dist'
$payloadPath = Join-Path $outputPath 'installer-payload'

if (-not $IsccPath) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        $(if ($compilerCommand) { $compilerCommand.Source }),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7 (32-bit)\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6 (32-bit)\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe')
    )
    $IsccPath = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Inno Setup 6.3+ or 7 is required. Install from https://jrsoftware.org/isdl.php or pass -IsccPath to ISCC.exe.'
}
Write-Output "Compiler: $IsccPath"

New-Item -ItemType Directory -Path $payloadPath -Force | Out-Null
Push-Location $projectRoot
try {
    & (Join-Path $projectRoot 'build.cmd') '--no-pause' (Join-Path $payloadPath 'GearBackup.exe')
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    & (Join-Path $projectRoot 'build-gui.cmd') '--no-pause' (Join-Path $payloadPath 'GearBackup.GUI.exe')
    if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }
} finally { Pop-Location }

# The compiled application is the authority for the release version.
$applicationVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadPath 'GearBackup.exe')).ProductVersion
$guiVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadPath 'GearBackup.GUI.exe')).ProductVersion
if ($applicationVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$' -or $guiVersion -ne $applicationVersion) {
    throw 'The executables must have the same valid product version.'
}
if ($Version -and $Version -ne $applicationVersion) {
    throw "Requested installer version $Version differs from application version $applicationVersion. Update AppIdentity.Version before building a release."
}
$Version = $applicationVersion

Copy-Item -LiteralPath (Join-Path $projectRoot 'MANUAL.md'), (Join-Path $projectRoot 'README.md') -Destination $payloadPath
$imagePath = Join-Path $payloadPath 'assets\manual'
New-Item -ItemType Directory -Path $imagePath -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'assets\manual\*.png') -Destination $imagePath
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\Backup.ico') -Destination $payloadPath

# Use PowerShell's Markdown renderer to provide an offline manual that opens in any browser.
$manualBody = (ConvertFrom-Markdown -LiteralPath (Join-Path $projectRoot 'MANUAL.md')).Html
$manualHtml = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Gear Backup Manual</title>
<style>
body{margin:0;background:#f3f6f6;color:#1d2d36;font:16px/1.65 "Segoe UI",sans-serif}
main{max-width:1080px;margin:32px auto;padding:32px 40px;background:white;border-radius:18px}
h1,h2,h3{color:#00544c;line-height:1.3}h2{margin-top:2em}a{color:#00756a}
img{display:block;max-width:100%;height:auto;margin:24px auto;border-radius:12px}
pre{padding:16px;background:#f3f6f6;border-radius:8px;overflow:auto}code{font-family:Consolas,monospace}
table{border-collapse:collapse;width:100%}td,th{padding:10px;text-align:left;border:1px solid #dce7e5}
th{background:#e7f5f2}blockquote{border-left:4px solid #f2546e;padding-left:16px}
@media(max-width:700px){main{margin:0;padding:20px;border-radius:0}table{display:block;overflow:auto}}
</style></head><body><main>$manualBody</main></body></html>
"@
Set-Content -LiteralPath (Join-Path $payloadPath 'Manual.html') -Value $manualHtml -Encoding utf8

# The wizard uses the same smiling card as the app, on its light background.
Add-Type -AssemblyName System.Drawing
$iconImage = [System.Drawing.Image]::FromFile((Join-Path $projectRoot 'assets\Backup-256.png'))
$wizardImage = [System.Drawing.Bitmap]::new(96, 96)
$graphics = [System.Drawing.Graphics]::FromImage($wizardImage)
try {
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#F3F6F6'))
    $graphics.InterpolationMode = 'HighQualityBicubic'
    $graphics.DrawImage($iconImage, 8, 8, 80, 80)
    $wizardImage.Save((Join-Path $payloadPath 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
} finally { $graphics.Dispose(); $wizardImage.Dispose(); $iconImage.Dispose() }

foreach ($variant in @('Setup', 'Portable')) {
    $compilerArguments = @("/DAppVersion=$Version", "/DPayloadDir=$payloadPath", "/DOutputPath=$outputPath")
    if ($variant -eq 'Portable') { $compilerArguments += '/DPortableBuild=1' }
    & $IsccPath $compilerArguments (Join-Path $PSScriptRoot 'GearBackup.iss')
    if ($LASTEXITCODE -ne 0) { throw "$variant installer compilation failed." }
    $installerPath = Join-Path $outputPath "GearBackup-$variant-$Version.exe"
    $checksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$installerPath.sha256" -Value "$checksum  $(Split-Path $installerPath -Leaf)" -Encoding ascii
    Write-Output "${variant}: $installerPath"
}
