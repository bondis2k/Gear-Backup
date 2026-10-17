# -Filter TEXT (more than one allowed) runs only the tests whose name contains it; -List shows the tests.
param([string[]]$Filter = @(), [switch]$List, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = if (!$BuildDirectory) { Join-Path $PSScriptRoot '.build\gui' }
    elseif ([IO.Path]::IsPathRooted($BuildDirectory)) { [IO.Path]::GetFullPath($BuildDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $projectRoot $BuildDirectory)) }
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
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
$guiSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'gui') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'GearBackup.GuiTests.exe'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /target:exe /main:GuiTestRunner "/win32manifest:$projectRoot\gui\app.manifest" "/out:$runnerPath" "/r:$corePath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/resource:$projectRoot\assets\Backup.ico,Backup.ico" "/resource:$projectRoot\assets\Backup-256.png,Backup-256.png" "/resource:$projectRoot\assets\M8-256.png,M8-256.png" "/resource:$projectRoot\assets\OP1Field-256.png,OP1Field-256.png" "/resource:$projectRoot\assets\OPXY-256.png,OPXY-256.png" "/resource:$projectRoot\assets\TrackerMini-256.png,TrackerMini-256.png" "/resource:$projectRoot\assets\X100VI-256.png,X100VI-256.png" $testSources $guiSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
$runnerArguments = @(); foreach ($text in $Filter) { $runnerArguments += @('--filter', $text) }; if ($List) { $runnerArguments += '--list' }
try { & $runnerPath @runnerArguments; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult
