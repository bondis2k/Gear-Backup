# -Filter TEXT (more than one allowed) runs only the tests whose name contains it; -List shows the tests.
param([string[]]$Filter = @(), [switch]$List)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
# The core keeps its layers (docs\ARCHITECTURE.md); new violations fail before anything is compiled.
& (Join-Path $projectRoot 'build\check-layers.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
. (Join-Path $projectRoot 'build\csharp-compiler.ps1')
$compilerPath = Get-CSharpCompiler
# The native M8 song reader is optional for the tests: without Rust the tests that need it are skipped.
# The tests find the DLL where the build leaves it (native\m8-reader\target\release).
& (Join-Path $projectRoot 'build\build-native.ps1') -Optional | Out-Null
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -Recurse | Where-Object Name -ne 'Notifications.cs' | ForEach-Object FullName)
# Every test file except the GUI tests, which need Windows Forms (run-gui-tests.ps1).
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -notlike 'Gui*' | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'GearBackup.Tests.exe'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /main:TestRunner "/out:$runnerPath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll $testSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
$runnerArguments = @(); foreach ($text in $Filter) { $runnerArguments += @('--filter', $text) }; if ($List) { $runnerArguments += '--list' }
try { & $runnerPath @runnerArguments; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult
