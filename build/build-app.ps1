# Shared production build; Windows PowerShell and the built-in .NET compiler are sufficient.
param(
    [ValidateSet('CLI', 'GUI')][string]$Target = 'CLI',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadataPath = Join-Path $env:WINDIR 'System32\WinMetadata'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The Windows .NET Framework C# compiler was not found.' }
if (-not $Output) { $Output = if ($Target -eq 'GUI') { 'GearBackup.GUI.exe' } else { 'GearBackup.exe' } }
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
$arguments = @('/nologo', "/out:$Output", "/win32icon:$projectRoot\assets\Backup.ico",
    '/r:System.Management.dll', '/r:Microsoft.VisualBasic.dll', '/r:System.IO.Compression.dll')
foreach ($reference in @('Windows.Data.winmd', 'Windows.UI.winmd', 'Windows.Foundation.winmd')) {
    $arguments += "/r:$(Join-Path $metadataPath $reference)"
}
foreach ($reference in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll')) {
    $arguments += "/r:$(Join-Path $frameworkPath $reference)"
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
if ($Target -eq 'GUI') {
    $arguments += @('/target:winexe', '/main:GearGui', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    foreach ($image in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'assets') -Filter '*-256.png') {
        $arguments += "/resource:$($image.FullName),$($image.Name)"
    }
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'gui') -Filter '*.cs' -Recurse | ForEach-Object FullName)
} else { $arguments += @('/target:exe', '/main:GearBackup') }
& $compilerPath $arguments $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Built $Output"
