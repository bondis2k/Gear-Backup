$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
. (Join-Path $projectRoot 'build\csharp-compiler.ps1')
$compilerPath = Get-CSharpCompiler
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'Notifications.cs' | ForEach-Object FullName)
# Every test file except the GUI tests, which need Windows Forms (run-gui-tests.ps1).
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -ne 'GuiTests.cs' | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'GearBackup.Tests.exe'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /main:TestRunner "/out:$runnerPath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll $testSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
try { & $runnerPath; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult
