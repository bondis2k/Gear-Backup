$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'Notifications.cs' | ForEach-Object FullName)
# Every test file except the GUI tests, which need Windows Forms (run-gui-tests.ps1).
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -ne 'GuiTests.cs' | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'GearBackup.Tests.exe'
& $compilerPath /nologo /main:TestRunner "/out:$runnerPath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll $testSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
try { & $runnerPath; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult
