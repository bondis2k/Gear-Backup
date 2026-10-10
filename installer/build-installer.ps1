#requires -Version 7.0
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?[A-Za-z]?$')]
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
$cliMetadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadPath 'GearBackup.exe'))
$guiMetadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadPath 'GearBackup.GUI.exe'))
$applicationVersion = $cliMetadata.ProductVersion
$binaryVersion = $cliMetadata.FileVersion
if ($applicationVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?[A-Za-z]?$' -or $guiMetadata.ProductVersion -ne $applicationVersion -or $guiMetadata.FileVersion -ne $binaryVersion) {
    throw 'The executables must have the same valid product version.'
}
if ($Version -and $Version -ne $applicationVersion) {
    throw "Requested installer version $Version differs from application version $applicationVersion. Update AppIdentity.Version before building a release."
}
$Version = $applicationVersion

Copy-Item -LiteralPath (Join-Path $projectRoot 'MANUAL.md'), (Join-Path $projectRoot 'README.md') -Destination $payloadPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\Backup.ico') -Destination $payloadPath

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
    $compilerArguments = @("/DAppVersion=$Version", "/DBinaryVersion=$binaryVersion", "/DPayloadDir=$payloadPath", "/DOutputPath=$outputPath")
    if ($variant -eq 'Portable') { $compilerArguments += '/DPortableBuild=1' }
    & $IsccPath $compilerArguments (Join-Path $PSScriptRoot 'GearBackup.iss')
    if ($LASTEXITCODE -ne 0) { throw "$variant installer compilation failed." }
    $installerPath = Join-Path $outputPath "GearBackup-$variant-$Version.exe"
    $checksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$installerPath.sha256" -Value "$checksum  $(Split-Path $installerPath -Leaf)" -Encoding ascii
    Write-Output "${variant}: $installerPath"
}
