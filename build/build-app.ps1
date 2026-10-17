# Shared production build; GUI manual generation also requires PowerShell 7.
# Builds GearBackup.Core.dll (everything under src except the command-line front end) next to the executable, then the
# executable itself. Both programs use the core through that DLL.
param(
    [ValidateSet('CLI', 'GUI')][string]$Target = 'CLI',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadataPath = Join-Path $env:WINDIR 'System32\WinMetadata'
# References to .NET Framework itself still come from its folder; only the compiler is newer.
. (Join-Path $PSScriptRoot 'csharp-compiler.ps1')
$compilerPath = Get-CSharpCompiler
if (-not $Output) { $Output = if ($Target -eq 'GUI') { 'dist\app\GearBackup.GUI.exe' } else { 'dist\app\GearBackup.exe' } }
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
$outputDirectory = Split-Path $Output -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$corePath = Join-Path $outputDirectory 'GearBackup.Core.dll'
$sourceRoot = Join-Path $projectRoot 'src'
$cliSources = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Cli') -Filter '*.cs' | ForEach-Object FullName)
$assemblyInfo = Join-Path $sourceRoot 'Foundation\AssemblyInfo.cs'
$references = @('/r:System.Management.dll', '/r:Microsoft.VisualBasic.dll', '/r:System.IO.Compression.dll', '/r:System.Web.Extensions.dll')

$coreSources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -Recurse | Where-Object { $cliSources -notcontains $_.FullName } | ForEach-Object FullName)
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /target:library "/out:$corePath" $references $coreSources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @('/nologo', "/langversion:$CSharpLanguageVersion", "/out:$Output", "/win32icon:$projectRoot\assets\Backup.ico", "/r:$corePath") + $references
foreach ($reference in @('Windows.Data.winmd', 'Windows.UI.winmd', 'Windows.Foundation.winmd')) {
    $arguments += "/r:$(Join-Path $metadataPath $reference)"
}
foreach ($reference in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll')) {
    $arguments += "/r:$(Join-Path $frameworkPath $reference)"
}
# Each program carries the same version information as the core.
$sources = @($assemblyInfo)
if ($Target -eq 'GUI') {
    $manualShell = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if (-not $manualShell) { throw 'PowerShell 7 is required to generate the HTML manual. Install it from https://aka.ms/powershell.' }
    $arguments += @('/target:winexe', '/main:GearBackup.Gui.GearGui', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    $arguments += "/win32manifest:$projectRoot\gui\app.manifest"
    $arguments += "/resource:$projectRoot\assets\Backup.ico,Backup.ico"
    foreach ($image in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets') -Filter '*-256.png') {
        $arguments += "/resource:$($image.FullName),$($image.Name)"
    }
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'gui') -Filter '*.cs' -Recurse | ForEach-Object FullName)
} else {
    $arguments += @('/target:exe', '/main:GearBackup.Cli.Program')
    $sources += $cliSources
}
& $compilerPath $arguments $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Target -eq 'GUI') {
    # The Song tab reads M8 files through m8_reader.dll, which ships next to the GUI executable.
    & (Join-Path $PSScriptRoot 'build-native.ps1') -OutputDirectory $outputDirectory | Out-Null
    & $manualShell.Source -NoProfile -File (Join-Path $PSScriptRoot 'build-manual.ps1') -OutputDirectory $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'HTML manual generation failed.' }
}
Write-Output "Built $Output"
