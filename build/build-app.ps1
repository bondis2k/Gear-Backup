# Shared production build; GUI manual generation also requires PowerShell 7.
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
if (-not $Output) { $Output = if ($Target -eq 'GUI') { 'GearBackup.GUI.exe' } else { 'GearBackup.exe' } }
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
$arguments = @('/nologo', "/langversion:$CSharpLanguageVersion", "/out:$Output", "/win32icon:$projectRoot\assets\Backup.ico",
    '/r:System.Management.dll', '/r:Microsoft.VisualBasic.dll', '/r:System.IO.Compression.dll')
foreach ($reference in @('Windows.Data.winmd', 'Windows.UI.winmd', 'Windows.Foundation.winmd')) {
    $arguments += "/r:$(Join-Path $metadataPath $reference)"
}
foreach ($reference in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll')) {
    $arguments += "/r:$(Join-Path $frameworkPath $reference)"
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
if ($Target -eq 'GUI') {
    $manualShell = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if (-not $manualShell) { throw 'PowerShell 7 is required to generate the HTML manual. Install it from https://aka.ms/powershell.' }
    $arguments += @('/target:winexe', '/main:GearGui', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    $arguments += "/win32manifest:$projectRoot\gui\app.manifest"
    $arguments += "/resource:$projectRoot\assets\Backup.ico,Backup.ico"
    foreach ($image in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets') -Filter '*-256.png') {
        $arguments += "/resource:$($image.FullName),$($image.Name)"
    }
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'gui') -Filter '*.cs' -Recurse | ForEach-Object FullName)
} else { $arguments += @('/target:exe', '/main:GearBackup') }
& $compilerPath $arguments $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Target -eq 'GUI') {
    & $manualShell.Source -NoProfile -File (Join-Path $PSScriptRoot 'build-manual.ps1') -OutputDirectory (Split-Path $Output -Parent)
    if ($LASTEXITCODE -ne 0) { throw 'HTML manual generation failed.' }
}
Write-Output "Built $Output"
