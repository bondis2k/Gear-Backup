# -Filter TEXT (more than one allowed) runs only the tests whose name contains it; -List shows the tests.
param([string[]]$Filter = @(), [switch]$List)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
# The core keeps its layers (docs\ARCHITECTURE.md); new violations fail before anything is compiled.
& (Join-Path $projectRoot 'build\check-layers.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# An empty catch block must say why it is safe to ignore the error.
& (Join-Path $projectRoot 'build\check-catches.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
. (Join-Path $projectRoot 'build\csharp-compiler.ps1')
$compilerPath = Get-CSharpCompiler
# The native M8 song reader is optional for the tests: without Rust the tests that need it are skipped.
# The tests find the DLL where the build leaves it (native\m8-reader\target\release).
& (Join-Path $projectRoot 'build\build-native.ps1') -Optional | Out-Null
$coreSourceRoot = Join-Path $projectRoot 'src'
$cliSources = @(Get-ChildItem -LiteralPath (Join-Path $coreSourceRoot 'Cli') -Filter '*.cs' | ForEach-Object FullName)
# The core library is built beside the test program, which uses it the way the real programs do.
$corePath = Join-Path $testBuild 'GearBackup.Core.dll'
$coreSources = @(Get-ChildItem -LiteralPath $coreSourceRoot -Filter '*.cs' -Recurse | Where-Object { $cliSources -notcontains $_.FullName } | ForEach-Object FullName)
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /target:library "/out:$corePath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll $coreSources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# The command-line front end is compiled into the test program, without the toast notifications that need Windows metadata.
$testSources = @($cliSources | Where-Object { (Split-Path $_ -Leaf) -ne 'Notifications.cs' })
# Every test file except the GUI tests, which need Windows Forms (run-gui-tests.ps1).
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -notlike 'Gui*' | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'GearBackup.Tests.exe'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /main:TestRunner "/out:$runnerPath" "/r:$corePath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll $testSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
$runnerArguments = @(); foreach ($text in $Filter) { $runnerArguments += @('--filter', $text) }; if ($List) { $runnerArguments += '--list' }
try { & $runnerPath @runnerArguments; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult
